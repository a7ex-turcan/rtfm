using Rtfm.Core.Indexing;
using Rtfm.Core.Manifest;
using Rtfm.Core.OpenSearch;
using Rtfm.Core.Watch;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm watch &lt;folder...&gt;</c> — long-running incremental indexer (§2.8,
/// Phase 5). Reconciles against the manifest on start, then keeps the index
/// fresh on change until Ctrl+C. Phase 7: on a live terminal this renders a
/// live dashboard (status, counters, event feed); when redirected it emits the
/// original plain stderr lines, which scripts parse.
///
/// Watches one or more folders in a single process. Multiple folders under one
/// <c>--project</c> (<c>rtfm watch a b --project foo</c>) or every previously
/// indexed folder via <c>--all</c> (optionally filtered by <c>--project</c>).
/// All folders share one embedding model (~100–200 MB) and one ingestor, whose
/// ingest work is serialized by a gate (§FolderWatcher.ingestGate).
/// </summary>
internal static class WatchCommand
{
    private readonly record struct Target(string Folder, string Project);

    public static async Task<int> RunAsync(string[] args)
    {
        var (folders, project, all, ok) = CommandArgs.ParseWatchTargets(args);
        if (!ok)
        {
            PrintUsage();
            return 2;
        }

        if (all && folders.Count > 0)
        {
            Console.Error.WriteLine("rtfm watch: --all watches every known folder — don't also pass folders.");
            return 2;
        }

        if (!all && folders.Count == 0)
        {
            PrintUsage();
            return 2;
        }

        var (targets, resolveCode) = all
            ? ResolveAllTargets(project)
            : ResolveExplicitTargets(folders, project ?? "default");

        if (targets.Count == 0)
        {
            return resolveCode;
        }

        using var embedder = await EmbedderProvider.TryCreateAsync().ConfigureAwait(false);
        var gateway = new OpenSearchGateway();
        var indexer = new DocumentIndexer(gateway);
        var ingestor = new DocumentIngestor(indexer, embedder, new Rtfm.Core.Contradictions.ContradictionDetector(gateway));

        // >1 folder shares one ingestor → serialize their ingest work. Single
        // folder passes null so its behavior is byte-for-byte as before.
        using var ingestGate = targets.Count > 1 ? new SemaphoreSlim(1, 1) : null;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // let us shut down cleanly instead of hard-killing
            cts.Cancel();
        };

        try
        {
            // Create the index once up front so the watchers don't race to
            // create it (their own EnsureIndexAsync then takes the idempotent
            // "already exists" path).
            await ingestor.EnsureIndexAsync(cts.Token).ConfigureAwait(false);

            if (Ui.Fancy)
            {
                await RunDashboardAsync(targets, ingestor, ingestGate, cts.Token).ConfigureAwait(false);
            }
            else
            {
                await RunPlainAsync(targets, ingestor, ingestGate, cts.Token).ConfigureAwait(false);
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

    /// <summary>Explicit positional folders, all under one project. Any missing folder is a hard error.</summary>
    private static (IReadOnlyList<Target> Targets, int Code) ResolveExplicitTargets(IReadOnlyList<string> folders, string project)
    {
        var targets = new List<Target>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var missing = false;

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                Console.Error.WriteLine($"rtfm watch: folder not found: {folder}");
                missing = true;
                continue;
            }

            // De-dup the same folder given twice (normalized key).
            if (seen.Add(PathNormalizer.Normalize(folder)))
            {
                targets.Add(new Target(folder, project));
            }
        }

        return missing ? ([], 1) : (targets, 0);
    }

    /// <summary>Every previously indexed folder from the manifests, optionally filtered by project.</summary>
    private static (IReadOnlyList<Target> Targets, int Code) ResolveAllTargets(string? project)
    {
        var manifests = ManifestStore.ListAll()
            .Where(m => project is null || string.Equals(m.Project, project, StringComparison.Ordinal))
            .ToList();

        if (manifests.Count == 0)
        {
            Console.Error.WriteLine(project is null
                ? "rtfm watch --all: no watch manifests yet — run 'rtfm index <folder>' first."
                : $"rtfm watch --all: nothing indexed under project '{project}'.");
            return ([], 1);
        }

        var targets = new List<Target>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var m in manifests)
        {
            var folder = m.OpenableFolder;
            if (!Directory.Exists(folder))
            {
                // A manifest whose folder was moved/deleted since indexing — skip
                // it (not fatal) rather than abort the whole --all run.
                Console.Error.WriteLine($"rtfm watch --all: skipping missing folder '{folder}' (project '{m.Project}').");
                continue;
            }

            if (seen.Add($"{PathNormalizer.Normalize(folder)}|{m.Project}"))
            {
                targets.Add(new Target(folder, m.Project));
            }
        }

        return targets.Count == 0 ? ([], 1) : (targets, 0);
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("usage: rtfm watch <folder...> [--project <name>]");
        Console.Error.WriteLine("       rtfm watch --all [--project <name>]");
    }

    /// <summary>Redirected / non-interactive: each watcher writes the pinned plain event lines to stderr.</summary>
    private static Task RunPlainAsync(
        IReadOnlyList<Target> targets, DocumentIngestor ingestor, SemaphoreSlim? ingestGate, CancellationToken cancellationToken)
    {
        var runs = targets.Select(t =>
        {
            var watcher = new FolderWatcher(
                t.Folder, t.Project, ingestor, ManifestStore.For(t.Folder, t.Project),
                e => Console.Error.WriteLine(e), ingestGate: ingestGate);
            return watcher.RunAsync(cancellationToken);
        });

        return Task.WhenAll(runs);
    }

    /// <summary>The Phase 7 live view: header + counters + a scrolling event feed, now spanning N folders.</summary>
    private static async Task RunDashboardAsync(
        IReadOnlyList<Target> targets, DocumentIngestor ingestor, SemaphoreSlim? ingestGate, CancellationToken cancellationToken)
    {
        var state = new DashboardState(targets.Select(t => (t.Folder, t.Project)).ToList());

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

                var runs = targets.Select(t =>
                {
                    var label = state.LabelFor(t.Folder, t.Project);
                    var watcher = new FolderWatcher(t.Folder, t.Project, ingestor, ManifestStore.For(t.Folder, t.Project), e =>
                    {
                        lock (state)
                        {
                            state.Apply(label, e);
                        }

                        Refresh();
                    }, ingestGate: ingestGate);

                    return watcher.RunAsync(cancellationToken);
                }).ToList();

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
                    await Task.WhenAll(runs).ConfigureAwait(false);
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
    private sealed class DashboardState
    {
        private const int FeedSize = 12;

        private readonly IReadOnlyList<(string Folder, string Project)> _targets;
        private readonly bool _multi;
        private readonly DateTime _startedUtc = DateTime.UtcNow;
        private readonly Queue<(DateTime AtUtc, string Source, WatchEvent Event)> _feed = new();

        private string _status = "reconciling…";
        private int _indexed;
        private int _deleted;
        private int _failed;

        public DashboardState(IReadOnlyList<(string Folder, string Project)> targets)
        {
            _targets = targets;
            _multi = targets.Count > 1;
        }

        /// <summary>The short attribution shown in the feed's Source column: folder leaf, prefixed by project when projects differ.</summary>
        public string LabelFor(string folder, string project)
        {
            var leaf = Path.GetFileName(folder.TrimEnd('/', '\\'));
            if (string.IsNullOrEmpty(leaf))
            {
                leaf = folder;
            }

            var projectsDiffer = _targets.Select(t => t.Project).Distinct(StringComparer.Ordinal).Count() > 1;
            return projectsDiffer ? $"{project}/{leaf}" : leaf;
        }

        public void Apply(string source, WatchEvent e)
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

            _feed.Enqueue((DateTime.UtcNow, source, e));
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

            if (_multi)
            {
                var projects = _targets.Select(t => t.Project).Distinct(StringComparer.Ordinal).ToList();
                header.AddRow(
                    new Markup("[dim]watching[/]"),
                    new Markup($"[{Ui.Accent}]{_targets.Count}[/] folders across " +
                               $"[{Ui.Accent}]{projects.Count}[/] project{(projects.Count == 1 ? "" : "s")} " +
                               $"[dim]({Ui.E(string.Join(", ", projects))})[/]"));
                foreach (var (folder, project) in _targets)
                {
                    header.AddRow(new Markup("[dim] •[/]"), new Markup($"{Ui.E(folder)}  [dim]→[/]  [{Ui.Accent}]{Ui.E(project)}[/]"));
                }
            }
            else
            {
                var (folder, project) = _targets[0];
                header.AddRow(new Markup("[dim]folder[/]"), new Text(folder));
                header.AddRow(new Markup("[dim]project[/]"), new Markup($"[{Ui.Accent}]{Ui.E(project)}[/]"));
            }

            header.AddRow(
                new Markup("[dim]status[/]"),
                new Markup($"{statusMarkup}  [dim]up {uptime:hh\\:mm\\:ss}[/]  " +
                           $"[green]{_indexed} indexed[/] · [yellow]{_deleted} removed[/] · " +
                           (_failed > 0 ? $"[red]{_failed} failed[/]" : "[dim]0 failed[/]")));

            var feed = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey)
                .AddColumn(new TableColumn("[bold]Time[/]").NoWrap())
                .AddColumn(new TableColumn("[bold]Event[/]").NoWrap());

            if (_multi)
            {
                feed.AddColumn(new TableColumn("[bold]Source[/]").NoWrap());
            }

            feed.AddColumn("[bold]File[/]")
                .AddColumn(new TableColumn("[bold]Chunks[/]").RightAligned());

            if (_feed.Count == 0)
            {
                IRenderable[] blanks = _multi
                    ? [new Markup("[dim]—[/]"), new Markup("[dim]waiting for changes[/]"), Text.Empty, Text.Empty, Text.Empty]
                    : [new Markup("[dim]—[/]"), new Markup("[dim]waiting for changes[/]"), Text.Empty, Text.Empty];
                feed.AddRow(blanks);
            }

            foreach (var (at, source, e) in _feed.Reverse())
            {
                var cells = new List<IRenderable>
                {
                    new Text(at.ToLocalTime().ToString("HH:mm:ss")),
                    new Markup(KindMarkup(e.Kind)),
                };

                if (_multi)
                {
                    cells.Add(new Markup($"[dim]{Ui.E(source)}[/]"));
                }

                cells.Add(new Text(e.Path ?? e.Detail ?? string.Empty));
                cells.Add(new Text(e.ChunkCount?.ToString() ?? (e.Kind is WatchEventKind.Failed or WatchEventKind.WatcherError ? Truncate(e.Detail) : string.Empty)));
                feed.AddRow(cells);
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
