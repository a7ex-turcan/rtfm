using System.Globalization;
using System.Text.RegularExpressions;

namespace Rtfm.Core.AzureDevOps;

/// <summary>How far and how wide a crawl may reach (the §2.16 leash).</summary>
/// <param name="MaxDepth">Link-follow depth; seeds are depth 0. 0 = seeds only.</param>
/// <param name="MaxItems">Hard ceiling on work items fetched/indexed per crawl.</param>
/// <param name="FollowMentions">Follow <c>#123</c> mentions in the seeds' text — <b>seeds only</b>.</param>
public sealed record AdoCrawlOptions(int MaxDepth, int MaxItems, bool FollowMentions);

/// <summary>One crawled work item: its BFS depth, the pulled item, its rendered document, and its development record.</summary>
public sealed record AdoCrawlNode(int Id, int Depth, AdoWorkItem Item, RenderedAdoDocument Rendered, AdoDevelopment Development);

/// <summary>The outcome of a crawl: what to index, and what the leash cut.</summary>
/// <param name="Nodes">Work items pulled, in BFS order — the index plan.</param>
/// <param name="Discovered">Unique ids reached (indexed + dropped + skipped).</param>
/// <param name="Dropped">Discovered but not pulled because the budget was hit.</param>
/// <param name="BudgetHit">True when the budget stopped the walk.</param>
/// <param name="Skipped">Ids that could not be fetched (deleted, or no permission) — never fatal.</param>
/// <param name="Warnings">Non-fatal degradations (comments or development data unreadable), de-duplicated.</param>
public sealed record AdoCrawlResult(
    IReadOnlyList<AdoCrawlNode> Nodes,
    int Discovered,
    int Dropped,
    bool BudgetHit,
    IReadOnlyList<int> Skipped,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Breadth-first traversal of a work item's neighbourhood (§2.18), the
/// <see cref="Jira.JiraCrawler"/> model: one or more seeds crawl as a single run
/// sharing one visited-set and one budget; every work item → work item relation
/// is an edge (parent, children, related, predecessor/successor, duplicates,
/// tests); the visited-set kills cycles; items at max depth are indexed but not
/// expanded; the budget's remainder is reported, never swallowed (§5).
///
/// <para>Simpler than Jira's in one respect: an Azure DevOps item carries its
/// <em>children</em> among its own relations, so no query is needed to find an
/// epic's features or a feature's stories — which is also what keeps the client
/// GET-only.</para>
///
/// Fidelity degrades with depth exactly as Jira's does: seeds pull their
/// comments, deeper items do not. Development links are resolved at every depth,
/// because PRs hang off the stories and tasks under an epic, not the epic.
/// </summary>
public sealed partial class AdoCrawler(AdoClient client, AdoDocumentRenderer renderer)
{
    public async Task<AdoCrawlResult> CrawlAsync(
        IReadOnlyList<int> seedIds,
        DateTimeOffset pulledAt,
        AdoCrawlOptions options,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var maxDepth = Math.Max(0, options.MaxDepth);
        var budget = Math.Max(1, options.MaxItems);

        var visited = new HashSet<int>();
        var queue = new Queue<(int Id, int Depth)>();

        // Every seed is enqueued before any neighbour, so a tight budget cuts
        // links, never the items the user named.
        foreach (var seed in seedIds.Where(i => i > 0))
        {
            if (visited.Add(seed))
            {
                queue.Enqueue((seed, 0));
            }
        }

        var nodes = new List<AdoCrawlNode>();
        var skipped = new List<int>();
        var warnings = new List<string>();
        var budgetHit = false;

        void Warn(string message)
        {
            if (!warnings.Contains(message, StringComparer.Ordinal))
            {
                warnings.Add(message);
            }

            log?.Invoke(message);
        }

        while (queue.Count > 0)
        {
            var (id, depth) = queue.Dequeue();

            if (nodes.Count >= budget)
            {
                budgetHit = true;
                break;
            }

            AdoWorkItem item;
            try
            {
                item = await client.FetchWorkItemAsync(id, includeComments: depth == 0, Warn, cancellationToken).ConfigureAwait(false);
            }
            catch (AdoException ex)
            {
                skipped.Add(id);
                log?.Invoke($"skipped #{id}: {ex.Message}");
                continue;
            }

            var development = await client.FetchDevelopmentAsync(item, Warn, cancellationToken).ConfigureAwait(false);
            var rendered = renderer.Render(item, client.OrganizationUrl, pulledAt, development);
            nodes.Add(new AdoCrawlNode(item.Id, depth, item, rendered, development));
            log?.Invoke($"pulled #{item.Id} (depth {depth}"
                + (development.IsEmpty ? ")" : $", {development.PullRequests.Count} PR(s), {development.Commits.Count} commit(s))"));

            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (var neighbour in Neighbours(item, isSeed: depth == 0, options))
            {
                if (visited.Add(neighbour))
                {
                    queue.Enqueue((neighbour, depth + 1));
                }
            }
        }

        var dropped = Math.Max(0, visited.Count - nodes.Count - skipped.Count);
        return new AdoCrawlResult(nodes, visited.Count, dropped, budgetHit, skipped, warnings);
    }

    internal static IReadOnlyList<int> Neighbours(AdoWorkItem item, bool isSeed, AdoCrawlOptions options)
    {
        var neighbours = new List<int>();
        if (item.ParentId is { } parent)
        {
            neighbours.Add(parent);
        }

        neighbours.AddRange(item.Links.Select(l => l.Id));

        if (isSeed && options.FollowMentions)
        {
            neighbours.AddRange(ExtractMentions(item));
        }

        return neighbours.Where(i => i > 0 && i != item.Id).Distinct().ToList();
    }

    /// <summary>
    /// Work item ids mentioned in an item's rich fields and comments. Two forms:
    /// links to <c>…/_workitems/edit/{id}</c> (how the editor renders a
    /// <c>#123</c> mention), and a bare <c>#123</c> in the text once markup is
    /// stripped — markup first, because raw HTML is full of <c>#</c>-digits that
    /// are not mentions (<c>color:#333</c>, <c>&amp;#128542;</c> entities). At
    /// least two digits, so "step #1" isn't a link. Seed-only and opt-in, like
    /// Jira's mention-following: a stray hit costs one fetch, nothing worse.
    /// </summary>
    internal static IReadOnlyList<int> ExtractMentions(AdoWorkItem item)
    {
        var found = new SortedSet<int>();

        void Scan(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            foreach (Match m in EditLink().Matches(text))
            {
                Add(m.Groups["id"].Value);
            }

            var plain = Tag().Replace(text, " ");
            foreach (Match m in HashMention().Matches(plain))
            {
                Add(m.Groups["id"].Value);
            }
        }

        void Add(string digits)
        {
            if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
            {
                found.Add(id);
            }
        }

        foreach (var field in item.RichFields)
        {
            Scan(field.Content);
        }

        foreach (var comment in item.Comments)
        {
            Scan(comment.Text);
        }

        found.Remove(item.Id);
        return found.ToList();
    }

    [GeneratedRegex(@"/_workitems/edit/(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex EditLink();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tag();

    // Not after '&' (an HTML entity like &#128542;) or a word character.
    [GeneratedRegex(@"(?<![&\w])#(?<id>\d{2,})\b")]
    private static partial Regex HashMention();
}
