using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rtfm.Core.AzureDevOps;

/// <summary>
/// A read-only Azure DevOps Services REST client (§2.18). <b>Every request it
/// makes is a <c>GET</c></b>, by construction: the single private
/// <see cref="GetAsync"/> is the only place a request is built, and it can build
/// nothing else. RTFM is a retrieval tool and must never mutate a team's
/// tracker — the §2.16 invariant, kept the same way.
///
/// <para>That rules out WIQL (<c>POST …/wiql</c>) — a read-only query language,
/// but one Azure DevOps only accepts over POST. Nothing here needs it: a crawl
/// seeds from ids and walks the relations each work item already carries
/// (children included, unlike Jira's epic children), and the watch probe is a
/// batched <c>GET …/workitems?ids=…</c>.</para>
///
/// <para>Auth comes from an <see cref="IAdoCredential"/>: an <c>az</c>-borrowed
/// bearer token (refreshed once on a 401 — tokens live about an hour, so a long
/// crawl can outlive one) or a PAT. A 429 is honoured via <c>Retry-After</c>
/// before giving up.</para>
/// </summary>
public sealed partial class AdoClient : IDisposable
{
    internal const string ApiVersion = "7.1";

    // The comments API has only ever shipped as a preview version; the un-suffixed
    // "7.1" is rejected with HTTP 400 (measured).
    internal const string CommentsApiVersion = "7.1-preview.4";

    /// <summary>Pull requests resolved per work item (newest-linked first); the rest are counted, not fetched.</summary>
    internal const int MaxPullRequestsFetched = 20;

    /// <summary>Commits resolved per work item (newest-linked first); the rest are counted, not fetched.</summary>
    internal const int MaxCommitsFetched = 20;

    private const int MaxCommentsFetched = 500;
    private const int RevisionBatchSize = 200;
    private const int DevelopmentParallelism = 6;
    private const int MaxThrottleRetries = 3;

    // Rendered multi-line fields, in display order. Anything else the response's
    // multilineFieldsFormat names (custom rich-text fields) follows, by label.
    private static readonly (string Field, string Label)[] KnownRichFields =
    [
        ("System.Description", "Description"),
        ("Microsoft.VSTS.Common.AcceptanceCriteria", "Acceptance criteria"),
        ("Microsoft.VSTS.TCM.ReproSteps", "Repro steps"),
        ("Microsoft.VSTS.TCM.SystemInfo", "System info"),
    ];

    private readonly HttpClient _http;
    private readonly IAdoCredential _credential;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    // Code (Read) is granted per project, so a 401/403 from one project's Git
    // API says nothing about another's — the Jira 1.11.1 lesson (§2.16). Latch
    // per project, warn once per project.
    private readonly ConcurrentDictionary<string, byte> _gitBlockedProjects = new(StringComparer.OrdinalIgnoreCase);

    public AdoClient(AdoConfig config)
        : this(config, CreateCredential(config), CreateHandler())
    {
    }

