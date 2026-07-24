namespace Rtfm.Core.Confluence;

/// <summary>How far and how wide a crawl may reach (§2.17 leash).</summary>
/// <param name="MaxDepth">In-body link-follow depth beyond the resolved scope; 0 = scope only.</param>
/// <param name="MaxPages">Hard ceiling on pages fetched/indexed per crawl.</param>
public sealed record ConfluenceCrawlOptions(int MaxDepth, int MaxPages);

/// <summary>One crawled page: its BFS depth (0 = in-scope), the pulled page, and its rendered document.</summary>
public sealed record ConfluenceCrawlNode(string PageId, int Depth, ConfluencePage Page, RenderedConfluenceDocument Rendered);

/// <summary>The outcome of a crawl: what to index, and what the leash/scoping cut.</summary>
public sealed record ConfluenceCrawlResult(
    IReadOnlyList<ConfluenceCrawlNode> Nodes,
    int ScopeCount,
    int Discovered,
    int Dropped,
    bool BudgetHit,
    IReadOnlyList<string> Skipped);

/// <summary>
/// Traverses a Confluence seed's neighbourhood (§2.17 Phase 26 step 2). The
/// <b>scope</b> — a page + its subtree, a folder's subtree, or a whole space —
/// is resolved in one shot via CQL (<c>ancestor</c>/<c>space</c>, which flattens
/// sub-folders), so the page *tree* needs no level-walking. From those in-scope
/// pages the crawl then follows <b>in-body page links</b> breadth-first up to
/// <see cref="ConfluenceCrawlOptions.MaxDepth"/> hops. A visited-set keys cycles,
/// the <see cref="ConfluenceCrawlOptions.MaxPages"/> budget stops the walk (the
/// remainder reported as <see cref="ConfluenceCrawlResult.Dropped"/>, §5), and
/// non-page content (a folder reached as a seed self, a link to a folder) is
/// skipped, not indexed.
/// </summary>
public sealed class ConfluenceCrawler(ConfluenceClient client, ConfluenceDocumentRenderer renderer)
{
    /// <summary>
    /// Resolves a seed to its in-scope page id + title summaries via CQL,
    /// budget-capped. Used both for <c>--dry-run</c> preview and as the crawl's
    /// depth-0 set.
    /// </summary>
    public Task<IReadOnlyList<(string Id, string Title)>> ResolveScopeAsync(ConfluenceSeed seed, int budget, CancellationToken cancellationToken = default)
    {
        var cql = seed.Kind switch
        {
            // The page itself plus everything beneath it.
            ConfluenceSeedKind.Page => $"(id = {seed.Value} OR ancestor = {seed.Value}) AND type = page",
            // A folder's descendant pages (the folder itself is not a page).
            ConfluenceSeedKind.Folder => $"ancestor = {seed.Value} AND type = page",
            // Every page in the space.
            _ => $"space = \"{seed.Value}\" AND type = page",
        };

        return client.SearchPagesAsync(cql, budget, cancellationToken);
    }

    public async Task<ConfluenceCrawlResult> CrawlAsync(
        ConfluenceSeed seed,
        string baseUrl,
        DateTimeOffset pulledAt,
        ConfluenceCrawlOptions options,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var maxDepth = Math.Max(0, options.MaxDepth);
        var budget = Math.Max(1, options.MaxPages);

        // Resolve the *full* scope (CQL returns only ids/titles, so this is cheap
        // even for hundreds of pages), not just `budget` of it — otherwise a
        // 60-page folder with --max-pages 8 would index 8 and silently drop 52.
        // Everything past the budget stays queued and is reported as Dropped (§5).
        var scope = await ResolveScopeAsync(seed, ConfluenceConfig.MaxPagesCeiling, cancellationToken).ConfigureAwait(false);
        log?.Invoke($"resolved scope: {scope.Count} page(s)");

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string Id, int Depth)>();
        foreach (var (id, _) in scope)
        {
            if (visited.Add(id))
            {
                queue.Enqueue((id, 0));
            }
        }

        var nodes = new List<ConfluenceCrawlNode>();
        var skipped = new List<string>();
        var budgetHit = false;

        while (queue.Count > 0)
        {
            var (id, depth) = queue.Dequeue();

            if (nodes.Count >= budget)
            {
                budgetHit = true;
                break;
            }

            ConfluencePage page;
            try
            {
                page = await client.FetchPageAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (ConfluenceException ex)
            {
                skipped.Add(id);
                log?.Invoke($"skipped {id}: {ex.Message}");
                continue;
            }

            // Folders/whiteboards reached as a seed self or a stray link have no
            // indexable body — count them as skipped, not dropped.
            if (!page.IsPage)
            {
                skipped.Add(id);
                continue;
            }

            var rendered = renderer.Render(page, baseUrl, pulledAt);
            nodes.Add(new ConfluenceCrawlNode(page.Id, depth, page, rendered));
            log?.Invoke($"pulled {page.Id} \"{page.Title}\" (depth {depth})");

            if (depth < maxDepth)
            {
                foreach (var linkedId in page.LinkedPageIds)
                {
                    if (visited.Add(linkedId))
                    {
                        queue.Enqueue((linkedId, depth + 1));
                    }
                }
            }
        }

        var dropped = Math.Max(0, visited.Count - nodes.Count - skipped.Count);
        return new ConfluenceCrawlResult(nodes, scope.Count, visited.Count, dropped, budgetHit, skipped);
    }
}
