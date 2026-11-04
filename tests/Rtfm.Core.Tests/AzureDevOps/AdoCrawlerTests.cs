using Rtfm.Core.AzureDevOps;

namespace Rtfm.Core.Tests.AzureDevOps;

/// <summary>
/// The crawl leash over a fixed graph, offline:
///   1 (epic) ─child→ 2 ─child→ 4
///            └child→ 3 ─related→ 5 (404 → skipped)
///   every child links back to its parent (cycles), and 2 ↔ 3 are related.
/// </summary>
public class AdoCrawlerTests
{
    private static readonly DateTimeOffset When = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    private static (AdoCrawler Crawler, AdoClient Client, AdoStubHandler Handler) NewCrawler()
    {
        var handler = new AdoStubHandler()
            .OnJson(p => AdoFixtures.IsItem(p, 1), AdoFixtures.WorkItem(1, "Epic", children: [2, 3], commentCount: 1))
            .OnJson(p => AdoFixtures.IsItem(p, 2), AdoFixtures.WorkItem(2, "Feature A", parent: 1, children: [4], related: [3], commentCount: 1))
            .OnJson(p => AdoFixtures.IsItem(p, 3), AdoFixtures.WorkItem(3, "Feature B", parent: 1, related: [2, 5]))
            .OnJson(p => AdoFixtures.IsItem(p, 4), AdoFixtures.WorkItem(4, "Story", parent: 2))
            .OnJson(p => p.Contains("/comments"), """{"comments":[]}""");
        var client = AdoFixtures.Client(handler);
        return (new AdoCrawler(client, new AdoDocumentRenderer()), client, handler);
    }

    [Fact]
    public async Task Full_depth_walks_once_skips_the_dead_link_and_survives_cycles()
    {
        var (crawler, client, _) = NewCrawler();
        using var _c = client;

        var result = await crawler.CrawlAsync([1], When, new AdoCrawlOptions(MaxDepth: 5, MaxItems: 50, FollowMentions: false));

        Assert.Equal([1, 2, 3, 4], result.Nodes.Select(n => n.Id).Order());
        Assert.Equal([5], result.Skipped);
        Assert.False(result.BudgetHit);
        Assert.Equal(0, result.Dropped);
        Assert.Equal(2, result.Nodes.Single(n => n.Id == 4).Depth);
    }

    [Fact]
    public async Task Only_seeds_pull_comments()
    {
        var (crawler, client, handler) = NewCrawler();
        using var _c = client;

        await crawler.CrawlAsync([1], When, new AdoCrawlOptions(MaxDepth: 2, MaxItems: 50, FollowMentions: false));

        // Item 2 has comments too, but it is depth 1 → description-only.
        Assert.Equal(["/Contoso/Team Alpha/_apis/wit/workItems/1/comments"], handler.Paths.Where(p => p.Contains("/comments")).Select(p => p.Split('?')[0]));
    }

    [Fact]
    public async Task Budget_stops_the_walk_and_reports_the_remainder()
    {
        var (crawler, client, _) = NewCrawler();
        using var _c = client;

        var result = await crawler.CrawlAsync([1], When, new AdoCrawlOptions(MaxDepth: 5, MaxItems: 2, FollowMentions: false));

        Assert.Equal(2, result.Nodes.Count);
        Assert.True(result.BudgetHit);
        Assert.True(result.Dropped >= 1);
    }

    [Fact]
    public async Task Seeds_are_pulled_before_any_neighbour()
    {
        var (crawler, client, _) = NewCrawler();
        using var _c = client;

        // Budget 2 with seeds 4 and 1: both seeds survive, no neighbour does.
        var result = await crawler.CrawlAsync([4, 1], When, new AdoCrawlOptions(MaxDepth: 5, MaxItems: 2, FollowMentions: false));

        Assert.Equal([4, 1], result.Nodes.Select(n => n.Id));
        Assert.All(result.Nodes, n => Assert.Equal(0, n.Depth));
    }

    [Fact]
    public void Mentions_come_from_links_and_bare_hashes_but_not_entities_or_css()
    {
        var item = new AdoWorkItem(
            1, 1, "t", null, null, null, null, null, null, null, null, null, null, [], "Team", 0, null,
            RichFields:
            [
                new AdoRichField("System.Description", "Description",
                    """<p style="color:#333333">See <a href="https://dev.azure.com/Contoso/Team/_workitems/edit/4242/">#4242</a> &#128542; and step #1.</p>""",
                    false),
            ],
            Links: [],
            Artifacts: [],
            Hyperlinks: [],
            Comments: [new AdoComment("A", null, "Duplicate of #777, see also #1 itself", true)],
            WebUrl: null);

        Assert.Equal([777, 4242], AdoCrawler.ExtractMentions(item));
    }

    [Fact]
    public void Mentions_are_followed_only_from_seeds_and_only_when_opted_in()
    {
        var item = new AdoWorkItem(
            1, 1, "t", null, null, null, null, null, null, null, null, null, null, [], "Team", 0, ParentId: 9,
            RichFields: [new AdoRichField("System.Description", "Description", "relates to #555", true)],
            Links: [new AdoLink("Child", 2)], Artifacts: [], Hyperlinks: [], Comments: [], WebUrl: null);

        Assert.Equal([9, 2], AdoCrawler.Neighbours(item, isSeed: true, new AdoCrawlOptions(1, 10, FollowMentions: false)));
        Assert.Equal([9, 2, 555], AdoCrawler.Neighbours(item, isSeed: true, new AdoCrawlOptions(1, 10, FollowMentions: true)));
        Assert.Equal([9, 2], AdoCrawler.Neighbours(item, isSeed: false, new AdoCrawlOptions(1, 10, FollowMentions: true)));
    }
}
