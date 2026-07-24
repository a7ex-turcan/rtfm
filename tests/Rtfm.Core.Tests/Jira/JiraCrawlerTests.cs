using System.Net;
using System.Text.RegularExpressions;
using Rtfm.Core.Jira;

namespace Rtfm.Core.Tests.Jira;

/// <summary>
/// The Phase 25 step-2 traversal leash, exercised offline via <see cref="JiraClient"/>'s
/// handler seam against a fixed graph:
///   A ─┬─(child)→ B ─(parent)→ A   (cycle)
///      └─(child)→ C ─┬─(child)→ D  (leaf)
///                    └─(link)→  E  (404 → skipped)
/// No network — the stub answers issue fetches and <c>parent = "…"</c> searches.
/// </summary>
public class JiraCrawlerTests
{
    private static readonly DateTimeOffset When = new(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);

    private static JiraCrawler NewCrawler(out JiraClient client)
    {
        client = new JiraClient(new JiraConfig("https://x.atlassian.net", "me@x.com", "tok"), new StubHandler());
        return new JiraCrawler(client, new JiraDocumentRenderer());
    }

    [Fact]
    public async Task Full_depth_walks_the_graph_once_skips_the_dead_link_and_terminates_on_the_cycle()
    {
        var crawler = NewCrawler(out var client);
        using var _ = client;

        var result = await crawler.CrawlAsync("A", "https://x.atlassian.net", When, new JiraCrawlOptions(MaxDepth: 3, MaxTickets: 50, FollowMentions: false));

        // A, B, C, D pulled exactly once; E is the dead link → skipped, not fatal.
        Assert.Equal(new[] { "A", "B", "C", "D" }, result.Nodes.Select(n => n.Key).OrderBy(k => k));
        Assert.Equal(new[] { "E" }, result.Skipped);
        Assert.False(result.BudgetHit);
        Assert.Equal(0, result.Dropped);

        // Depths: A=0, B=C=1, D=2.
        Assert.Equal(0, result.Nodes.Single(n => n.Key == "A").Depth);
        Assert.Equal(1, result.Nodes.Single(n => n.Key == "C").Depth);
        Assert.Equal(2, result.Nodes.Single(n => n.Key == "D").Depth);
    }

    [Fact]
    public async Task Depth_limit_bounds_the_frontier()
    {
        var crawler = NewCrawler(out var client);
        using var _ = client;

        var result = await crawler.CrawlAsync("A", "https://x.atlassian.net", When, new JiraCrawlOptions(MaxDepth: 1, MaxTickets: 50, FollowMentions: false));

        // A(0) expands to B,C(1); B and C are the frontier and are not expanded,
        // so D and E are never discovered.
        Assert.Equal(new[] { "A", "B", "C" }, result.Nodes.Select(n => n.Key).OrderBy(k => k));
        Assert.Empty(result.Skipped);
        Assert.False(result.BudgetHit);
    }

    [Fact]
    public async Task Budget_stops_the_walk_and_reports_the_remainder_as_dropped()
    {
        var crawler = NewCrawler(out var client);
        using var _ = client;

        var result = await crawler.CrawlAsync("A", "https://x.atlassian.net", When, new JiraCrawlOptions(MaxDepth: 3, MaxTickets: 2, FollowMentions: false));

        Assert.Equal(2, result.Nodes.Count);       // A + one child
        Assert.True(result.BudgetHit);
        Assert.True(result.Dropped >= 1);           // at least the un-followed child
    }

    [Fact]
    public async Task Depth_zero_indexes_only_the_seed()
    {
        var crawler = NewCrawler(out var client);
        using var _ = client;

        var result = await crawler.CrawlAsync("A", "https://x.atlassian.net", When, new JiraCrawlOptions(MaxDepth: 0, MaxTickets: 50, FollowMentions: false));

        Assert.Single(result.Nodes);
        Assert.Equal("A", result.Nodes[0].Key);
        Assert.False(result.BudgetHit);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private static readonly Dictionary<string, string[]> Children = new()
        {
            ["A"] = ["B", "C"],
            ["C"] = ["D"],
        };

        // B links up to A (cycle); C links out to E (which 404s).
        private static readonly Dictionary<string, string> Links = new()
        {
            ["C"] = "E",
        };

        private static readonly HashSet<string> Existing = ["A", "B", "C", "D"];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            string body;

            if (path.Contains("/search/jql", StringComparison.Ordinal))
            {
                var jql = Uri.UnescapeDataString(uri.Query);
                var parent = Regex.Match(jql, "parent = \"(?<k>[^\"]+)\"").Groups["k"].Value;
                var kids = Children.TryGetValue(parent, out var c) ? c : [];
                body = $"{{\"issues\":[{string.Join(",", kids.Select(k => $"{{\"key\":\"{k}\"}}"))}],\"isLast\":true}}";
            }
            else if (path.Contains("/project/search", StringComparison.Ordinal))
            {
                body = "{\"values\":[],\"isLast\":true}";
            }
            else if (path.Contains("/issue/", StringComparison.Ordinal))
            {
                var key = path[(path.IndexOf("/issue/", StringComparison.Ordinal) + "/issue/".Length)..];
                if (!Existing.Contains(key))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
                }

                var parentLink = Links.TryGetValue(key, out var e)
                    ? "{\"type\":{\"outward\":\"relates to\"},\"outwardIssue\":{\"key\":\"" + e + "\"}}"
                    : string.Empty;
                var parentField = key == "B" ? ",\"parent\":{\"key\":\"A\"}" : string.Empty;
                body =
                    "{\"key\":\"" + key + "\",\"fields\":{\"summary\":\"" + key
                    + " summary\",\"status\":{\"name\":\"Open\"},\"issuetype\":{\"name\":\"Task\"},\"labels\":[],\"issuelinks\":["
                    + parentLink + "],\"subtasks\":[]" + parentField
                    + "},\"renderedFields\":{\"description\":\"<p>body</p>\"}}";
            }
            else
            {
                body = "{\"displayName\":\"Test\"}";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