    /// <summary>Test seam: inject the credential, the handler, and the back-off delay.</summary>
    internal AdoClient(AdoConfig config, IAdoCredential credential, HttpMessageHandler handler, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var organization = AdoOrganization.Parse(config.OrganizationUrl);
        OrganizationUrl = organization.Url;
        Organization = organization.Name;
        _credential = credential;
        _delay = delay ?? Task.Delay;
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(organization.Url + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("rtfm-ado/1.0");
    }

    /// <summary>The organization's REST base, e.g. <c>https://dev.azure.com/contoso</c>.</summary>
    public string OrganizationUrl { get; }

    /// <summary>The organization name, used in source keys (<see cref="AdoSource.Key"/>).</summary>
    public string Organization { get; }

    /// <summary>The credential a config asks for. A PAT reference is expanded here — a missing variable throws <see cref="InvalidDataException"/>.</summary>
    public static IAdoCredential CreateCredential(AdoConfig config)
        => config.UsesPat ? new PatCredential(config.ResolveToken()) : new AzCliCredential(config.Resource, config.Tenant);

    /// <summary>
    /// Verifies the credential with a read-only <c>GET _apis/connectionData</c>.
    /// Returns the authenticated identity on success, or an error — never throws.
    /// </summary>
    public async Task<(bool Ok, string? DisplayName, string? Error)> VerifyAuthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await GetAsync("_apis/connectionData", cancellationToken).ConfigureAwait(false);
            if (IsAuthFailure(response))
            {
                return (false, null, "authentication failed — the credential was rejected by Azure DevOps");
            }

            if (!response.IsSuccessStatusCode)
            {
                return (false, null, $"Azure DevOps returned HTTP {(int)response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var user = GetObject(doc.RootElement, "authenticatedUser");
            return (true, GetString(user, "providerDisplayName") ?? GetString(user, "customDisplayName"), null);
        }
        catch (AdoException ex)
        {
            return (false, null, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, null, ex.Message);
        }
    }

    /// <summary>
    /// Pulls one work item with its fields and relations (<c>$expand=all</c>).
    /// <paramref name="includeComments"/> also pulls the discussion — a separate,
    /// project-scoped call, skipped when <c>System.CommentCount</c> is 0. A
    /// comment-fetch failure warns and keeps the item.
    /// </summary>
    /// <exception cref="AdoException">Auth failure, not-found, or any non-success HTTP status for the item itself.</exception>
    public async Task<AdoWorkItem> FetchWorkItemAsync(
        int id,
        bool includeComments = true,
        Action<string>? warn = null,
        CancellationToken cancellationToken = default)
    {
        string body;
        try
        {
            using var response = await GetAsync($"_apis/wit/workitems/{id}?$expand=all&api-version={ApiVersion}", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new AdoException($"work item #{id} not found (or you lack permission to view it)");
            }

            if (IsAuthFailure(response))
            {
                throw new AdoException($"not authorized to read work item #{id} — check the credential (Work Items: Read)");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AdoException($"Azure DevOps returned HTTP {(int)response.StatusCode} for work item #{id}");
            }

            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new AdoException($"could not reach Azure DevOps for work item #{id}: {ex.Message}");
        }

        AdoWorkItem item;
        try
        {
            using var doc = JsonDocument.Parse(body);
            item = Parse(doc.RootElement, OrganizationUrl);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new AdoException($"could not parse Azure DevOps' response for work item #{id}: {ex.Message}");
        }

        if (includeComments && item.CommentCount > 0 && item.TeamProject is { } project)
        {
            item = item with { Comments = await FetchCommentsAsync(project, item.Id, warn, cancellationToken).ConfigureAwait(false) };
        }

        return item;
    }

    /// <summary>A work item's comments, oldest first, paged by continuation token. Best-effort: failures warn and return what was read.</summary>
    private async Task<IReadOnlyList<AdoComment>> FetchCommentsAsync(string project, int id, Action<string>? warn, CancellationToken cancellationToken)
    {
        var comments = new List<AdoComment>();
        string? continuation = null;

        try
        {
            do
            {
                var url = $"{Uri.EscapeDataString(project)}/_apis/wit/workItems/{id}/comments?api-version={CommentsApiVersion}&$top=200&order=asc"
                    + (continuation is null ? string.Empty : $"&continuationToken={Uri.EscapeDataString(continuation)}");

                using var response = await GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || IsAuthFailure(response))
                {
                    warn?.Invoke($"could not read the comments on #{id} (HTTP {(int)response.StatusCode}) — indexing it without them.");
                    break;
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                comments.AddRange(ParseComments(doc.RootElement));
                continuation = GetString(doc.RootElement, "continuationToken");
            }
            while (continuation is not null && comments.Count < MaxCommentsFetched);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            warn?.Invoke($"could not read the comments on #{id}: {ex.Message} — indexing it without them.");
        }

        return comments;
    }

    /// <summary>
    /// The live <c>System.Rev</c> of each id — the watch loop's change probe —
    /// via batched <c>GET …/workitems?ids=…</c> (200 per call, the API's limit).
    /// <c>errorPolicy=omit</c> keeps one deleted or hidden id from failing its
    /// whole batch; such ids are simply absent. Failures return what was read.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, int>> FetchRevisionsAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default)
    {
        var revisions = new Dictionary<int, int>();
        var ordered = ids.Where(i => i > 0).Distinct().Order().ToList();

        try
        {
            for (var start = 0; start < ordered.Count; start += RevisionBatchSize)
            {
                var batch = string.Join(",", ordered.Skip(start).Take(RevisionBatchSize).Select(i => i.ToString(CultureInfo.InvariantCulture)));
                using var response = await GetAsync(
                    $"_apis/wit/workitems?ids={batch}&fields=System.Rev&errorPolicy=omit&api-version={ApiVersion}", cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || IsAuthFailure(response))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                foreach (var item in GetArray(doc.RootElement, "value"))
                {
                    if (GetInt(item, "id") is { } id && GetInt(item, "rev") is { } rev)
                    {
                        revisions[id] = rev;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            // Partial result — the caller compares only the revisions it received.
        }

        return revisions;
    }

    /// <summary>
    /// Resolves the pull requests, commits, and branches linked to a work item
    /// through the Git REST API. <b>Never throws</b>: development data is an
    /// enrichment, so any failure warns (via <paramref name="warn"/> — Core stays
    /// host-agnostic) and the item still indexes.
    ///
    /// <para>Cost is bounded per item: at most <see cref="MaxPullRequestsFetched"/>
    /// PRs (one GET each, a few in parallel) and <see cref="MaxCommitsFetched"/>
    /// commits (one batched GET per repository), newest-linked first; the
    /// remainder is counted in the result and stated in the rendered output. A
    /// busy story can carry dozens of PR and commit links — the real org's first
    /// probe had 70 — so uncapped resolution would dominate a crawl.</para>
    /// </summary>
    public async Task<AdoDevelopment> FetchDevelopmentAsync(AdoWorkItem item, Action<string>? warn = null, CancellationToken cancellationToken = default)
    {
        if (item.Artifacts.Count == 0)
        {
            return AdoDevelopment.None;
        }

        try
        {
            var plan = PlanDevelopment(item.Artifacts);
            var projectLabel = item.TeamProject;
            var unreadable = 0;

            var pullRequests = new ConcurrentBag<AdoPullRequest>();
            await Parallel.ForEachAsync(
                plan.PullRequests,
                new ParallelOptions { MaxDegreeOfParallelism = DevelopmentParallelism, CancellationToken = cancellationToken },
                async (link, token) =>
                {
                    var pr = await FetchPullRequestAsync(link, projectLabel, warn, token).ConfigureAwait(false);
                    if (pr is null)
                    {
                        Interlocked.Increment(ref unreadable);
                    }
                    else
                    {
                        pullRequests.Add(pr);
                    }
                }).ConfigureAwait(false);

            var commits = new List<AdoCommit>();
            foreach (var group in plan.Commits.GroupBy(c => (c.ProjectId, c.RepositoryId)))
            {
                var wanted = group.Select(c => c.Value).ToList();
                var found = await FetchCommitsAsync(group.Key.ProjectId, group.Key.RepositoryId, wanted, projectLabel, warn, cancellationToken).ConfigureAwait(false);
                commits.AddRange(found);
                unreadable += wanted.Count - found.Count;
            }

            // Branch links carry only ids; borrow repository names from what the
            // PR and commit fetches already returned rather than spending a call.
            var repositoryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pr in pullRequests)
            {
                if (pr.Repository is { } name && plan.PullRequests.FirstOrDefault(l => l.Value == pr.Id.ToString(CultureInfo.InvariantCulture)) is { } link)
                {
                    repositoryNames.TryAdd(link.RepositoryId, name);
                }
            }

            var branches = plan.Branches
                .Select(b => new AdoBranch(b.Value, repositoryNames.TryGetValue(b.RepositoryId, out var repo) ? repo : null))
                .ToList();

            return new AdoDevelopment(
                PullRequests: pullRequests.OrderByDescending(p => p.Created ?? DateTimeOffset.MinValue).ThenByDescending(p => p.Id).ToList(),
                Commits: commits.OrderByDescending(c => c.AuthoredAt ?? DateTimeOffset.MinValue).ToList(),
                Branches: branches,
                OmittedPullRequests: plan.OmittedPullRequests,
                OmittedCommits: plan.OmittedCommits,
                Unreadable: unreadable);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or InvalidOperationException or FormatException or AdoException
                                   && !cancellationToken.IsCancellationRequested)
        {
            warn?.Invoke($"could not read the development links of #{item.Id}: {ex.Message} — indexing it without them.");
            return AdoDevelopment.None;
        }
    }

    /// <summary>The capped, de-duplicated fetch plan for a work item's artifact links (newest-linked first). Pure.</summary>
    internal static DevelopmentPlan PlanDevelopment(IReadOnlyList<AdoArtifactLink> artifacts)
    {
        static List<AdoArtifactLink> Distinct(IEnumerable<AdoArtifactLink> links)
            => links
                .GroupBy(l => (l.RepositoryId.ToLowerInvariant(), l.Value.ToLowerInvariant()))
                .Select(g => g.OrderByDescending(l => l.LinkedAt ?? DateTimeOffset.MinValue).First())
                .OrderByDescending(l => l.LinkedAt ?? DateTimeOffset.MinValue)
                .ToList();

        var prs = Distinct(artifacts.Where(a => a.Kind == AdoArtifactKind.PullRequest));
        var commits = Distinct(artifacts.Where(a => a.Kind == AdoArtifactKind.Commit));
        var branches = Distinct(artifacts.Where(a => a.Kind == AdoArtifactKind.Branch));

        return new DevelopmentPlan(
            PullRequests: prs.Take(MaxPullRequestsFetched).ToList(),
            Commits: commits.Take(MaxCommitsFetched).ToList(),
            Branches: branches,
            OmittedPullRequests: Math.Max(0, prs.Count - MaxPullRequestsFetched),
            OmittedCommits: Math.Max(0, commits.Count - MaxCommitsFetched));
    }

    internal sealed record DevelopmentPlan(
        IReadOnlyList<AdoArtifactLink> PullRequests,
        IReadOnlyList<AdoArtifactLink> Commits,
        IReadOnlyList<AdoArtifactLink> Branches,
        int OmittedPullRequests,
        int OmittedCommits);

    private async Task<AdoPullRequest?> FetchPullRequestAsync(AdoArtifactLink link, string? projectLabel, Action<string>? warn, CancellationToken cancellationToken)
    {
        using var doc = await GetGitJsonAsync(
            $"{Uri.EscapeDataString(link.ProjectId)}/_apis/git/pullrequests/{Uri.EscapeDataString(link.Value)}?api-version={ApiVersion}",
            link.ProjectId, projectLabel, warn, cancellationToken).ConfigureAwait(false);
        return doc is null ? null : ParsePullRequest(doc.RootElement, OrganizationUrl);
    }

    private async Task<IReadOnlyList<AdoCommit>> FetchCommitsAsync(
        string projectId,
        string repositoryId,
        IReadOnlyList<string> shas,
        string? projectLabel,
        Action<string>? warn,
        CancellationToken cancellationToken)
    {
        var baseUrl = $"{Uri.EscapeDataString(projectId)}/_apis/git/repositories/{Uri.EscapeDataString(repositoryId)}/commits";
        var query = string.Join("&", shas.Select(s => "searchCriteria.ids=" + Uri.EscapeDataString(s)));

        using var doc = await GetGitJsonAsync($"{baseUrl}?{query}&api-version={ApiVersion}", projectId, projectLabel, warn, cancellationToken).ConfigureAwait(false);
        if (doc is null)
        {
            return [];
        }

        var commits = new List<AdoCommit>();
        foreach (var element in GetArray(doc.RootElement, "value"))
        {
            var commit = ParseCommit(element);
            if (commit is null)
            {
                continue;
            }

            // List responses truncate long messages; the full text is the point,
            // so re-read just those commits individually.
            if (element.TryGetProperty("commentTruncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
            {
                using var full = await GetGitJsonAsync(
                    $"{baseUrl}/{Uri.EscapeDataString(commit.Id)}?api-version={ApiVersion}", projectId, projectLabel, warn, cancellationToken).ConfigureAwait(false);
                if (full is not null && ParseCommit(full.RootElement) is { } complete)
                {
                    commit = complete;
                }
            }

            commits.Add(commit);
        }

        return commits;
    }

    /// <summary>
    /// One best-effort Git API GET. Returns null on any failure. A 401/403 latches
    /// <em>that project</em> off with one warning (Code: Read is per project); a
    /// 404 is one unreadable artifact (deleted repository or PR) and stays quiet —
    /// the caller counts it, and the count is rendered.
    /// </summary>
    private async Task<JsonDocument?> GetGitJsonAsync(string url, string projectId, string? projectLabel, Action<string>? warn, CancellationToken cancellationToken)
    {
        if (_gitBlockedProjects.ContainsKey(projectId))
        {
            return null;
        }

        try
        {
            return await GetGitJsonCoreAsync(url, projectId, projectLabel, warn, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            // One transient failure costs one artifact (counted unreadable), not
            // the whole Development section.
            warn?.Invoke($"could not read development data: {ex.Message} — indexing without it.");
            return null;
        }
    }

    private async Task<JsonDocument?> GetGitJsonCoreAsync(string url, string projectId, string? projectLabel, Action<string>? warn, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(url, cancellationToken).ConfigureAwait(false);

        if (IsAuthFailure(response) || response.StatusCode == HttpStatusCode.Forbidden)
        {
            if (_gitBlockedProjects.TryAdd(projectId, 0))
            {
                var scope = projectLabel is null ? $"project {projectId}" : $"project {projectLabel}";
                warn?.Invoke(
                    $"Azure DevOps code data in {scope} is not visible to this credential (HTTP {(int)response.StatusCode} — needs Code: Read). "
                    + "Its work items index without pull request and commit details; other projects are unaffected.");
            }

            return null;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            warn?.Invoke($"could not read development data (HTTP {(int)response.StatusCode}) — indexing without it.");
            return null;
        }

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The one place a request is made — and it is always a <c>GET</c>. A 401 on a
    /// refreshable credential (an expired <c>az</c> token) is retried once with a
    /// fresh one; a 429 (or a 503 that names a <c>Retry-After</c>) is retried up to
    /// <see cref="MaxThrottleRetries"/> times after the server's requested delay.
    /// </summary>
    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellationToken)
    {
        var refreshed = false;
        var throttled = 0;

        while (true)
        {
            var header = await _credential.GetHeaderAsync(refreshed, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = header;

            var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (IsAuthFailure(response) && _credential.CanRefresh && !refreshed)
            {
                response.Dispose();
                refreshed = true;
                continue;
            }

            var throttledStatus = response.StatusCode == HttpStatusCode.TooManyRequests
                || (response.StatusCode == HttpStatusCode.ServiceUnavailable && response.Headers.RetryAfter is not null);
            if (throttledStatus && throttled < MaxThrottleRetries)
            {
                var wait = RetryDelay(response.Headers.RetryAfter, DateTimeOffset.UtcNow);
                response.Dispose();
                throttled++;
                await _delay(wait, cancellationToken).ConfigureAwait(false);
                continue;
            }

            return response;
        }
    }

    /// <summary>The server's requested back-off, clamped to 1–60 s (5 s when it names none). Pure.</summary>
    internal static TimeSpan RetryDelay(RetryConditionHeaderValue? retryAfter, DateTimeOffset now)
    {
        var delay = retryAfter?.Delta
            ?? (retryAfter?.Date is { } date ? date - now : (TimeSpan?)null)
            ?? TimeSpan.FromSeconds(5);
        return TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 1, 60));
    }

    /// <summary>
    /// A rejected credential. Besides 401, Azure DevOps answers a bad PAT with
    /// <b>HTTP 203</b> and an HTML sign-in page — a "success" status that would
    /// otherwise reach the JSON parser and fail confusingly.
    /// </summary>
    private static bool IsAuthFailure(HttpResponseMessage response)
        => response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NonAuthoritativeInformation
           || (response.IsSuccessStatusCode
               && response.Content.Headers.ContentType?.MediaType is "text/html");

    // ---- parsing (internal static — tested directly) ----

    internal static AdoWorkItem Parse(JsonElement root, string organizationUrl)
    {
        var id = GetInt(root, "id") ?? throw new KeyNotFoundException("id");
        var fields = GetObject(root, "fields");
        var formats = GetObject(root, "multilineFieldsFormat");

        var links = new List<AdoLink>();
        var artifacts = new List<AdoArtifactLink>();
        var hyperlinks = new List<AdoHyperlink>();
        int? parentFromRelations = null;

        foreach (var relation in GetArray(root, "relations"))
        {
            var rel = GetString(relation, "rel") ?? string.Empty;
            var url = GetString(relation, "url") ?? string.Empty;
            var attributes = GetObject(relation, "attributes");

            if (string.Equals(rel, "ArtifactLink", StringComparison.OrdinalIgnoreCase))
            {
                var linkedAt = ParseDate(GetString(attributes, "resourceCreatedDate")) ?? ParseDate(GetString(attributes, "authorizedDate"));
                if (ParseArtifact(url, linkedAt) is { } artifact)
                {
                    artifacts.Add(artifact);
                }
            }
            else if (string.Equals(rel, "Hyperlink", StringComparison.OrdinalIgnoreCase))
            {
                if (url.Length > 0)
                {
                    hyperlinks.Add(new AdoHyperlink(url, NullIfBlank(GetString(attributes, "comment"))));
                }
            }
            else if (WorkItemUrl().Match(url) is { Success: true } match
                     && int.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var linkedId))
            {
                links.Add(new AdoLink(GetString(attributes, "name") ?? HumanizeRelation(rel), linkedId));
                if (rel.EndsWith("Hierarchy-Reverse", StringComparison.OrdinalIgnoreCase))
                {
                    parentFromRelations = linkedId;
                }
            }
        }

        var teamProject = GetString(fields, "System.TeamProject");
        var webUrl = GetString(GetObject(GetObject(root, "_links"), "html"), "href")
            ?? (teamProject is null ? null : $"{organizationUrl}/{Uri.EscapeDataString(teamProject)}/_workitems/edit/{id}");

        return new AdoWorkItem(
            Id: id,
            Rev: GetInt(root, "rev") ?? GetInt(fields, "System.Rev") ?? 0,
            Title: GetString(fields, "System.Title") ?? $"Work item {id}",
            WorkItemType: GetString(fields, "System.WorkItemType"),
            State: GetString(fields, "System.State"),
            Reason: GetString(fields, "System.Reason"),
            AssignedTo: GetIdentity(fields, "System.AssignedTo"),
            CreatedBy: GetIdentity(fields, "System.CreatedBy"),
            Created: ParseDate(GetString(fields, "System.CreatedDate")),
            Changed: ParseDate(GetString(fields, "System.ChangedDate")),
            Priority: GetScalar(fields, "Microsoft.VSTS.Common.Priority"),
            AreaPath: GetString(fields, "System.AreaPath"),
            IterationPath: GetString(fields, "System.IterationPath"),
            Tags: (GetString(fields, "System.Tags") ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            TeamProject: teamProject,
            CommentCount: GetInt(fields, "System.CommentCount") ?? 0,
            ParentId: GetInt(fields, "System.Parent") ?? parentFromRelations,
            RichFields: ParseRichFields(fields, formats),
            Links: links,
            Artifacts: artifacts,
            Hyperlinks: hyperlinks,
            Comments: [],
            WebUrl: webUrl);
    }

    private static IReadOnlyList<AdoRichField> ParseRichFields(JsonElement fields, JsonElement formats)
    {
        bool IsMarkdown(string field)
            => string.Equals(GetString(formats, field), "markdown", StringComparison.OrdinalIgnoreCase);

        var result = new List<AdoRichField>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (field, label) in KnownRichFields)
        {
            seen.Add(field);
            if (NullIfBlank(GetString(fields, field)) is { } content)
            {
                result.Add(new AdoRichField(field, label, content, IsMarkdown(field)));
            }
        }

        // Custom rich-text fields the organization defined. System.History is the
        // latest comment echoed as a field — the comments API is the real source.
        var custom = new List<AdoRichField>();
        if (formats.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in formats.EnumerateObject())
            {
                if (seen.Contains(property.Name) || string.Equals(property.Name, "System.History", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (NullIfBlank(GetString(fields, property.Name)) is { } content)
                {
                    custom.Add(new AdoRichField(property.Name, HumanizeField(property.Name), content, IsMarkdown(property.Name)));
                }
            }
        }

        result.AddRange(custom.OrderBy(f => f.Label, StringComparer.OrdinalIgnoreCase));
        return result;
    }

    internal static IReadOnlyList<AdoComment> ParseComments(JsonElement root)
    {
        var comments = new List<AdoComment>();
        foreach (var comment in GetArray(root, "comments"))
        {
            if (comment.TryGetProperty("isDeleted", out var deleted) && deleted.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            comments.Add(new AdoComment(
                Author: GetString(GetObject(comment, "createdBy"), "displayName") ?? "Unknown",
                Created: ParseDate(GetString(comment, "createdDate")),
                Text: GetString(comment, "text") ?? string.Empty,
                IsMarkdown: string.Equals(GetString(comment, "format"), "markdown", StringComparison.OrdinalIgnoreCase)));
        }

        return comments;
    }

    /// <summary>
    /// Parses a <c>vstfs:///Git/{Kind}/{project}%2F{repo}%2F{value}</c> artifact
    /// URI. The separator escape's case varies between links on the same item
    /// (<c>%2f</c> and <c>%2F</c> both occur — measured), so it is decoded rather
    /// than matched. Non-Git artifacts (builds, wiki pages) and tag refs return null.
    /// </summary>
    internal static AdoArtifactLink? ParseArtifact(string uri, DateTimeOffset? linkedAt = null)
    {
        const string prefix = "vstfs:///Git/";
        if (!uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = uri[prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0)
        {
            return null;
        }

        var kind = rest[..slash];
        var parts = Uri.UnescapeDataString(rest[(slash + 1)..]).Split('/', 3);
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace))
        {
            return null;
        }

        var (project, repository, value) = (parts[0], parts[1], parts[2]);

        if (string.Equals(kind, "PullRequestId", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _)
                ? new AdoArtifactLink(AdoArtifactKind.PullRequest, project, repository, value, linkedAt)
                : null;
        }

        if (string.Equals(kind, "Commit", StringComparison.OrdinalIgnoreCase))
        {
            return new AdoArtifactLink(AdoArtifactKind.Commit, project, repository, value.ToLowerInvariant(), linkedAt);
        }

        // Refs are "GB<branch>" (branch) or "GT<tag>"; only branches are kept. A
        // branch with '/' in its name may arrive escaped a second time.
        if (string.Equals(kind, "Ref", StringComparison.OrdinalIgnoreCase) && value.StartsWith("GB", StringComparison.Ordinal))
        {
            var branch = Uri.UnescapeDataString(value[2..]);
            return branch.Length > 0 ? new AdoArtifactLink(AdoArtifactKind.Branch, project, repository, branch, linkedAt) : null;
        }

        return null;
    }

    internal static AdoPullRequest? ParsePullRequest(JsonElement pr, string organizationUrl)
    {
        if (GetInt(pr, "pullRequestId") is not { } id)
        {
            return null;
        }

        var repository = GetObject(pr, "repository");
        var repositoryName = GetString(repository, "name");
        var projectName = GetString(GetObject(repository, "project"), "name");

        var reviewers = new List<AdoReviewer>();
        foreach (var reviewer in GetArray(pr, "reviewers"))
        {
            if (GetString(reviewer, "displayName") is { } name)
            {
                reviewers.Add(new AdoReviewer(name, GetInt(reviewer, "vote") ?? 0));
            }
        }

        return new AdoPullRequest(
            Id: id,
            Title: GetString(pr, "title") ?? $"Pull request {id}",
            Status: GetString(pr, "status"),
            IsDraft: pr.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True,
            Author: GetString(GetObject(pr, "createdBy"), "displayName"),
            SourceBranch: ShortRef(GetString(pr, "sourceRefName")),
            TargetBranch: ShortRef(GetString(pr, "targetRefName")),
            Repository: repositoryName,
            Url: repositoryName is null || projectName is null
                ? null
                : $"{organizationUrl}/{Uri.EscapeDataString(projectName)}/_git/{Uri.EscapeDataString(repositoryName)}/pullrequest/{id}",
            Created: ParseDate(GetString(pr, "creationDate")),
            Closed: ParseDate(GetString(pr, "closedDate")),
            Description: NullIfBlank(GetString(pr, "description")),
            Reviewers: reviewers);
    }

    internal static AdoCommit? ParseCommit(JsonElement commit)
    {
        if (GetString(commit, "commitId") is not { } id)
        {
            return null;
        }

        var author = GetObject(commit, "author");
        var remoteUrl = GetString(commit, "remoteUrl");
        var repository = remoteUrl is not null && RepositoryInRemoteUrl().Match(remoteUrl) is { Success: true } m
            ? Uri.UnescapeDataString(m.Groups["repo"].Value)
            : null;

        return new AdoCommit(
            Id: id,
            Author: GetString(author, "name"),
            AuthoredAt: ParseDate(GetString(author, "date")),
            Message: GetString(commit, "comment"),
            Repository: repository,
            Url: remoteUrl);
    }

    private static string? ShortRef(string? reference)
        => reference is null ? null
            : reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..]
            : reference;

    /// <summary><c>System.LinkTypes.Dependency-Forward</c> → <c>Dependency forward</c> — only used when the API omits <c>attributes.name</c>.</summary>
    internal static string HumanizeRelation(string rel)
    {
        var last = rel.Split('.').LastOrDefault() ?? rel;
        return last.Replace('-', ' ').Trim() is { Length: > 0 } text ? char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant() : "Related";
    }

    /// <summary><c>Custom.TechnicalNotes</c> → <c>Technical notes</c>.</summary>
    internal static string HumanizeField(string referenceName)
    {
        var last = referenceName.Split('.').LastOrDefault() ?? referenceName;
        var spaced = CamelBoundary().Replace(last.Replace('_', ' '), " ").Trim();
        return spaced.Length == 0 ? referenceName : char.ToUpperInvariant(spaced[0]) + spaced[1..].ToLowerInvariant();
    }

    private static DateTimeOffset? ParseDate(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // Identity fields are objects ({displayName, uniqueName, …}) on current API
    // versions; older payloads use "Display Name <user@domain>".
    private static string? GetIdentity(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            return GetString(value, "displayName") ?? GetString(value, "uniqueName");
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text)
        {
            return null;
        }

        var angle = text.IndexOf('<');
        return angle > 0 ? text[..angle].Trim() : text;
    }

    private static string? GetScalar(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static int? GetInt(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

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
            if (item.ValueKind != JsonValueKind.Null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private static HttpClientHandler CreateHandler() => new() { AutomaticDecompression = DecompressionMethods.All };

    // A work item reference: …/_apis/wit/workItems/{id} (any casing).
    [GeneratedRegex(@"/_apis/wit/workItems/(?<id>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex WorkItemUrl();

    [GeneratedRegex(@"/_git/(?<repo>[^/]+)/commit/", RegexOptions.IgnoreCase)]
    private static partial Regex RepositoryInRemoteUrl();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex CamelBoundary();

    public void Dispose() => _http.Dispose();
}
