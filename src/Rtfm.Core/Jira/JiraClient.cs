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

    /// <summary>All project keys visible to the credential (GET only), for validating text-mention edges.</summary>
    public async Task<IReadOnlySet<string>> FetchProjectKeysAsync(CancellationToken cancellationToken = default)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string startAt = "0";

        try
        {
            while (true)
            {
                using var response = await _http.GetAsync($"rest/api/3/project/search?maxResults=100&startAt={startAt}&keys=true", cancellationToken).ConfigureAwait(false);
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
