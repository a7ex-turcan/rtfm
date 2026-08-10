using Spectre.Console;

namespace Rtfm.Cli;

/// <summary>
/// One indexed item, positioned in the hierarchy its source defines: a Jira
/// ticket's parent epic/story, or a Confluence page's ancestor.
/// </summary>
/// <param name="ParentId">Parent's id. Null, or an id outside the run, makes this a root.</param>
/// <param name="Depth">Crawl depth — 0 means it was in the seed's own scope, higher means it was reached by following a link.</param>
/// <param name="Detail">Dimmed suffix (chunk count, PR count…). Optional.</param>
internal sealed record IndexTreeItem(string Id, string? ParentId, int Depth, string Label, string? Detail);

/// <summary>
/// Renders "what just got indexed" as a tree, after a <c>jira index</c> or
/// <c>confluence index</c> run.
///
/// <para>It is decoration with a side of diagnostics: the shape shows at a
/// glance which epic or page subtree the run actually pulled, and what arrived
/// only because a link pointed at it. Presentation only — it reads the crawl
/// result and nothing else.</para>
///
/// <para>Rendered on stderr (diagnostics, per the stream contract) and only on a
/// live terminal, so redirected output keeps exactly the lines it had before.</para>
/// </summary>
internal static class IndexTree
{
    /// <summary>
    /// Nodes drawn before the tree truncates. A 400-page space would otherwise
    /// bury the summary that follows it; the remainder is <em>reported</em>, not
    /// silently dropped (root CLAUDE.md §5).
    /// </summary>
    internal const int MaxNodes = 80;

    /// <param name="seedId">
    /// The item the run was seeded from, marked so it's findable. It is often
    /// <em>not</em> the tree's root: traversal walks up as well as down, so a
    /// seed ticket's own parent epic is usually crawled too and legitimately
    /// sits above it.
    /// </param>
    public static void Render(IReadOnlyList<IndexTreeItem> items, string caption, string? seedId = null)
    {
        if (!Ui.Fancy || items.Count == 0)
        {
            return;
        }

        var byId = items
            .GroupBy(i => i.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var children = items
            .Where(i => i.ParentId is { } p && byId.ContainsKey(p) && !string.Equals(p, i.Id, StringComparison.Ordinal))
            .GroupBy(i => i.ParentId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(i => i.Label, NaturalOrder.Instance).ToList(), StringComparer.Ordinal);

        // A root is anything whose parent isn't part of this run. Depth splits
        // them: 0 was in the seed's scope, anything higher only arrived because
        // a link pointed at it — worth separating, since that's how an
        // unrelated project sneaks into a crawl.
        var roots = items
            .Where(i => i.ParentId is null || !byId.ContainsKey(i.ParentId) || string.Equals(i.ParentId, i.Id, StringComparison.Ordinal))
            .OrderBy(i => i.Label, NaturalOrder.Instance)
            .ToList();

        var tree = new Tree($"[bold]{Ui.E(caption)}[/]").Guide(TreeGuide.Line);
        var drawn = 0;
        var visited = new HashSet<string>(StringComparer.Ordinal);

        var scopeRoots = roots.Where(r => r.Depth == 0).ToList();
        var linkedRoots = roots.Where(r => r.Depth > 0).ToList();

        foreach (var root in scopeRoots)
        {
            AddNode(tree, root, children, visited, ref drawn, seedId);
        }

        // The "linked" heading only earns its place when there is something to
        // contrast it with. When every root arrived at depth > 0 — which is the
        // norm for a Jira seed whose own parent epic got crawled — the heading
        // would sit above the *entire* tree and read as if nothing was in scope.
        var linkedParent = scopeRoots.Count > 0 && linkedRoots.Count > 0
            ? tree.AddNode("[dim]reached by following links[/]")
            : (IHasTreeNodes)tree;

        foreach (var item in linkedRoots)
        {
            AddNode(linkedParent, item, children, visited, ref drawn, seedId);
        }

        // Anything a parent cycle would have stranded: show it rather than lose it.
        var stranded = items.Where(i => !visited.Contains(i.Id)).ToList();
        if (stranded.Count > 0 && drawn < MaxNodes)
        {
            var group = tree.AddNode("[dim]unplaced[/]");
            foreach (var item in stranded)
            {
                AddNode(group, item, children, visited, ref drawn, seedId);
            }
        }

        Ui.Err.Write(tree);

        if (drawn < items.Count)
        {
            Ui.Err.MarkupLine($"[dim]… {items.Count - drawn} more not shown.[/]");
        }
    }

    private static void AddNode(
        IHasTreeNodes parent,
        IndexTreeItem item,
        IReadOnlyDictionary<string, List<IndexTreeItem>> children,
        HashSet<string> visited,
        ref int drawn,
        string? seedId)
    {
        if (drawn >= MaxNodes || !visited.Add(item.Id))
        {
            return;
        }

        drawn++;
        var isSeed = seedId is not null && string.Equals(item.Id, seedId, StringComparison.OrdinalIgnoreCase);
        var text = isSeed
            ? $"[bold {Ui.Accent}]{Ui.E(item.Label)}[/]"
            : $"[{Ui.Accent}]{Ui.E(item.Label)}[/]";

        if (!string.IsNullOrWhiteSpace(item.Detail))
        {
            text += $"  [dim]{Ui.E(item.Detail)}[/]";
        }

        if (isSeed)
        {
            text += "  [dim]← seed[/]";
        }

        var node = parent.AddNode(text);
        if (children.TryGetValue(item.Id, out var kids))
        {
            foreach (var kid in kids)
            {
                AddNode(node, kid, children, visited, ref drawn, seedId);
            }
        }
    }

    /// <summary>
    /// Orders labels so <c>AEXP-19</c> precedes <c>AEXP-100</c>: digit runs
    /// compare numerically, everything else ordinally. Plain ordinal sorting
    /// makes a list of ticket keys look broken.
    /// </summary>
    internal sealed class NaturalOrder : IComparer<string>
    {
        public static readonly NaturalOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x is null || y is null)
            {
                return string.CompareOrdinal(x, y);
            }

            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    var si = i;
                    var sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) { i++; }
                    while (j < y.Length && char.IsDigit(y[j])) { j++; }

                    // Compare by value: trim leading zeros, then length, then digits.
                    var a = x.AsSpan(si, i - si).TrimStart('0');
                    var b = y.AsSpan(sj, j - sj).TrimStart('0');
                    if (a.Length != b.Length)
                    {
                        return a.Length - b.Length;
                    }

                    var cmp = a.CompareTo(b, StringComparison.Ordinal);
                    if (cmp != 0)
                    {
                        return cmp;
                    }
                }
                else
                {
                    var cmp = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (cmp != 0)
                    {
                        return cmp;
                    }

                    i++;
                    j++;
                }
            }

            return (x.Length - i) - (y.Length - j);
        }
    }
}
