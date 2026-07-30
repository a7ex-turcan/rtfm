using Rtfm.Core.Configuration;
using Rtfm.Core.Contradictions;
using Rtfm.Core.Database;
using Rtfm.Core.Manifest;
using Rtfm.Core.Notes;
using Rtfm.Core.OpenSearch;
using Rtfm.Core.Search;
using Spectre.Console;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm describe project &lt;name&gt;</c> — the detail view for one project,
/// laid out <c>kubectl describe</c> style: an aligned field block, then
/// sections. Where <c>rtfm status</c> is one row per project across the whole
/// machine, this is everything RTFM holds for a single one — the read-side
/// mirror of what <c>rtfm purge</c> would delete (§2.14). Resource-noun
/// dispatch (<c>describe &lt;kind&gt; &lt;name&gt;</c>) leaves room for more
/// kinds later; only <c>project</c> exists today.
/// </summary>
internal static class DescribeCommand
{
    private const int LabelWidth = 17;
    private const int DefaultDocumentsShown = 20;

    private static readonly string[] Kinds = ["project"];

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            return Usage();
        }

        if (!Kinds.Contains(args[0], StringComparer.Ordinal))
        {
            Console.Error.WriteLine($"rtfm describe: unknown resource '{args[0]}'. Known: {string.Join(", ", Kinds)}.");
            return 2;
        }

        var (name, all, ok) = ParseArgs(args[1..]);
        if (!ok)
        {
            return Usage();
        }

        // No name given: fall back to the ambient scope, the same one the MCP
        // server would use (§2.14) — inside a wired-up repo that's the answer.
        name ??= RtfmEnvironment.ResolveProjectScope();
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine("rtfm describe project: no project given and RTFM_PROJECT is not set.");
            return 2;
        }

        var gateway = new OpenSearchGateway();
        var health = await Ui.Err.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Describing project {Ui.E(name)} …", _ => gateway.PingAsync());

        if (!health.Reachable)
        {
            Console.Error.WriteLine($"rtfm describe: OpenSearch unreachable at {gateway.Endpoint} ({health.Error}). Is it running? docker compose up -d");
            return 1;
        }

        var description = await new ProjectDescriber(gateway).DescribeAsync(name).ConfigureAwait(false);
        if (!description.Exists)
        {
            Console.Error.WriteLine($"rtfm describe: no project '{name}'.");
            await SuggestProjectsAsync(gateway).ConfigureAwait(false);
            return 1;
        }

        Render(description, all);
        return 0;
    }

    private static void Render(ProjectDescription p, bool allDocuments)
    {
        // One label map for the whole report, so the same document reads the
        // same way in every section.
        var labels = BuildLabels(p.Documents.Select(d => d.Path)
            .Concat(p.OpenPairs.SelectMany(pair => new[] { pair.A.Path, pair.B.Path }))
            .Concat(p.Notes.Select(n => n.TargetPath).OfType<string>()));

        Field("Name:", $"[{Ui.Accent}]{Ui.E(p.Name)}[/]");
        Field("Documents:", p.Status is { } s ? s.DocCount.ToString() : "[dim]0 (nothing indexed)[/]");

        if (p.Status is { } status)
        {
            Field("Chunks:", $"{status.ChunkCount}");
            Field("Vectors:", status.VectorCoverage switch
            {
                >= 0.999 => $"[green]{status.EmbeddedChunkCount}/{status.ChunkCount} (100%)[/]",
                0 => "[yellow]none[/] [dim](lexical-only — run index with the model available)[/]",
                _ => $"[yellow]{status.EmbeddedChunkCount}/{status.ChunkCount} ({status.VectorCoverage:P0})[/]",
            });
            Field("Source dates:", Span(status.OldestSourceModified, status.NewestSourceModified));
            Field("Last indexed:", Moment(status.LastIndexedAt));
        }

        Field("Notes:", p.Notes.Count == 0 ? "[dim]none[/]" : $"{p.Notes.Count} override note{S(p.Notes.Count)}");
        Field("Contradictions:", DescribeContradictions(p.Contradictions));

        RenderKinds(p.Kinds);
        RenderManifests(p.Manifests);
        RenderConnectors(p);
        RenderDatabases(p.Databases);
        RenderOpenPairs(p, labels);
        RenderNotes(p.Notes, labels);
        RenderDocuments(p.Documents, labels, allDocuments);
    }

    /// <summary>
    /// Display labels for source paths: the filename, extended leftwards by as
    /// many folder segments as it takes to stay unique — four documents called
    /// <c>the-workflow.md</c> in different folders must not render as four
    /// identical rows. Synthetic keys (<c>jira://</c>, <c>confluence://</c>) are
    /// already unique and are shown whole.
    /// </summary>
    private static Dictionary<string, string> BuildLabels(IEnumerable<string> paths)
    {
        var unique = paths.Distinct(StringComparer.Ordinal).ToList();
        var segments = unique.ToDictionary(
            p => p,
            p => p.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);

        var labels = unique.ToDictionary(
            p => p,
            p => IsSynthetic(p) || segments[p].Length == 0 ? p : segments[p][^1],
            StringComparer.Ordinal);

        var maxDepth = segments.Count == 0 ? 1 : segments.Values.Max(s => s.Length);
        for (var depth = 2; depth <= maxDepth; depth++)
        {
            var colliding = labels
                .GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .SelectMany(g => g.Select(kv => kv.Key))
                .Where(p => !IsSynthetic(p))
                .ToList();

            if (colliding.Count == 0)
            {
                break;
            }

            foreach (var path in colliding)
            {
                labels[path] = string.Join('/', segments[path].TakeLast(Math.Min(depth, segments[path].Length)));
            }
        }

        return labels;
    }

    private static void RenderKinds(IReadOnlyList<SourceKindCount> kinds)
    {
        if (kinds.Count == 0)
        {
            return;
        }

        Section("Sources by type:");
        var width = kinds.Max(k => k.Kind.Length);
        foreach (var kind in kinds)
        {
            Ui.Out.MarkupLine(
                $"  [{Ui.Accent}]{Ui.E(kind.Kind.PadRight(width))}[/]  "
                + $"{kind.Documents,4} doc{S(kind.Documents)}  {kind.Chunks,6} chunk{S(kind.Chunks)}");
        }
    }

    private static void RenderManifests(IReadOnlyList<ManifestInfo> manifests)
    {
        if (manifests.Count == 0)
        {
            return;
        }

        Section("Watched folders:");
        foreach (var m in manifests)
        {
            Ui.Out.MarkupLine(
                $"  {Ui.E(m.OpenableFolder)}  [dim]{m.TrackedFiles} file{S(m.TrackedFiles)}, "
                + $"manifest saved {new DateTimeOffset(m.UpdatedUtc, TimeSpan.Zero).ToLocalTime():yyyy-MM-dd HH:mm}[/]");
        }
    }

    private static void RenderConnectors(ProjectDescription p)
    {
        if (p.Jira is null && p.Confluence is null)
        {
            return;
        }

        Section("Connectors:");
        foreach (var c in new[] { p.Jira, p.Confluence }.OfType<ConnectorSummary>())
        {
            var unit = c.Kind == "Jira" ? "ticket" : "page";
            Ui.Out.MarkupLine(
                $"  [{Ui.Accent}]{Ui.E(c.Kind.PadRight(10))}[/] {Ui.E(c.BaseUrl)}  [dim]{Ui.E(c.Email)}, token {Ui.E(c.TokenReference)}[/]");
            Ui.Out.MarkupLine(
                $"  {new string(' ', 10)} [dim]{c.Monitored} {unit}{S(c.Monitored)} monitored, "
                + $"last poll {(c.LastPolledAt is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "never")}, every {c.PollSeconds}s[/]");
        }
    }

    private static void RenderDatabases(IReadOnlyList<DatabaseInfo> databases)
    {
        if (databases.Count == 0)
        {
            return;
        }

        Section("Databases:");
        var width = databases.Max(d => d.Name.Length);
        foreach (var db in databases)
        {
            var access = !db.Queryable ? "[dim]schema only[/]" : db.Writable ? "[yellow]read+write[/]" : "[green]read-only[/]";
            Ui.Out.MarkupLine(
                $"  [{Ui.Accent}]{Ui.E(db.Name.PadRight(width))}[/]  {Ui.E(db.Provider),-10} {access}  [dim]{Ui.E(db.DescriptorPath)}[/]");
        }
    }

    private static void RenderOpenPairs(ProjectDescription p, IReadOnlyDictionary<string, string> labels)
    {
        if (p.OpenPairs.Count == 0)
        {
            return;
        }

        Section("Open contradictions:");
        foreach (var pair in p.OpenPairs)
        {
            var color = pair.Kind == ContradictionPair.KindSupersession ? "yellow" : "grey";
            Ui.Out.MarkupLine(
                $"  [dim]{Ui.E(pair.Id)}[/]  [{color}]{Ui.E(pair.Kind)}[/]  [dim]{pair.Similarity:F2}[/]  "
                + $"{Ui.E(Label(labels, pair.A.Path))} [dim]↔[/] {Ui.E(Label(labels, pair.B.Path))}");
        }

        if (p.Contradictions.Open > p.OpenPairs.Count)
        {
            Ui.Out.MarkupLine($"  [dim]… {p.Contradictions.Open - p.OpenPairs.Count} more — rtfm contradictions --project {Ui.E(p.Name)}[/]");
        }
    }

    private static void RenderNotes(IReadOnlyList<Note> notes, IReadOnlyDictionary<string, string> labels)
    {
        if (notes.Count == 0)
        {
            return;
        }

        Section("Override notes:");
        foreach (var note in notes)
        {
            Ui.Out.MarkupLine(
                $"  [dim]{Ui.E(note.Id)}[/]  {Ui.E(Truncate(note.Text, 72))}");
            Ui.Out.MarkupLine(
                $"  {new string(' ', note.Id.Length)}  [dim]{Ui.E(note.TargetPath is null ? "project-wide" : Label(labels, note.TargetPath))}"
                + $" · {Ui.E(note.Author)} · {note.CreatedAt.ToLocalTime():yyyy-MM-dd}[/]");
        }
    }

    private static void RenderDocuments(IReadOnlyList<SourceInfo> documents, IReadOnlyDictionary<string, string> labels, bool all)
    {
        if (documents.Count == 0)
        {
            return;
        }

        var ordered = documents
            .OrderByDescending(d => d.ChunkCount)
            .ThenBy(d => d.Path, StringComparer.Ordinal)
            .ToList();
        var shown = all ? ordered : ordered.Take(DefaultDocumentsShown).ToList();

        Section("Documents:");
        Ui.Out.MarkupLine("  [dim]chunks  modified    document[/]");
        foreach (var doc in shown)
        {
            var modified = doc.SourceModifiedAt is { } m ? m.ToString("yyyy-MM-dd") : "—         ";
            Ui.Out.MarkupLine($"  {doc.ChunkCount,6}  {modified}  {Ui.E(Label(labels, doc.Path))}");
        }

        if (shown.Count < ordered.Count)
        {
            Ui.Out.MarkupLine($"  [dim]… {ordered.Count - shown.Count} more — pass --all to list every document[/]");
        }
    }

    /// <summary>Lists the projects that do exist — the k8s "not found" courtesy.</summary>
    private static async Task SuggestProjectsAsync(OpenSearchGateway gateway)
    {
        var known = (await new StatusService(gateway).GetProjectStatusesAsync().ConfigureAwait(false))
            .Select(s => s.Project)
            .Concat(ManifestStore.ListAll().Select(m => m.Project))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Console.Error.WriteLine(known.Count == 0
            ? "Nothing is indexed yet — run 'rtfm index <folder> --project <name>'."
            : $"Known projects: {string.Join(", ", known)}");
    }

    private static string DescribeContradictions(ContradictionSummary c)
    {
        if (c.Total == 0)
        {
            return "[dim]none nominated[/]";
        }

        var closed = c.Dismissed + c.Resolved;
        var open = c.Open == 0
            ? "[green]0 open[/]"
            : $"[yellow]{c.Open} open[/]" + (c.LikelySupersession > 0 ? $" [dim]({c.LikelySupersession} likely-supersession)[/]" : string.Empty);

        return closed == 0
            ? open
            : $"{open}[dim], {closed} closed ({c.Dismissed} dismissed, {c.Resolved} resolved)[/]";
    }

    private static string Span(DateTimeOffset? oldest, DateTimeOffset? newest)
        => oldest is { } o && newest is { } n
            ? o.Date == n.Date ? o.ToString("yyyy-MM-dd") : $"{o:yyyy-MM-dd} [dim]→[/] {n:yyyy-MM-dd}"
            : "[dim]—[/]";

    private static string Moment(DateTimeOffset? when)
        => when is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "[dim]never[/]";

    private static void Field(string label, string valueMarkup)
        => Ui.Out.MarkupLine($"{label.PadRight(LabelWidth)}{valueMarkup}");

    private static void Section(string title)
    {
        Ui.Out.WriteLine();
        Ui.Out.MarkupLine($"[bold]{title}[/]");
    }

    private static string Label(IReadOnlyDictionary<string, string> labels, string path)
        => labels.TryGetValue(path, out var label) ? label : path;

    private static bool IsSynthetic(string path)
        => path.StartsWith("jira://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("confluence://", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string text, int max)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }

    private static string S(long count) => count == 1 ? string.Empty : "s";

    private static (string? Name, bool All, bool Ok) ParseArgs(string[] args)
    {
        string? name = null;
        var all = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--all" or "-a":
                    all = true;
                    break;
                case "--project" or "-p" when i + 1 < args.Length:
                    name = args[++i];
                    break;
                default:
                    if (args[i].StartsWith('-') || name is not null)
                    {
                        return (null, false, false);
                    }

                    name = args[i];
                    break;
            }
        }

        return (name, all, true);
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: rtfm describe project [<name>] [--all]");
        Console.Error.WriteLine("       <name> defaults to RTFM_PROJECT; --all lists every document.");
        return 2;
    }
}
