using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Rtfm.Core.Jira;

/// <summary>
/// A read-only Jira Cloud REST v3 client (§2.16). <b>It issues <c>GET</c> and
/// nothing else</b> — there is no create/update/delete method on it, by
/// construction: RTFM is a retrieval tool and must never mutate a team's
/// tracker. Auth is HTTP Basic <c>email:token</c>, the token env-expanded from
/// the <see cref="JiraConfig"/> at construction and never exposed after.
/// </summary>
public sealed class JiraClient : IDisposable
{
    // Fields pulled per issue. `renderedFields` (via expand) gives description +
    // comment bodies as HTML; the raw fields carry machine dates + authors.
    // `comment` is appended only for full-fidelity (seed) fetches — deeper
    // tickets are indexed description-only (§2.16), so their comments are never
    // pulled.
    private const string BaseIssueFields =
        "summary,description,issuelinks,status,issuetype,created,updated,reporter,assignee,priority,labels,parent,subtasks";

    private readonly HttpClient _http;

    // Latched off by the first *globally* structural dev-status failure — 401
    // (bad credential) or 404/410 (endpoint withdrawn). Without it, a
    // 150-ticket crawl would repeat a doomed call 150 times and emit 150
    // identical warnings. Transient failures (timeouts, 5xx) do not latch, and
    // neither does 403: that is a per-project permission, not a global one
    // (see GetDevStatusAsync).
    private bool _devStatusAvailable = true;

    public JiraClient(JiraConfig config)
        : this(config, CreateHandler())
    {
    }

