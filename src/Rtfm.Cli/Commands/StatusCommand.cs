using Rtfm.Core.Configuration;
using Rtfm.Core.Embeddings;
using Rtfm.Core.Manifest;
using Rtfm.Core.OpenSearch;
using Rtfm.Core.Search;
using Spectre.Console;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm status [--project &lt;name&gt;] [--stale &lt;days&gt;]</c> — Phase 10
/// observability. Environment health (OpenSearch, model cache, watch
/// manifests) plus per-project rollups: docs, chunks, vector coverage, recency
/// spread, last index time. <c>--stale</c> lists documents whose
/// <c>source_modified_at</c> is older than the window — the corpus is manual
/// exports, so age is the drift signal (§2.13, Confluence pull is a non-goal).
/// </summary>
internal static class StatusCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var (project, staleDays, ok) = ParseArgs(args);
        if (!ok)
        {
            Console.Error.WriteLine("usage: rtfm status [--project <name>] [--stale <days>]");
            return 2;
        }

        var gateway = new OpenSearchGateway();
        var health = await Ui.Err.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Checking environment…", _ => gateway.PingAsync());

        var modelStore = new EmbeddingModelStore();
        var manifests = ManifestStore.ListAll()
            .Where(m => project is null || string.Equals(m.Project, project, StringComparison.Ordinal))
            .ToList();

        RenderEnvironment(health, gateway, modelStore, manifests.Count);
        if (!health.Reachable)
        {
            return 1;
        }

        var statuses = await new StatusService(gateway).GetProjectStatusesAsync(project).ConfigureAwait(false);
        if (statuses.Count == 0)
        {
            Ui.Out.MarkupLine(project is null
                ? "[yellow]Index is empty or missing[/] — run [bold]rtfm index <folder>[/] first."
                : $"[yellow]Nothing indexed under project[/] [{Ui.Accent}]{Ui.E(project)}[/].");
            return 0;
        }

        RenderProjects(statuses);

        if (manifests.Count > 0)
        {
            RenderManifests(manifests);
        }

        if (staleDays is { } days)
        {
            await RenderStaleAsync(gateway, project, days).ConfigureAwait(false);
        }

        return 0;
    }

    private static void RenderEnvironment(
        Rtfm.Core.OpenSearch.ClusterHealthResult health, OpenSearchGateway gateway, EmbeddingModelStore modelStore, int manifestCount)
    {
        var grid = new Grid().AddColumn().AddColumn();

        grid.AddRow(new Markup("[dim]OpenSearch[/]"), new Markup(health.Reachable
            ? $"[{(health.Status == "green" ? "green" : health.Status == "yellow" ? "yellow" : "red")}]● {Ui.E(health.Status ?? "?")}[/]  [dim]{Ui.E(gateway.Endpoint.ToString())}[/]"
            : $"[red]● unreachable[/]  [dim]{Ui.E(gateway.Endpoint.ToString())} — docker compose up -d?[/]"));

        grid.AddRow(new Markup("[dim]embedding model[/]"), new Markup(modelStore.IsCached
            ? $"[green]cached[/]  [dim]{Ui.E(modelStore.ModelPath)}[/]"
            : "[yellow]not downloaded[/]  [dim](first index/search fetches it; lexical-only until then)[/]"));

        grid.AddRow(new Markup("[dim]watch manifests[/]"), new Markup(manifestCount == 0
            ? "[dim]none[/]"
            : $"{manifestCount}"));

        Ui.Out.Write(new Panel(grid).Header("[bold] Environment [/]").BorderColor(health.Reachable ? Color.Grey : Color.Red));
    }

    private static void RenderProjects(IReadOnlyList<ProjectStatus> statuses)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title("[bold]Projects[/]")
            .AddColumn("[bold]Project[/]")
            .AddColumn(new TableColumn("[bold]Docs[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Chunks[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Vectors[/]").RightAligned())
            .AddColumn("[bold]Sources span[/]")
            .AddColumn("[bold]Last indexed[/]");

        foreach (var s in statuses)
        {
            var coverage = s.VectorCoverage switch
            {
                >= 0.999 => "[green]100%[/]",
                0 => "[yellow]none[/]",
                _ => $"[yellow]{s.VectorCoverage:P0}[/]",
            };

            var span = s.OldestSourceModified is { } oldest && s.NewestSourceModified is { } newest
                ? oldest.Date == newest.Date
                    ? newest.ToString("yyyy-MM-dd")
                    : $"{oldest:yyyy-MM-dd} → {newest:yyyy-MM-dd}"
                : "—";

            table.AddRow(
                new Markup($"[{Ui.Accent}]{Ui.E(s.Project)}[/]"),
                new Text(s.DocCount.ToString()),
                new Text(s.ChunkCount.ToString()),
                new Markup(coverage),
                new Text(span),
                new Text(s.LastIndexedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—"));
        }

        Ui.Out.Write(table);
    }

    private static void RenderManifests(IReadOnlyList<ManifestInfo> manifests)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title("[bold]Watch manifests[/]")
            .AddColumn("[bold]Project[/]")
            .AddColumn("[bold]Folder[/]")
            .AddColumn(new TableColumn("[bold]Files[/]").RightAligned())
            .AddColumn("[bold]Updated[/]");

        foreach (var m in manifests)
        {
            table.AddRow(
                new Markup($"[{Ui.Accent}]{Ui.E(m.Project)}[/]"),
                new Text(m.Folder),
                new Text(m.TrackedFiles.ToString()),
                new Text(new DateTimeOffset(m.UpdatedUtc, TimeSpan.Zero).ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
        }

        Ui.Out.Write(table);
    }

    private static async Task RenderStaleAsync(OpenSearchGateway gateway, string? project, int days)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        var stale = (await new DocumentCatalog(gateway).ListSourcesAsync(project).ConfigureAwait(false))
            .Where(s => s.SourceModifiedAt is { } m && m < cutoff)
            .OrderBy(s => s.SourceModifiedAt)
            .ToList();

        if (stale.Count == 0)
        {
            Ui.Out.MarkupLine($"[green]No documents older than {days} days.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Yellow)
            .Title($"[bold yellow]Stale (> {days} days old)[/]")
            .AddColumn("[bold]Project[/]")
            .AddColumn("[bold]Document[/]")
            .AddColumn("[bold]Modified[/]")
            .AddColumn(new TableColumn("[bold]Age[/]").RightAligned());

        foreach (var s in stale)
        {
            var age = (int)(DateTimeOffset.UtcNow - s.SourceModifiedAt!.Value).TotalDays;
            table.AddRow(
                new Markup($"[{Ui.Accent}]{Ui.E(s.Project)}[/]"),
                new Text(Path.GetFileName(s.Path)),
                new Text(s.SourceModifiedAt.Value.ToString("yyyy-MM-dd")),
                new Markup($"[yellow]{age}d[/]"));
        }

        Ui.Out.Write(table);
        Ui.Out.MarkupLine("[dim]The corpus is manual exports — consider re-exporting these from the source wiki.[/]");
    }

    private static (string? Project, int? StaleDays, bool Ok) ParseArgs(string[] args)
    {
        string? project = null;
        int? staleDays = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--project" or "-p" when i + 1 < args.Length:
                    project = args[++i];
                    break;
                case "--stale" when i + 1 < args.Length && int.TryParse(args[i + 1], out var days) && days > 0:
                    staleDays = days;
                    i++;
                    break;
                default:
                    return (null, null, false);
            }
        }

        return (project, staleDays, true);
    }
}
