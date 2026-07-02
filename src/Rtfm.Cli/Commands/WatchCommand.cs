using Rtfm.Core.Indexing;
using Rtfm.Core.Manifest;
using Rtfm.Core.OpenSearch;
using Rtfm.Core.Watch;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm watch &lt;folder&gt;</c> — long-running incremental indexer (§2.8,
/// Phase 5). Reconciles against the manifest on start, then keeps the index
/// fresh on change until Ctrl+C. Phase 7: on a live terminal this renders a
/// live dashboard (status, counters, event feed); when redirected it emits the
/// original plain stderr lines, which scripts parse.
/// </summary>
internal static class WatchCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var (folder, project) = CommandArgs.ParseFolderAndProject(args);
        if (folder is null)
        {
            Console.Error.WriteLine("usage: rtfm watch <folder> [--project <name>]");
            return 2;
        }

        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine($"rtfm watch: folder not found: {folder}");
            return 1;
        }

        using var embedder = await EmbedderProvider.TryCreateAsync().ConfigureAwait(false);
        var gateway = new OpenSearchGateway();
        var indexer = new DocumentIndexer(gateway);
        var ingestor = new DocumentIngestor(indexer, embedder, new Rtfm.Core.Contradictions.ContradictionDetector(gateway));
        var manifestStore = ManifestStore.For(folder, project);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // let us shut down cleanly instead of hard-killing
            cts.Cancel();
        };

        try
        {
            if (Ui.Fancy)
            {
                await RunDashboardAsync(folder, project, ingestor, manifestStore, cts.Token).ConfigureAwait(false);
            }
            else
            {
                var watcher = new FolderWatcher(folder, project, ingestor, manifestStore, e => Console.Error.WriteLine(e));
                await watcher.RunAsync(cts.Token).ConfigureAwait(false);
            }

            Console.Error.WriteLine("Stopped.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Stopped.");
            return 0;
        }
    }

    /// <summary>The Phase 7 live view: header + counters + a scrolling event feed.</summary>
    private static async Task RunDashboardAsync(
        string folder, string project, DocumentIngestor ingestor, ManifestStore manifestStore, CancellationToken cancellationToken)
    {
        var state = new DashboardState(folder, project);

        await Ui.Err.Live(state.Render())
            .AutoClear(false)
            .StartAsync(async ctx =>
            {
                void Refresh()
                {
                    lock (state)
                    {
                        ctx.UpdateTarget(state.Render());
                        ctx.Refresh();
                    }
                }

                var watcher = new FolderWatcher(folder, project, ingestor, manifestStore, e =>
                {
                    lock (state)
                    {
                        state.Apply(e);
                    }

                    Refresh();
                });

                // Uptime ticks even when no files change.
                var ticker = Task.Run(async () =>
                {
                    try
                    {
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                            Refresh();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Shutting down.
                    }
                }, CancellationToken.None);

                try
                {
                    await watcher.RunAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    lock (state)
                    {
                        state.MarkStopped();
                    }

                    Refresh();
                    await ticker.ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
    }

    /// <summary>Mutable view model for the live display. All mutation under lock(this).</summary>
    private sealed class DashboardState(string folder, string project)
    {
        private const int FeedSize = 12;

        private readonly DateTime _startedUtc = DateTime.UtcNow;
        private readonly Queue<(DateTime AtUtc, WatchEvent Event)> _feed = new();

        private string _status = "reconciling…";
        private int _indexed;
        private int _deleted;
        private int _failed;

        public void Apply(WatchEvent e)
        {
            switch (e.Kind)
            {
                case WatchEventKind.Watching:
                    _status = "watching";
                    return; // lifecycle, not a feed item
                case WatchEventKind.ReconcileComplete:
                    _status = "watching";
                    break;
                case WatchEventKind.Indexed or WatchEventKind.Reconciled:
                    _indexed++;
                    break;
                case WatchEventKind.Deleted or WatchEventKind.Removed:
                    _deleted++;
                    break;
                case WatchEventKind.Failed or WatchEventKind.WatcherError:
                    _failed++;
                    break;
            }

            _feed.Enqueue((DateTime.UtcNow, e));
            while (_feed.Count > FeedSize)
            {
                _feed.Dequeue();
            }
        }

        public void MarkStopped() => _status = "stopped";

        public IRenderable Render()
        {
            var uptime = DateTime.UtcNow - _startedUtc;
            var statusMarkup = _status switch
            {
                "watching" => "[green]● watching[/]",
                "stopped" => "[red]● stopped[/]",
                _ => $"[yellow]● {_status}[/]",
            };

            var header = new Grid().AddColumn().AddColumn();
            header.AddRow(new Markup("[dim]folder[/]"), new Text(folder));
            header.AddRow(new Markup("[dim]project[/]"), new Markup($"[{Ui.Accent}]{Ui.E(project)}[/]"));
            header.AddRow(
                new Markup("[dim]status[/]"),
                new Markup($"{statusMarkup}  [dim]up {uptime:hh\\:mm\\:ss}[/]  " +
                           $"[green]{_indexed} indexed[/] · [yellow]{_deleted} removed[/] · " +
                           (_failed > 0 ? $"[red]{_failed} failed[/]" : "[dim]0 failed[/]")));

            var feed = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey)
                .AddColumn(new TableColumn("[bold]Time[/]").NoWrap())
                .AddColumn(new TableColumn("[bold]Event[/]").NoWrap())
                .AddColumn("[bold]File[/]")
                .AddColumn(new TableColumn("[bold]Chunks[/]").RightAligned());

            if (_feed.Count == 0)
            {
                feed.AddRow(new Markup("[dim]—[/]"), new Markup("[dim]waiting for changes[/]"), Text.Empty, Text.Empty);
            }

            foreach (var (at, e) in _feed.Reverse())
            {
                feed.AddRow(
                    new Text(at.ToLocalTime().ToString("HH:mm:ss")),
                    new Markup(KindMarkup(e.Kind)),
                    new Text(e.Path ?? e.Detail ?? string.Empty),
                    new Text(e.ChunkCount?.ToString() ?? (e.Kind is WatchEventKind.Failed or WatchEventKind.WatcherError ? Truncate(e.Detail) : string.Empty)));
            }

            return new Panel(new Rows(header, feed))
                .Header($"[bold] RTFM watch [/]")
                .BorderColor(Ui.Accent);
        }

        private static string KindMarkup(WatchEventKind kind) => kind switch
        {
            WatchEventKind.Indexed => "[green]indexed[/]",
            WatchEventKind.Reconciled => "[teal]reconciled[/]",
            WatchEventKind.ReconcileComplete => "[teal]reconcile ✓[/]",
            WatchEventKind.Deleted => "[yellow]deleted[/]",
            WatchEventKind.Removed => "[yellow]removed[/]",
            WatchEventKind.Failed => "[red]failed[/]",
            WatchEventKind.WatcherError => "[red]watcher error[/]",
            _ => "[dim]event[/]",
        };

        private static string Truncate(string? text)
            => text is null ? string.Empty : text.Length <= 40 ? text : text[..40] + "…";
    }
}