    /// <summary>Test seam: inject a handler (e.g. a stub) instead of the real network.</summary>
    internal JiraClient(JiraConfig config, HttpMessageHandler handler)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Email}:{config.ResolveToken()}"));
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(JiraConfig.NormalizeBaseUrl(config.BaseUrl) + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("rtfm-jira/1.0");
    }

    /// <summary>
    /// Verifies the credentials with a read-only <c>GET /myself</c>. Returns the
    /// account display name on success, or an error message on failure — never
    /// throws, so <c>rtfm jira config</c> can report cleanly.
    /// </summary>
    public async Task<(bool Ok, string? DisplayName, string? Error)> VerifyAuthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync("rest/api/3/myself", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return (false, null, "authentication failed — check the email and API token");
            }

            if (!response.IsSuccessStatusCode)
            {
                return (false, null, $"Jira returned HTTP {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return (true, GetString(doc.RootElement, "displayName"), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, null, ex.Message);
        }
    }

    /// <summary>
    /// Pulls one issue by key (case-insensitive) with rendered bodies. GET only.
    /// <paramref name="includeComments"/> gates the comment field: the seed pulls
    /// full fidelity, deeper tickets are description-only (§2.16).
    /// </summary>
    /// <exception cref="JiraException">Auth failure, not-found, or any non-success HTTP status.</exception>
    public async Task<JiraIssue> FetchIssueAsync(string key, bool includeComments = true, CancellationToken cancellationToken = default)
    {
        var canonical = key.Trim().ToUpperInvariant();
        var fields = includeComments ? BaseIssueFields + ",comment" : BaseIssueFields;
        var url = $"rest/api/3/issue/{Uri.EscapeDataString(canonical)}?expand=renderedFields&fields={fields}";

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new JiraException($"could not reach Jira for {canonical}: {ex.Message}");
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    throw new JiraException($"issue {canonical} not found (or you lack permission to view it)");
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    throw new JiraException($"not authorized to read {canonical} — check the token and its permissions");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new JiraException($"Jira returned HTTP {(int)response.StatusCode} for {canonical}");
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                return Parse(doc.RootElement, canonical);
            }
            catch (JsonException ex)
            {
                throw new JiraException($"could not parse Jira's response for {canonical}: {ex.Message}");
            }
        }
    }

    internal static JiraIssue Parse(JsonElement root, string key)
    {
        var fields = root.GetProperty("fields");
        var rendered = root.TryGetProperty("renderedFields", out var rf) ? rf : default;

        var descriptionHtml = rendered.ValueKind == JsonValueKind.Object ? GetString(rendered, "description") : null;

        return new JiraIssue(
            Key: GetString(root, "key") ?? key,
            Summary: GetString(fields, "summary") ?? key,
            Id: GetString(root, "id"),
            Status: GetString(GetObject(fields, "status"), "name"),
            IssueType: GetString(GetObject(fields, "issuetype"), "name"),
            Reporter: GetString(GetObject(fields, "reporter"), "displayName"),
            Assignee: GetString(GetObject(fields, "assignee"), "displayName"),
            Priority: GetString(GetObject(fields, "priority"), "name"),
            Labels: GetStringArray(fields, "labels"),
            Created: JiraDate.Parse(GetString(fields, "created")),
            Updated: JiraDate.Parse(GetString(fields, "updated")),
            DescriptionHtml: descriptionHtml,
            Comments: ParseComments(fields, rendered),
            Links: ParseLinks(fields),
            ParentKey: GetString(GetObject(fields, "parent"), "key"),
            Subtasks: ParseSubtaskKeys(fields));
    }

    private static IReadOnlyList<string> ParseSubtaskKeys(JsonElement fields)
    {
        var keys = new List<string>();
        foreach (var sub in GetArray(fields, "subtasks"))
        {
            if (GetString(sub, "key") is { } k)
            {
                keys.Add(k);
            }
        }

        return keys;
    }

    // Machine dates + authors come from the raw comment field; rendered bodies
    // are joined in by comment id (renderedFields dates are display-formatted).
    private static IReadOnlyList<JiraComment> ParseComments(JsonElement fields, JsonElement rendered)
    {
        var rawComments = GetArray(GetObject(fields, "comment"), "comments");
        if (rawComments.Count == 0)
        {
            return [];
        }

        var htmlById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rc in GetArray(rendered.ValueKind == JsonValueKind.Object ? GetObject(rendered, "comment") : default, "comments"))
        {
            var id = GetString(rc, "id");
            var html = GetString(rc, "body");
            if (id is not null && html is not null)
            {
                htmlById[id] = html;
            }
        }

        var comments = new List<JiraComment>(rawComments.Count);
        foreach (var raw in rawComments)
        {
            var id = GetString(raw, "id");
            var html = id is not null && htmlById.TryGetValue(id, out var h) ? h : string.Empty;
            comments.Add(new JiraComment(
                Author: GetString(GetObject(raw, "author"), "displayName") ?? "Unknown",
                Created: JiraDate.Parse(GetString(raw, "created")),
                BodyHtml: html));
        }

        return comments;
    }

    private static IReadOnlyList<JiraLink> ParseLinks(JsonElement fields)
    {
        var links = new List<JiraLink>();
        foreach (var link in GetArray(fields, "issuelinks"))
        {
            var type = GetObject(link, "type");
            if (link.TryGetProperty("outwardIssue", out var outward) && outward.ValueKind == JsonValueKind.Object)
            {
                if (GetString(outward, "key") is { } k)
                {
                    links.Add(new JiraLink(GetString(type, "outward") ?? "relates to", k, Outward: true));
                }
            }
            else if (link.TryGetProperty("inwardIssue", out var inward) && inward.ValueKind == JsonValueKind.Object)
            {
                if (GetString(inward, "key") is { } k)
                {
                    links.Add(new JiraLink(GetString(type, "inward") ?? "relates to", k, Outward: false));
                }
            }
        }

        return links;
    }

    /// <summary>
    /// Pulls the ticket's <b>Development</b> panel — branches, pull requests, and
    /// commits from the linked source host (GET only, like everything here).
    ///
    /// <para><b>This rides an undocumented endpoint.</b> <c>/rest/dev-status/</c>
    /// is the internal API behind Jira's own Development panel: it is not part of
    /// Atlassian's published REST surface and carries no compatibility promise,
    /// unlike every other call in this client. It is therefore strictly
    /// best-effort — <b>this method never throws</b>. Any failure warns and
    /// yields <see cref="JiraDevelopment.None"/>, so a ticket still indexes
    /// (minus its development data) if the endpoint changes or disappears.</para>
    /// </summary>
    /// <param name="issueId">The <em>numeric</em> issue id (<see cref="JiraIssue.Id"/>) — this API does not accept ticket keys.</param>
    /// <param name="warn">Receives a human-readable reason when the data could not be read. Core stays host-agnostic (see this project's CLAUDE.md), so the caller decides where it goes.</param>
    /// <param name="issueKey">
    /// The ticket key (e.g. <c>AEXP-19</c>), used only to name the project in a
    /// permission warning — the API itself keys on the numeric id.
    /// </param>
    public async Task<JiraDevelopment> FetchDevelopmentAsync(
        string? issueId,
        string? issueKey = null,
        Action<string>? warn = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(issueId) || !_devStatusAvailable)
        {
            return JiraDevelopment.None;
        }

        var id = Uri.EscapeDataString(issueId.Trim());

        // The summary call is the cheap gate: it reports which categories have
        // anything at all and which source hosts they live on, so a ticket with
        // no development data costs exactly one request and no detail calls.
        using var summary = await GetDevStatusAsync($"rest/dev-status/latest/issue/summary?issueId={id}", issueKey, warn, cancellationToken).ConfigureAwait(false);
        if (summary is null)
        {
            return JiraDevelopment.None;
        }

        // Parsing is guarded as well as fetching: this payload has no published
        // schema, so an unfamiliar shape must degrade to "no development data"
        // rather than break ingestion of an otherwise-fine ticket.
        try
        {
            var fetches = PlanDevelopmentFetches(summary.RootElement);
            if (fetches.Count == 0)
            {
                return JiraDevelopment.None;
            }

            var pullRequests = new List<JiraPullRequest>();
            var branches = new List<JiraBranch>();
            var commits = new List<JiraCommit>();

            foreach (var (applicationType, dataType) in fetches)
            {
                var url = $"rest/dev-status/1.0/issue/detail?issueId={id}"
                    + $"&applicationType={Uri.EscapeDataString(applicationType)}&dataType={dataType}";

                using var detail = await GetDevStatusAsync(url, issueKey, warn, cancellationToken).ConfigureAwait(false);
                if (detail is not null)
                {
                    MergeDevelopmentDetail(detail.RootElement, pullRequests, branches, commits);
                }
            }

            return new JiraDevelopment(pullRequests, branches, commits);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or FormatException)
        {
            warn?.Invoke($"could not read development data: unexpected dev-status response shape ({ex.Message}) — indexing the ticket without it.");
            return JiraDevelopment.None;
        }
    }

    /// <summary>
    /// Which <c>(applicationType, dataType)</c> detail calls the summary says are
    /// worth making. Only two dataTypes are ever requested: <c>pullrequest</c>
    /// (which returns branches alongside the PRs — <c>dataType=branch</c> was
    /// measured to return a byte-identical payload) and <c>repository</c> (which
    /// carries the commits). A category with a zero count is not fetched.
    /// </summary>
    internal static IReadOnlyList<(string ApplicationType, string DataType)> PlanDevelopmentFetches(JsonElement root)
    {
        var summary = GetObject(root, "summary");
        if (summary.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var planned = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Plan(string category, string dataType)
        {
            // A category can be absent entirely (a ticket with no branches has no
            // "branch" node at all), and GetObject yields an *Undefined* element
            // for that — on which TryGetProperty throws rather than returning
            // false. Check the kind before reaching in.
            var node = GetObject(summary, category);
            var overall = GetObject(node, "overall");
            var instances = GetObject(node, "byInstanceType");
            if (overall.ValueKind != JsonValueKind.Object || instances.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (!overall.TryGetProperty("count", out var count)
                || count.ValueKind != JsonValueKind.Number
                || count.GetInt32() <= 0)
            {
                return;
            }

            foreach (var instance in instances.EnumerateObject())
            {
                if (seen.Add($"{instance.Name}|{dataType}"))
                {
                    planned.Add((instance.Name, dataType));
                }
            }
        }

        Plan("pullrequest", "pullrequest");
        Plan("branch", "pullrequest");
        Plan("repository", "repository");
        return planned;
    }

    /// <summary>
    /// Folds one detail response into the accumulating lists, de-duplicating
    /// across instance types (the same PR can surface under more than one
    /// category). Shape: <c>detail[].{pullRequests,branches,repositories[].commits}</c>.
    /// </summary>
    internal static void MergeDevelopmentDetail(
        JsonElement root,
        List<JiraPullRequest> pullRequests,
        List<JiraBranch> branches,
        List<JiraCommit> commits)
    {
        foreach (var block in GetArray(root, "detail"))
        {
            foreach (var pr in GetArray(block, "pullRequests"))
            {
                var name = GetString(pr, "name") ?? GetString(pr, "id") ?? "(untitled pull request)";
                var url = GetString(pr, "url");
                if (pullRequests.Any(existing => existing.Url == url && existing.Name == name))
                {
                    continue;
                }

                var reviewers = new List<JiraReviewer>();
                foreach (var reviewer in GetArray(pr, "reviewers"))
                {
                    if (GetString(reviewer, "name") is { } reviewerName)
                    {
                        reviewers.Add(new JiraReviewer(
                            reviewerName,
                            reviewer.TryGetProperty("approved", out var approved) && approved.ValueKind == JsonValueKind.True));
                    }
                }

                pullRequests.Add(new JiraPullRequest(
                    Name: name,
                    Status: GetString(pr, "status"),
                    Author: GetString(GetObject(pr, "author"), "name"),
                    SourceBranch: GetString(GetObject(pr, "source"), "branch"),
                    DestinationBranch: GetString(GetObject(pr, "destination"), "branch"),
                    Repository: GetString(pr, "repositoryName"),
                    Url: url,
                    LastUpdate: JiraDate.Parse(GetString(pr, "lastUpdate")),
                    Reviewers: reviewers));
            }

            foreach (var branch in GetArray(block, "branches"))
            {
                var name = GetString(branch, "name");
                var repository = GetString(GetObject(branch, "repository"), "name");
                if (name is null || branches.Any(existing => existing.Name == name && existing.Repository == repository))
                {
                    continue;
                }

                branches.Add(new JiraBranch(name, repository, GetString(branch, "url")));
            }

            foreach (var repository in GetArray(block, "repositories"))
            {
                var repositoryName = GetString(repository, "name");
                foreach (var commit in GetArray(repository, "commits"))
                {
                    var commitId = GetString(commit, "id");
                    if (commitId is null || commits.Any(existing => existing.Id == commitId))
                    {
                        continue;
                    }

                    commits.Add(new JiraCommit(
                        Id: commitId,
                        DisplayId: GetString(commit, "displayId") ?? commitId[..Math.Min(7, commitId.Length)],
                        Author: GetString(GetObject(commit, "author"), "name"),
                        AuthoredAt: JiraDate.Parse(GetString(commit, "authorTimestamp")),
                        Message: GetString(commit, "message"),
                        Url: GetString(commit, "url"),
                        Repository: repositoryName,
                        Merge: commit.TryGetProperty("merge", out var merge) && merge.ValueKind == JsonValueKind.True));
                }
            }
        }
    }

    /// <summary>
    /// One best-effort dev-status GET. Returns null on any failure, having warned.
    ///
    /// <para><b>403 is local; 401/404/410 are global — and conflating them was a
    /// bug.</b> <c>View Development Tools</c> is a <em>per-project</em> Jira
    /// permission, so a 403 means "not this project", not "not this endpoint".
    /// The original cut latched the whole feature off on any of these, so a
    /// single cross-project link into a project the account can't see stripped
    /// the Development panel from every ticket crawled after it — silently, and
    /// while reporting the panel as globally unavailable. Only a bad credential
    /// (401) or a withdrawn endpoint (404/410) latches now; a 403 warns once per
    /// project and the crawl carries on.</para>
    ///
    /// <para>Deliberately <b>not</b> skipping the rest of a forbidden project's
    /// tickets: Jira also has issue-level security, so a 403 on one ticket does
    /// not prove the project is uniformly closed. One wasted summary GET per
    /// affected ticket is far cheaper than wrongly skipping readable ones.</para>
    /// </summary>
    private async Task<JsonDocument?> GetDevStatusAsync(string url, string? issueKey, Action<string>? warn, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                _devStatusAvailable = false;
                warn?.Invoke(
                    $"Jira development data is unavailable (HTTP {(int)response.StatusCode} from the dev-status endpoint) — "
                    + "tickets will be indexed without their Development panel.");
                return null;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                // Scoped to the project, and phrased so it reads as one project's
                // permission gap rather than a dead feature. The message is
                // identical for every ticket in that project, so the caller's
                // de-duplication collapses it to one line per project.
                var scope = ProjectOf(issueKey) is { } project ? $"project {project}" : "this ticket";
                warn?.Invoke(
                    $"Jira development data for {scope} is not visible to this account (HTTP 403 — the "
                    + "\"View Development Tools\" permission). Its tickets index without the Development panel; "
                    + "other projects are unaffected.");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                warn?.Invoke($"could not read development data (HTTP {(int)response.StatusCode}) — indexing the ticket without it.");
                return null;
            }

            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                warn?.Invoke($"could not read development data: {ex.Message} — indexing the ticket without it.");
            }

            return null;
        }
    }

    /// <summary>
    /// Runs a JQL query and returns matching issue keys (GET only), paginated via
    /// <c>nextPageToken</c> up to <paramref name="max"/>. Used to discover an
    /// epic's children (<c>parent = KEY</c>) during traversal and — later — the
    /// watch delta set. Failures return what was collected so far rather than
    /// throwing, so one bad search never sinks a crawl.
    /// </summary>
    public async Task<IReadOnlyList<string>> SearchIssueKeysAsync(string jql, int max, CancellationToken cancellationToken = default)
    {
        var keys = new List<string>();
        string? pageToken = null;

        try
        {
            do
            {
                var pageSize = Math.Min(100, max - keys.Count);
                var url = $"rest/api/3/search/jql?fields=key&maxResults={pageSize}&jql={Uri.EscapeDataString(jql)}"
                    + (pageToken is null ? string.Empty : $"&nextPageToken={Uri.EscapeDataString(pageToken)}");

                using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    break;
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                var root = doc.RootElement;
                foreach (var issue in GetArray(root, "issues"))
                {
                    if (GetString(issue, "key") is { } k)
                    {
                        keys.Add(k);
                    }
                }

                pageToken = root.TryGetProperty("isLast", out var last) && last.ValueKind == JsonValueKind.False
                    ? GetString(root, "nextPageToken")
                    : null;
            }
            while (pageToken is not null && keys.Count < max);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Return the partial result — the caller treats missing neighbours as
            // "none", never as a hard failure.
        }

        return keys;
    }

    /// <summary>
    /// The live <c>updated</c> stamp for each of <paramref name="keys"/> (GET
    /// only), via batched <c>key in (…)</c> searches — the watch loop's
    /// change-detection probe (§2.16 step 3). A key that comes back missing
    /// (deleted or no longer visible) is simply absent from the result. Failures
    /// return what was gathered; the caller treats a missing stamp as "no change".
    /// </summary>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset?>> FetchUpdatedAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        var stamps = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
        var ordered = keys.Select(k => k.Trim().ToUpperInvariant()).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        try
        {
            for (var start = 0; start < ordered.Count; start += 100)
            {
                var batch = ordered.Skip(start).Take(100);
                var jql = "key in (" + string.Join(",", batch.Select(k => $"\"{k}\"")) + ")";
                string? pageToken = null;

                do
                {
                    var url = $"rest/api/3/search/jql?fields=updated&maxResults=100&jql={Uri.EscapeDataString(jql)}"
                        + (pageToken is null ? string.Empty : $"&nextPageToken={Uri.EscapeDataString(pageToken)}");

                    using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        break;
                    }

                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                    var root = doc.RootElement;
                    foreach (var issue in GetArray(root, "issues"))
                    {
                        if (GetString(issue, "key") is { } k)
                        {
                            stamps[k] = JiraDate.Parse(GetString(GetObject(issue, "fields"), "updated"));
                        }
                    }

                    pageToken = root.TryGetProperty("isLast", out var last) && last.ValueKind == JsonValueKind.False
                        ? GetString(root, "nextPageToken")
                        : null;
                }
                while (pageToken is not null);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Partial result — the caller compares only the stamps it received.
        }

        return stamps;
    }

    /// <summary>
    /// All project keys visible to the credential (GET only), for validating
    /// text-mention edges.
    ///
    /// <para><b>Do not add <c>&amp;keys=true</c>.</b> On
    /// <c>/rest/api/3/project/search</c>, <c>keys</c> is a <em>filter by project
    /// key</em>, not a "return only the keys" projection — passing <c>true</c>
    /// filtered the search down to a project literally keyed <c>true</c> and
    /// returned an empty set, every time. Because an empty set makes
    /// mention-following a silent no-op (the crawler only follows a mention
    /// whose prefix is a known key), <c>--follow-mentions</c> did nothing at all
    /// rather than failing visibly. Paging here <em>is</em> ordinary
    /// <c>startAt</c>/<c>isLast</c> — unlike Confluence's content search
    /// (§2.17), this endpoint honours the offset.</para>
    /// </summary>
    public async Task<IReadOnlySet<string>> FetchProjectKeysAsync(CancellationToken cancellationToken = default)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string startAt = "0";

        try
        {
            while (true)
            {
                using var response = await _http.GetAsync($"rest/api/3/project/search?maxResults=100&startAt={startAt}", cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    break;
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                var root = doc.RootElement;
                foreach (var project in GetArray(root, "values"))
                {
                    if (GetString(project, "key") is { } k)
                    {
                        keys.Add(k);
                    }
                }

                var isLast = !root.TryGetProperty("isLast", out var last) || last.ValueKind != JsonValueKind.False;
                if (isLast)
                {
                    break;
                }

                startAt = (int.Parse(startAt) + 100).ToString();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException)
        {
            // Best-effort: an empty set makes mention-following a no-op, never a crash.
        }

        return keys;
    }

    /// <summary>The project part of a ticket key (<c>AEXP-19</c> → <c>AEXP</c>), or null if it isn't key-shaped.</summary>
    internal static string? ProjectOf(string? issueKey)
    {
        if (string.IsNullOrWhiteSpace(issueKey))
        {
            return null;
        }

        var dash = issueKey.IndexOf('-');
        return dash > 0 ? issueKey[..dash].Trim().ToUpperInvariant() : null;
    }

    private static HttpClientHandler CreateHandler() => new() { AutomaticDecompression = DecompressionMethods.All };

    private static JsonElement GetObject(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string? GetString(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<JsonElement> GetArray(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var items = new List<JsonElement>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            items.Add(item);
        }

        return items;
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement parent, string name)
    {
        var result = new List<string>();
        foreach (var item in GetArray(parent, name))
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
            {
                result.Add(s);
            }
        }

        return result;
    }

    public void Dispose() => _http.Dispose();
}
