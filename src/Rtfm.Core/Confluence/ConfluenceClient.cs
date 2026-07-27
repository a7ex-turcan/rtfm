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
            VersionNumber: GetInt(version, "number") ?? 0,
            VersionWhen: ConfluenceDate.Parse(GetString(version, "when")),
            VersionBy: GetString(GetObject(version, "by"), "displayName"),
            BodyHtml: bodyHtml,
            ChildPageIds: ParseChildIds(root),
            LinkedPageIds: ParseLinkedIds(bodyHtml, id));
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
    /// (GET only), paginated via <c>start</c>/<c>limit</c> up to
    /// <paramref name="max"/>. This is how a seed's scope is resolved (§2.17
    /// step 2) — <c>ancestor = {id}</c> flattens a page/folder subtree through
    /// sub-folders in one query, <c>space = "{key}"</c> enumerates a whole space.
    /// Failures return what was gathered rather than throwing.
    /// </summary>
    public async Task<IReadOnlyList<(string Id, string Title)>> SearchPagesAsync(string cql, int max, CancellationToken cancellationToken = default)
    {
        var results = new List<(string, string)>();
        var start = 0;

        try
        {
            while (results.Count < max)
            {
                var limit = Math.Min(100, max - results.Count);
                var url = $"rest/api/content/search?cql={Uri.EscapeDataString(cql)}&limit={limit}&start={start}";

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
                    if (GetString(item, "id") is { } id)
                    {
                        results.Add((id, GetString(item, "title") ?? id));
                    }
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
            // Partial result — the caller crawls what it got.
        }

        return results;
    }

    /// <summary>
    /// The live <c>version.number</c> for each of <paramref name="ids"/> (GET
    /// only), via batched <c>id in (…)</c> CQL searches with <c>expand=version</c>
    /// — the watch loop's change-detection probe (§2.17 step 3). A page that comes
    /// back missing (deleted or no longer visible) is simply absent from the
    /// result. Failures return what was gathered.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int?>> FetchVersionsAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        var versions = new Dictionary<string, int?>(StringComparer.Ordinal);
        var ordered = ids.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        try
        {
            for (var start = 0; start < ordered.Count; start += 100)
            {
                var batch = ordered.Skip(start).Take(100);
                var cql = "id in (" + string.Join(",", batch) + ")";
                var url = $"rest/api/content/search?cql={Uri.EscapeDataString(cql)}&expand=version&limit=100";

                using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                foreach (var item in GetArray(doc.RootElement, "results"))
                {
                    if (GetString(item, "id") is { } id)
                    {
                        versions[id] = GetInt(GetObject(item, "version"), "number");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Partial result — the caller compares only what it received.
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
