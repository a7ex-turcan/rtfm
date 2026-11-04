using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Rtfm.Core.AzureDevOps;

namespace Rtfm.Core.Tests.AzureDevOps;

/// <summary>
/// A route-table HTTP stub for <see cref="AdoClient"/>: each request's path +
/// query is matched against registered handlers (first match wins), and every
/// request is recorded — including its method, so tests can pin the GET-only
/// invariant. Unmatched requests return 404.
/// </summary>
internal sealed class AdoStubHandler : HttpMessageHandler
{
    private readonly List<(Func<string, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    public IEnumerable<string> Paths => Requests.Select(r => Uri.UnescapeDataString(r.RequestUri!.PathAndQuery));

    public AdoStubHandler On(Func<string, bool> match, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes.Add((match, respond));
        return this;
    }

    public AdoStubHandler OnJson(Func<string, bool> match, string json)
        => On(match, _ => Json(json));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var path = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
        foreach (var (match, respond) in _routes)
        {
            if (match(path))
            {
                return Task.FromResult(respond(request));
            }
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>A static credential that counts how often it was asked for, and whether a refresh was requested.</summary>
internal sealed class CountingCredential(bool canRefresh = false) : IAdoCredential
{
    public int Calls { get; private set; }

    public int Refreshes { get; private set; }

    public bool CanRefresh => canRefresh;

    public Task<AuthenticationHeaderValue> GetHeaderAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        Calls++;
        if (forceRefresh)
        {
            Refreshes++;
        }

        return Task.FromResult(new AuthenticationHeaderValue("Bearer", forceRefresh ? "fresh" : "stale"));
    }
}

internal static class AdoFixtures
{
    public const string Org = "https://dev.azure.com/Contoso";

    public static AdoClient Client(AdoStubHandler handler, IAdoCredential? credential = null, List<TimeSpan>? delays = null)
        => new(
            new AdoConfig(Org),
            credential ?? new CountingCredential(),
            handler,
            (wait, _) =>
            {
                delays?.Add(wait);
                return Task.CompletedTask;
            });

    /// <summary>A minimal <c>$expand=all</c> work item payload.</summary>
    public static string WorkItem(
        int id,
        string title = "A story",
        int? parent = null,
        int[]? children = null,
        int[]? related = null,
        int commentCount = 0,
        string description = "<div>Body</div>",
        string relationsExtra = "")
    {
        var relations = new List<string>();
        if (parent is { } p)
        {
            relations.Add(Relation("System.LinkTypes.Hierarchy-Reverse", p, "Parent"));
        }

        foreach (var c in children ?? [])
        {
            relations.Add(Relation("System.LinkTypes.Hierarchy-Forward", c, "Child"));
        }

        foreach (var r in related ?? [])
        {
            relations.Add(Relation("System.LinkTypes.Related", r, "Related"));
        }

        if (relationsExtra.Length > 0)
        {
            relations.Add(relationsExtra);
        }

        var parentField = parent is { } pp ? $",\"System.Parent\":{pp}" : "";
        return $$"""
            {"id":{{id}},"rev":7,
             "fields":{"System.Title":"{{title}}","System.WorkItemType":"User Story","System.State":"Active",
                       "System.TeamProject":"Team Alpha","System.CommentCount":{{commentCount}},
                       "System.ChangedDate":"2026-10-01T10:00:00Z","System.Description":"{{description}}"{{parentField}}},
             "multilineFieldsFormat":{"System.Description":"html"},
             "relations":[{{string.Join(",", relations)}}]}
            """;
    }

    private static string Relation(string rel, int id, string name)
        => "{\"rel\":\"" + rel + "\",\"url\":\"" + Org + "/_apis/wit/workItems/" + id + "\",\"attributes\":{\"name\":\"" + name + "\"}}";

    public static bool IsItem(string path, int id) => path.Contains($"/_apis/wit/workitems/{id}?", StringComparison.OrdinalIgnoreCase);
}
