using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rtfm.Core.Confluence;

/// <summary>
/// A read-only Confluence Cloud REST v1 client (§2.17). <b>GET only</b> — like
/// <see cref="Jira.JiraClient"/>, no create/update/delete path exists, by
/// construction. Auth is HTTP Basic <c>email:token</c>, the token env-expanded
/// from the <see cref="ConfluenceConfig"/> at construction.
/// </summary>
public sealed partial class ConfluenceClient : IDisposable
{
    // Rendered body + hierarchy + version, in one call.
    private const string PageExpand = "body.view,space,version,ancestors,children.page";

    private readonly HttpClient _http;

    public ConfluenceClient(ConfluenceConfig config)
        : this(config, CreateHandler())
    {
    }

    /// <summary>Test seam: inject a handler (e.g. a stub) instead of the real network.</summary>
    internal ConfluenceClient(ConfluenceConfig config, HttpMessageHandler handler)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Email}:{config.ResolveToken()}"));
        _http = new HttpClient(handler, disposeHandler: true)
        {
            // The Confluence REST API lives under /wiki on the same host.
            BaseAddress = new Uri(ConfluenceConfig.NormalizeBaseUrl(config.BaseUrl) + "/wiki/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("rtfm-confluence/1.0");
    }

    /// <summary>Verifies credentials with a read-only <c>GET /user/current</c>. Never throws.</summary>
    public async Task<(bool Ok, string? DisplayName, string? Error)> VerifyAuthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync("rest/api/user/current", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return (false, null, "authentication failed — check the email and API token");
            }

            if (!response.IsSuccessStatusCode)
            {
                return (false, null, $"Confluence returned HTTP {(int)response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return (true, GetString(doc.RootElement, "displayName") ?? GetString(doc.RootElement, "publicName"), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, null, ex.Message);
        }
    }

    /// <summary>Pulls one page by id with its rendered body, hierarchy, and version. GET only.</summary>
    /// <exception cref="ConfluenceException">Auth failure, not-found, or any non-success HTTP status.</exception>
    public async Task<ConfluencePage> FetchPageAsync(string pageId, CancellationToken cancellationToken = default)
    {
        var id = pageId.Trim();
        var url = $"rest/api/content/{Uri.EscapeDataString(id)}?expand={PageExpand}";

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ConfluenceException($"could not reach Confluence for page {id}: {ex.Message}");
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    throw new ConfluenceException($"page {id} not found (or you lack permission to view it)");
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    throw new ConfluenceException($"not authorized to read page {id} — check the token and its permissions");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ConfluenceException($"Confluence returned HTTP {(int)response.StatusCode} for page {id}");
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                return Parse(doc.RootElement, id);
            }
            catch (JsonException ex)
            {
                throw new ConfluenceException($"could not parse Confluence's response for page {id}: {ex.Message}");
            }
        }
    }

    internal static ConfluencePage Parse(JsonElement root, string id)
    {
        var version = GetObject(root, "version");
        var bodyHtml = GetString(GetObject(GetObject(root, "body"), "view"), "value") ?? string.Empty;

        return new ConfluencePage(
            Id: GetString(root, "id") ?? id,
            Title: GetString(root, "title") ?? id,
            Type: GetString(root, "type") ?? "page",
            SpaceKey: GetString(GetObject(root, "space"), "key"),
            Ancestors: ParseAncestorTitles(root),
            AncestorIds: ParseAncestorIds(root),
            VersionNumber: GetInt(version, "number") ?? 0,
            VersionWhen: ConfluenceDate.Parse(GetString(version, "when")),
            VersionBy: GetString(GetObject(version, "by"), "displayName"),
            BodyHtml: bodyHtml,
            ChildPageIds: ParseChildIds(root),
            LinkedPageIds: ParseLinkedIds(bodyHtml, id));
    }

    /// <summary>Ancestor ids, root first — the join key for a page hierarchy view.</summary>
    private static IReadOnlyList<string> ParseAncestorIds(JsonElement root)
    {
        var ids = new List<string>();
        foreach (var ancestor in GetArray(root, "ancestors"))
        {
            if (GetString(ancestor, "id") is { } id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static IReadOnlyList<string> ParseAncestorTitles(JsonElement root)
    {
        var titles = new List<string>();
        foreach (var ancestor in GetArray(root, "ancestors"))
        {
            if (GetString(ancestor, "title") is { } t)
            {
                titles.Add(t);
            }
        }

        return titles;
    }

    private static IReadOnlyList<string> ParseChildIds(JsonElement root)
    {
        var results = GetArray(GetObject(GetObject(root, "children"), "page"), "results");
        var ids = new List<string>();
        foreach (var child in results)
        {
            if (GetString(child, "id") is { } cid)
            {
                ids.Add(cid);
            }
        }

        return ids;
    }

    // In-body links to other Confluence pages are hrefs of the form
    // ".../pages/{id}/…" in the rendered HTML. Excludes the page's own id.
    private static IReadOnlyList<string> ParseLinkedIds(string bodyHtml, string selfId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in PageLinkPattern().Matches(bodyHtml))
        {
            var id = m.Groups["id"].Value;
            if (!string.Equals(id, selfId, StringComparison.Ordinal))
            {
                ids.Add(id);
            }
        }

        return ids.ToList();
    }

    /// <summary>
    /// Runs a CQL content search and returns matching page id + title summaries
    /// (GET only), up to <paramref name="max"/>. This is how a seed's scope is
    /// resolved (§2.17 step 2) — <c>ancestor = {id}</c> flattens a page/folder
    /// subtree through sub-folders in one query, <c>space = "{key}"</c>
    /// enumerates a whole space.
    ///
    /// <para><b>Cursor paging, never <c>start</c>.</b> Confluence Cloud's
    /// <c>/rest/api/content/search</c> <b>silently ignores</b> the <c>start</c>
    /// parameter: every request returns the same first page. Offset paging here
    /// therefore capped every scope at 100 pages while looking like it had read
    /// them all — the termination check never fired (each page came back exactly
    /// <c>limit</c> long), so it also burned a request per 100 of the budget
    /// re-reading the same ids. The only paging that works is following
    /// <c>_links.next</c>, which carries an opaque cursor.</para>
    /// </summary>
    /// <param name="warn">
    /// Invoked when enumeration ends <em>early</em> — an HTTP failure or a
    /// transport/JSON error partway through. The list is then a prefix of the
    /// real scope, and the caller must say so rather than treat it as complete
    /// (§5, "no silent caps").
    /// </param>
    public Task<IReadOnlyList<(string Id, string Title)>> SearchPagesAsync(
        string cql,
        int max,
        Action<string>? warn = null,
        CancellationToken cancellationToken = default)
        => SearchByCursorAsync(
            $"rest/api/content/search?cql={Uri.EscapeDataString(cql)}&limit=100",
            max,
            item => GetString(item, "id") is { } id ? (id, GetString(item, "title") ?? id) : default((string, string)?),
            warn,
            cancellationToken);

    /// <summary>
    /// Walks a v1 collection endpoint by following <c>_links.next</c> until it is
    /// absent (or <paramref name="max"/> items are collected), projecting each
    /// result with <paramref name="select"/>. <c>next</c> is a host-relative path
    /// to be resolved against <c>_links.base</c> (which already ends in
    /// <c>/wiki</c>), so it is combined rather than handed to the client's own
    /// base address.
    /// </summary>
    private async Task<IReadOnlyList<T>> SearchByCursorAsync<T>(
        string firstUrl,
        int max,
        Func<JsonElement, T?> select,
        Action<string>? warn,
        CancellationToken cancellationToken)
        where T : struct
    {
        var results = new List<T>();
        string? url = firstUrl;
        var requests = 0;

        // A cursor walk terminates when `next` disappears, but a server that
        // kept handing one back forever would spin. The backstop is deliberately
        // far above any real walk (`max` items at even 10 per page) *and* it
        // warns when it fires — a tighter cap sized to the expected page count
        // would silently truncate against a server returning short pages, which
        // is precisely the bug this method exists to fix.
        var maxRequests = Math.Max(20, (max / 10) + 2);

        try
        {
            while (url is not null && results.Count < max && requests < maxRequests)
            {
                using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                requests++;

                if (!response.IsSuccessStatusCode)
                {
                    warn?.Invoke($"Confluence returned HTTP {(int)response.StatusCode} after {results.Count} result(s) — the listing is incomplete.");
                    url = null;   // already reported; don't warn twice below
                    break;
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

                foreach (var item in GetArray(doc.RootElement, "results"))
                {
                    if (results.Count >= max)
                    {
                        break;
                    }

                    if (select(item) is { } projected)
                    {
                        results.Add(projected);
                    }
                }

                url = NextUrl(doc.RootElement);
            }

            // Stopped with more to read, and not because the caller's own limit
            // was reached: say so rather than return a quiet prefix.
            if (url is not null && results.Count < max)
            {
                warn?.Invoke($"stopped after {requests} requests with more Confluence results pending — the listing is incomplete.");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                warn?.Invoke($"could not finish reading Confluence results after {results.Count} — {ex.Message}. The listing is incomplete.");
            }
        }

        return results;
    }

    /// <summary>
    /// The absolute URL of the next page, or null at the end of the collection.
    /// <c>_links.next</c> is relative (e.g. <c>/rest/api/content/search?…cursor=…</c>)
    /// and belongs to <c>_links.base</c>; falling back to trimming a leading
    /// <c>/wiki</c> keeps it working against the client's own base address if a
    /// response ever omits <c>base</c>.
    /// </summary>
    private static string? NextUrl(JsonElement root)
    {
        var links = GetObject(root, "_links");
        if (GetString(links, "next") is not { Length: > 0 } next)
        {
            return null;
        }

        if (GetString(links, "base") is { Length: > 0 } baseUrl)
        {
            return baseUrl.TrimEnd('/') + "/" + next.TrimStart('/');
        }

        var relative = next.TrimStart('/');
        return relative.StartsWith("wiki/", StringComparison.OrdinalIgnoreCase) ? relative[5..] : relative;
    }

    // Comment expand: rendered body, the inline anchor (originalSelection) and
    // resolution status, and the original author/date from history.
    private const string CommentExpand = "body.view,extensions.inlineProperties,extensions.resolution,history.createdBy,history.createdDate,version";

    /// <summary>Hard cap on comments pulled per page — a page with a runaway thread can't stall a crawl.</summary>
    private const int MaxCommentsPerPage = 200;

    /// <summary>
    /// Pulls a page's comments — both inline (text-anchored) and footer (general)
    /// — GET only, paginated. Inline comments carry the highlighted passage they
    /// annotate (<see cref="ConfluenceComment.AnchorText"/>). A failure returns
    /// what was gathered; comments are additive, never a reason to fail the page.
    /// </summary>
    public async Task<IReadOnlyList<ConfluenceComment>> FetchCommentsAsync(string pageId, CancellationToken cancellationToken = default)
    {
        var comments = new List<ConfluenceComment>();
        var start = 0;

        try
        {
            while (comments.Count < MaxCommentsPerPage)
            {
                var limit = Math.Min(100, MaxCommentsPerPage - comments.Count);
                var url = $"rest/api/content/{Uri.EscapeDataString(pageId.Trim())}/child/comment?expand={CommentExpand}&limit={limit}&start={start}";

                using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    break;
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                var page = GetArray(doc.RootElement, "results");
                if (page.Count == 0)
                {
                    break;
                }

                foreach (var item in page)
                {
                    comments.Add(ParseComment(item));
                }

                if (page.Count < limit)
                {
                    break;
                }

                start += limit;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Best-effort — return the comments gathered so far.
        }

        return comments;
    }

    internal static ConfluenceComment ParseComment(JsonElement root)
    {
        var extensions = GetObject(root, "extensions");
        var history = GetObject(root, "history");
        var version = GetObject(root, "version");

        return new ConfluenceComment(
            Id: GetString(root, "id") ?? string.Empty,
            Author: GetString(GetObject(history, "createdBy"), "displayName")
                ?? GetString(GetObject(version, "by"), "displayName") ?? "Unknown",
            Created: ConfluenceDate.Parse(GetString(history, "createdDate") ?? GetString(version, "when")),
            Location: GetString(extensions, "location") ?? "footer",
            AnchorText: GetString(GetObject(extensions, "inlineProperties"), "originalSelection"),
            Resolution: GetString(GetObject(extensions, "resolution"), "status"),
            BodyHtml: GetString(GetObject(GetObject(root, "body"), "view"), "value") ?? string.Empty);
    }

    /// <summary>
    /// The live <c>version.number</c> for each of <paramref name="ids"/> (GET
    /// only), via batched <c>id in (…)</c> CQL searches with <c>expand=version</c>
    /// — the watch loop's change-detection probe (§2.17 step 3). A page that comes
    /// back missing (deleted or no longer visible) is simply absent from the
    /// result. Failures return what was gathered.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int?>> FetchVersionsAsync(
        IReadOnlyCollection<string> ids,
        Action<string>? warn = null,
        CancellationToken cancellationToken = default)
    {
        var versions = new Dictionary<string, int?>(StringComparer.Ordinal);
        var ordered = ids.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        for (var start = 0; start < ordered.Count; start += 100)
        {
            var batch = ordered.Skip(start).Take(100).ToList();
            var cql = "id in (" + string.Join(",", batch) + ")";
            var url = $"rest/api/content/search?cql={Uri.EscapeDataString(cql)}&expand=version&limit=100";

            // The batching is client-side (≤100 ids per query), so one response
            // normally covers a batch — but follow the cursor regardless. A short
            // page would otherwise drop those pages' versions, and the watch loop
            // would then never notice them change: the same silent-truncation
            // family as the scope-resolution bug, on the same endpoint.
            var found = await SearchByCursorAsync(
                url,
                batch.Count,
                item => GetString(item, "id") is { } id
                    ? (id, GetInt(GetObject(item, "version"), "number"))
                    : default((string, int?)?),
                warn,
                cancellationToken).ConfigureAwait(false);

            foreach (var (id, version) in found)
            {
                versions[id] = version;
            }
        }

        return versions;
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

    private static int? GetInt(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
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

    [GeneratedRegex(@"/pages/(?<id>\d+)")]
    private static partial Regex PageLinkPattern();

    public void Dispose() => _http.Dispose();
}
