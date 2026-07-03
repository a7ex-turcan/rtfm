using Rtfm.Core.Indexing;
using Rtfm.Core.Manifest;
using Rtfm.Core.OpenSearch;
using Spectre.Console;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm index &lt;folder&gt;</c> — batch (re)index a documentation folder
/// (§2.4). Converts, chunks, and bulk-upserts each supported file via the shared
/// <see cref="DocumentIngestor"/>; per-file failures are reported and skipped so
/// one bad doc doesn't sink the run. Writes a manifest of what it indexed so
/// <c>rtfm watch</c>'s startup reconcile (§2.8) starts from a correct baseline.
/// Phase 7: renders a progress bar + summary table on a live terminal, plain
/// lines when redirected.
/// </summary>
internal static class IndexCommand
{
    private sealed record FileResult(string Name, bool Ok, int Chunks, string? Error);

    public static async Task<int> RunAsync(string[] args)
    {
        var (folder, project) = CommandArgs.ParseFolderAndProject(args);
        if (folder is null)
        {
            Console.Error.WriteLine("usage: rtfm index <folder> [--project <name>]");
            return 2;
        }

        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine($"rtfm index: folder not found: {folder}");
            return 1;
        }

        var files = DocumentIngestor.EnumerateSupportedFiles(folder).ToList();
        if (files.Count == 0)
        {
            Console.Error.WriteLine($"rtfm index: no supported documents (.doc/.docx/.md/.pdf/.xlsx/.csv/.drawio/.png/.jpg/.sql/.rtfmdb) under {folder}");
            return 1;
        }

        // Before the progress display starts: the embedder may log a one-time
        // model download, which must not interleave with a live render.
        using var embedder = await EmbedderProvider.TryCreateAsync().ConfigureAwait(false);
        var gateway = new OpenSearchGateway();
        var indexer = new DocumentIndexer(gateway);
        var ingestor = new DocumentIngestor(indexer, embedder, new Rtfm.Core.Contradictions.ContradictionDetector(gateway));

        var created = await ingestor.EnsureIndexAsync().ConfigureAwait(false);
        Ui.Err.MarkupLine(created
            ? $"Created index [bold]{RtfmIndex.Name}[/]."
            : $"Index [bold]{RtfmIndex.Name}[/] already exists.");

        var manifestStore = ManifestStore.For(folder, project);
        var manifest = new DocumentManifest();
        var indexedAt = DateTimeOffset.UtcNow;
        var results = new List<FileResult>();

        async Task IndexAllAsync(Action<string>? onFile)
        {
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                onFile?.Invoke(name);

                try
                {
                    var n = await ingestor.IngestFileAsync(file, project, indexedAt).ConfigureAwait(false);
                    var info = new FileInfo(file);
                    manifest.Set(PathNormalizer.Normalize(file), new ManifestEntry(info.LastWriteTimeUtc.Ticks, info.Length));
                    results.Add(new FileResult(name, Ok: true, n, Error: null));
                }
                catch (Exception ex)
                {
                    results.Add(new FileResult(name, Ok: false, 0, ex.Message));
                }
            }
        }

        if (Ui.Fancy)
        {
            await Ui.Err.Progress()
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn(Spinner.Known.Dots))
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask($"[bold]Indexing[/] [dim]{Ui.E(folder)}[/]", maxValue: files.Count);
                    await IndexAllAsync(name =>
                    {
                        task.Description = $"[bold]Indexing[/] [dim]{Ui.E(name)}[/]";
                        task.Increment(results.Count == 0 ? 0 : 1);
                    }).ConfigureAwait(false);
                    task.Value = task.MaxValue;
                }).ConfigureAwait(false);
        }
        else
        {
            await IndexAllAsync(null).ConfigureAwait(false);
            foreach (var r in results)
            {
                Console.Error.WriteLine(r.Ok
                    ? $"  indexed {r.Name} → {r.Chunks} chunks"
                    : $"  FAILED {r.Name}: {r.Error}");
            }
        }

        await ingestor.RefreshAsync().ConfigureAwait(false);
        manifestStore.Save(manifest);

        RenderSummary(results, project, embedder is not null);
        return results.Any(r => !r.Ok) && results.All(r => !r.Ok) ? 1 : 0;
    }

    private static void RenderSummary(List<FileResult> results, string project, bool embedded)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(" ")
            .AddColumn("[bold]Document[/]")
            .AddColumn(new TableColumn("[bold]Chunks[/]").RightAligned());

        foreach (var r in results)
        {
            table.AddRow(
                r.Ok ? new Markup("[green]✓[/]") : new Markup("[red]✗[/]"),
                r.Ok ? new Text(r.Name) : new Text($"{r.Name} — {r.Error}", new Style(Color.Red)),
                new Text(r.Ok ? r.Chunks.ToString() : "—"));
        }

        Ui.Err.Write(table);

        var ok = results.Count(r => r.Ok);
        var failed = results.Count - ok;
        var chunks = results.Sum(r => r.Chunks);
        var summary = $"[bold]{ok}[/] docs · [bold]{chunks}[/] chunks → [bold]{RtfmIndex.Name}[/] · project [{Ui.Accent}]{Ui.E(project)}[/]"
            + (embedded ? " · [dim]embedded[/]" : " · [yellow]lexical-only[/]")
            + (failed > 0 ? $" · [red]{failed} failed[/]" : string.Empty);
        Ui.Err.MarkupLine(summary);
    }
}
