using System.Net;
using System.Web;
using Rtfm.Core.Confluence;

namespace Rtfm.Core.Tests.Confluence;

/// <summary>
/// Several seeds (e.g. <c>--space PR --space PH</c>) crawl as <b>one run</b>:
/// shared visited-set, shared budget, one result. That sharing is why the
/// crawler takes a list rather than the caller looping — looping outside would
/// fetch an overlapping page twice and turn <c>--max-pages</c> into a per-seed
/// allowance, silently multiplying the ceiling by the number of seeds.
/// </summary>
public class ConfluenceMultiSeedTests
{
    private static readonly DateTimeOffset When = new(2026, 8, 7, 0, 0, 0, TimeSpan.Zero);

    private static ConfluenceCrawler NewCrawler(out ConfluenceClient client, out SpaceHandler handler)
    {
        handler = new SpaceHandler();
        client = new ConfluenceClient(new ConfluenceConfig("https://x.atlassian.net", "me@x.com", "tok"), handler);
        return new ConfluenceCrawler(client, new ConfluenceDocumentRenderer());
    }

    private static ConfluenceSeed Space(string key) => new(ConfluenceSeedKind.Space, key);

    [Fact]
    public async Task Two_spaces_index_in_one_run()
    {
        var crawler = NewCrawler(out var client, out _);
        using var _c = client;

        var result = await crawler.CrawlAsync(
            [Space("PR"), Space("PH")], "https://x.atlassian.net", When,
            new ConfluenceCrawlOptions(MaxDepth: 0, MaxPages: 50));

        // PR = {1,2}, PH = {3,4} → all four, one run, one result.
        Assert.Equal(["1", "2", "3", "4"], result.Nodes.Select(n => n.PageId).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(4, result.ScopeCount);
        Assert.All(result.Nodes, n => Assert.Equal(0, n.Depth));
    }

    [Fact]
    public async Task A_page_in_two_scopes_is_counted_and_fetched_once()
    {
        var crawler = NewCrawler(out var client, out var handler);
        using var _c = client;

        // OVERLAP = {2,3} — both already covered by PR and PH.
        var result = await crawler.CrawlAsync(
            [Space("PR"), Space("OVERLAP"), Space("PH")], "https://x.atlassian.net", When,
            new ConfluenceCrawlOptions(MaxDepth: 0, MaxPages: 50));

        Assert.Equal(["1", "2", "3", "4"], result.Nodes.Select(n => n.PageId).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(4, result.ScopeCount);   // not 6

        // The shared visited-set means each page body is fetched exactly once.
        foreach (var id in new[] { "1", "2", "3", "4" })
        {
            Assert.Equal(1, handler.PageFetches.Count(f => f == id));
        }
    }

    [Fact]
    public async Task The_budget_is_a_ceiling_on_the_run_not_per_seed()
    {
        var crawler = NewCrawler(out var client, out _);
        using var _c = client;

        var result = await crawler.CrawlAsync(
            [Space("PR"), Space("PH")], "https://x.atlassian.net", When,
            new ConfluenceCrawlOptions(MaxDepth: 0, MaxPages: 3));

        // Three, not three-per-space — and the remainder is reported, not dropped
        // silently (§5).
        Assert.Equal(3, result.Nodes.Count);
        Assert.True(result.BudgetHit);
        Assert.Equal(1, result.Dropped);
    }

    [Fact]
    public async Task A_single_seed_still_works_through_the_convenience_overload()
    {
        var crawler = NewCrawler(out var client, out _);
        using var _c = client;

        var result = await crawler.CrawlAsync(
            Space("PR"), "https://x.atlassian.net", When,
            new ConfluenceCrawlOptions(MaxDepth: 0, MaxPages: 50));

        Assert.Equal(["1", "2"], result.Nodes.Select(n => n.PageId).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(2, result.ScopeCount);
    }

    /// <summary>Serves per-space CQL scopes and page bodies; records page fetches.</summary>
    private sealed class SpaceHandler : HttpMessageHandler
    {
        private static readonly Dictionary<string, string[]> Spaces = new(StringComparer.OrdinalIgnoreCase)
        {
            ["PR"] = ["1", "2"],
            ["PH"] = ["3", "4"],
            ["OVERLAP"] = ["2", "3"],
        };

        public List<string> PageFetches { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            string body;

            if (path.Contains("/content/search", StringComparison.Ordinal))
            {
                var cql = HttpUtility.ParseQueryString(uri.Query)["cql"] ?? string.Empty;
                var key = Spaces.Keys.FirstOrDefault(k => cql.Contains($"space = \"{k}\"", StringComparison.OrdinalIgnoreCase));
                var ids = key is null ? [] : Spaces[key];
                var results = string.Join(",", ids.Select(id => $"{{\"id\":\"{id}\",\"title\":\"Page {id}\"}}"));
                body = $"{{\"results\":[{results}],\"_links\":{{}}}}";
            }
            else if (path.Contains("/child/comment", StringComparison.Ordinal))
            {
                body = "{\"results\":[]}";
            }
            else
            {
                var id = path[(path.IndexOf("/content/", StringComparison.Ordinal) + "/content/".Length)..];
                PageFetches.Add(id);
                body =
                    "{\"id\":\"" + id + "\",\"title\":\"Page " + id + "\",\"type\":\"page\",\"space\":{\"key\":\"SP\"},"
                    + "\"version\":{\"number\":1,\"when\":\"2026-08-01T00:00:00.000Z\"},\"ancestors\":[],"
                    + "\"body\":{\"view\":{\"value\":\"<p>body</p>\"}}}";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
