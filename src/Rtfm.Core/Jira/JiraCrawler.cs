using System.Text.RegularExpressions;

namespace Rtfm.Core.Jira;

/// <summary>How far and how wide a crawl may reach (§2.16 leash).</summary>
/// <param name="MaxDepth">Link-follow depth; the seed is depth 0. 0 = seed only.</param>
/// <param name="MaxTickets">Hard ceiling on tickets fetched/indexed per crawl.</param>
/// <param name="FollowMentions">Follow <c>KEY-123</c> text mentions — <b>seed only</b>, validated against real project keys.</param>
public sealed record JiraCrawlOptions(int MaxDepth, int MaxTickets, bool FollowMentions);

/// <summary>One crawled ticket: its BFS depth, the pulled issue, and its rendered document.</summary>
public sealed record JiraCrawlNode(string Key, int Depth, JiraIssue Issue, RenderedJiraDocument Rendered);

/// <summary>The outcome of a crawl: what to index, and what the leash cut.</summary>
/// <param name="Nodes">Tickets pulled, in BFS order — the index plan.</param>
/// <param name="Discovered">Total unique ticket keys reached (indexed + dropped + skipped).</param>
/// <param name="Dropped">Discovered but not pulled because the <see cref="JiraCrawlOptions.MaxTickets"/> budget was hit.</param>
/// <param name="BudgetHit">True when the budget stopped the walk (some links were left unfollowed).</param>
/// <param name="Skipped">Keys that could not be fetched (deleted, or no permission) — logged, never fatal.</param>
public sealed record JiraCrawlResult(
    IReadOnlyList<JiraCrawlNode> Nodes,
    int Discovered,
    int Dropped,
    bool BudgetHit,
    IReadOnlyList<string> Skipped);

/// <summary>
/// Breadth-first traversal of a Jira ticket's neighbourhood (§2.16 Phase 25
/// step 2). Follows structured edges — issue links, parent, subtasks, and epic
/// children (<c>parent = KEY</c>) — plus, from the seed only and opt-in, text
/// mentions of real project keys. Three independent caps leash the walk
/// (<see cref="JiraCrawlOptions"/>): a visited-set keys circular refs by ticket,
/// tickets at max depth are not expanded, and the budget stops the walk once it
/// is reached (the remainder is reported as <see cref="JiraCrawlResult.Dropped"/>,
/// never silently swallowed — §5 "no silent caps"). Fidelity degrades with
/// depth: the seed is pulled with comments, deeper tickets description-only.
/// A ticket that fails to fetch is skipped, not fatal.
/// </summary>
public sealed partial class JiraCrawler(JiraClient client, JiraDocumentRenderer renderer)
{
    public async Task<JiraCrawlResult> CrawlAsync(
        string seedKey,
        string baseUrl,
        DateTimeOffset pulledAt,
        JiraCrawlOptions options,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var maxDepth = Math.Max(0, options.MaxDepth);
        var budget = Math.Max(1, options.MaxTickets);
        var seed = seedKey.Trim().ToUpperInvariant();

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { seed };
        var queue = new Queue<(string Key, int Depth)>();
        queue.Enqueue((seed, 0));

        var nodes = new List<JiraCrawlNode>();
        var skipped = new List<string>();
        var budgetHit = false;

        // Project keys are only needed to validate mention edges (and only the
        // seed contributes those), so fetch them once, up front, when opted in.
        IReadOnlySet<string>? projectKeys = options.FollowMentions
            ? await client.FetchProjectKeysAsync(cancellationToken).ConfigureAwait(false)
            : null;

        while (queue.Count > 0)
        {
            var (key, depth) = queue.Dequeue();

            if (nodes.Count >= budget)
            {
                budgetHit = true;
                break;
            }

            JiraIssue issue;
            try
            {
                issue = await client.FetchIssueAsync(key, includeComments: depth == 0, cancellationToken).ConfigureAwait(false);
            }
            catch (JiraException ex)
            {
                skipped.Add(key);
                log?.Invoke($"skipped {key}: {ex.Message}");
                continue;
            }

            var rendered = renderer.Render(issue, baseUrl, pulledAt);
            var canonical = issue.Key.ToUpperInvariant();
            nodes.Add(new JiraCrawlNode(canonical, depth, issue, rendered));
            log?.Invoke($"pulled {canonical} (depth {depth})");

            // Tickets at the depth limit are indexed but not expanded — no point
            // discovering neighbours we would never follow.
            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (var neighbour in await NeighboursAsync(issue, isSeed: depth == 0, options, projectKeys, budget, cancellationToken).ConfigureAwait(false))
            {
                if (visited.Add(neighbour))
                {
                    queue.Enqueue((neighbour, depth + 1));
                }
            }
        }

        // Everything discovered but neither indexed nor skipped was cut by the budget.
        var dropped = Math.Max(0, visited.Count - nodes.Count - skipped.Count);
        return new JiraCrawlResult(nodes, visited.Count, dropped, budgetHit, skipped);
    }

    private async Task<IReadOnlyList<string>> NeighboursAsync(
        JiraIssue issue,
        bool isSeed,
        JiraCrawlOptions options,
        IReadOnlySet<string>? projectKeys,
        int budget,
        CancellationToken cancellationToken)
    {
        var neighbours = new List<string>();

        foreach (var link in issue.Links)
        {
            neighbours.Add(link.Key);
        }

        if (issue.ParentKey is { } parent)
        {
            neighbours.Add(parent);
        }

        neighbours.AddRange(issue.Subtasks);

        // Epic/story children — the relationship that carries an epic's stories,
        // discoverable only by query. Bounded by the budget so one huge epic
        // can't pull an unbounded key list into memory.
        var children = await client.SearchIssueKeysAsync($"parent = \"{issue.Key}\"", budget, cancellationToken).ConfigureAwait(false);
        neighbours.AddRange(children);

        if (isSeed && options.FollowMentions && projectKeys is { Count: > 0 })
        {
            foreach (var mention in ExtractMentions(issue))
            {
                var prefix = mention[..mention.IndexOf('-')];
                if (projectKeys.Contains(prefix))
                {
                    neighbours.Add(mention);
                }
            }
        }

        return neighbours
            .Select(k => k.Trim().ToUpperInvariant())
            .Where(k => k.Length > 0 && !string.Equals(k, issue.Key, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Issue-key-shaped tokens in the seed's description + comment bodies (raw HTML).</summary>
    internal static IReadOnlyList<string> ExtractMentions(JiraIssue issue)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Scan(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            foreach (Match m in IssueKeyPattern().Matches(text))
            {
                found.Add(m.Value.ToUpperInvariant());
            }
        }

        Scan(issue.DescriptionHtml);
        foreach (var comment in issue.Comments)
        {
            Scan(comment.BodyHtml);
        }

        return found.ToList();
    }

    // A project key: two+ leading letters, then letters/digits, a dash, digits.
    // Validated against real project keys before use, so false positives
    // (UTF-8, SHA-256) are filtered by the caller.
    [GeneratedRegex(@"\b[A-Z][A-Z0-9]+-\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex IssueKeyPattern();
}
