using System.Net;
using Rtfm.Core.Confluence;

namespace Rtfm.Core.Tests.Confluence;

/// <summary>
/// The Phase 26 step-2 traversal (§2.17), offline via <see cref="ConfluenceClient"/>'s
/// handler seam. Fixture: a page seed whose CQL scope is pages 1,2,3; then
/// in-body links — 1→11, 2→1 (cycle), 3→99 (a folder), 11→12.
/// </summary>
public class ConfluenceCrawlerTests
{
    private static readonly DateTimeOffset When = new(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);

    private static ConfluenceCrawler NewCrawler(out ConfluenceClient client)
    {
        client = new ConfluenceClient(new ConfluenceConfig("https://x.atlassian.net", "me@x.com", "tok"), new StubHandler());
        return new ConfluenceCrawler(client, new ConfluenceDocumentRenderer());
    }

    private static ConfluenceSeed PageSeed => new(ConfluenceSeedKind.Page, "1");

    [Fact]
    public async Task Scope_plus_one_link_hop_indexes_the_subtree_and_linked_page_skips_the_folder()
    {
        var crawler = NewCrawler(out var client);
        using var _ = client;

        var result = await crawler.CrawlAsync(PageSeed, "https://x.atlassian.net", When, new ConfluenceCrawlOptions(MaxDepth: 1, MaxPages: 50));

        // Scope 1,2,3 (depth 0) + link 11 (depth 1). 99 is a folder → skipped;
        // 12 is behind 11 at depth 2, past the limit.
        Assert.Equal(new[] { "1", "11", "2", "3" }, result.Nodes.Select(n => n.PageId).OrderBy(k => k));
        Assert.Equal(3, result.ScopeCount);
        Assert.Contains("99", result.Skipped);
        Assert.False(result.BudgetHit);
        Assert.Equal(1, result.Nodes.Single(n => n.PageId == "11").Depth);
    }

    [Fact]
    public async Task Depth_zero_indexes_only_the_scope()
    {
        var crawler = NewCrawler(out var client);
        using var _ = client;

        var result = await crawler.CrawlAsync(PageSeed, "https://x.atlassian.net", When, new ConfluenceCrawlOptions(MaxDepth: 0, MaxPages: 50));

        Assert.Equal(new[] { "1", "2", "3" }, result.Nodes.Select(n => n.PageId).OrderBy(k => k));
        Assert.Equal(3, result.ScopeCount);
    }

    [Fact]
    public async Task Budget_stops_the_walk_and_reports_dropped()
    {
        var crawler = NewCrawler(out var client);
        using var _ = client;

        var result = await crawler.CrawlAsync(PageSeed, "https://x.atlassian.net", When, new ConfluenceCrawlOptions(MaxDepth: 2, MaxPages: 2));

        Assert.Equal(2, result.Nodes.Count);
        Assert.True(result.BudgetHit);
        Assert.True(result.Dropped >= 1);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        // id -> (type, body html with /pages/{id}/ links)
        private static readonly Dictionary<string, (string Type, string Body)> Pages = new()
        {
            // Single-quoted hrefs so the body embeds cleanly in JSON; the link
            // regex (/pages/\d+) matches either quote style.
            ["1"] = ("page", "<p><a href='/pages/11/x'>l</a></p>"),
            ["2"] = ("page", "<a href='/pages/1/x'>back</a>"),      // cycle to 1
            ["3"] = ("page", "<a href='/pages/99/x'>folder</a>"),   // link to a folder
            ["11"] = ("page", "<a href='/pages/12/x'>deep</a>"),
            ["12"] = ("page", ""),
            ["99"] = ("folder", ""),
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string body;

            if (path.Contains("/content/search", StringComparison.Ordinal))
            {
                // Scope for the page seed: 1 + its subtree 2,3.
                body = "{\"results\":[{\"id\":\"1\",\"title\":\"One\"},{\"id\":\"2\",\"title\":\"Two\"},{\"id\":\"3\",\"title\":\"Three\"}],\"size\":3}";
            }
            else
            {
                var id = path[(path.IndexOf("/content/", StringComparison.Ordinal) + "/content/".Length)..];
                if (!Pages.TryGetValue(id, out var p))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
                }

                body =
                    "{\"id\":\"" + id + "\",\"title\":\"Page " + id + "\",\"type\":\"" + p.Type
                    + "\",\"space\":{\"key\":\"SP\"},\"version\":{\"number\":1,\"when\":\"2026-07-01T00:00:00.000Z\"},\"ancestors\":[],"
                    + "\"children\":{\"page\":{\"results\":[]}},\"body\":{\"view\":{\"value\":\"" + p.Body + "\"}}}";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
