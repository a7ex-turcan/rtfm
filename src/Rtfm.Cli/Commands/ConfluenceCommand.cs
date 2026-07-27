using Rtfm.Core.Confluence;
using Rtfm.Core.Contradictions;
using Rtfm.Core.Indexing;
using Rtfm.Core.OpenSearch;
using Spectre.Console;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm confluence</c> — the Phase 26 Confluence integration (§2.17), the
/// Confluence twin of <see cref="JiraCommand"/>. <c>config</c> stores a
/// per-project workspace descriptor and verifies auth read-only; <c>index
/// &lt;URL|id&gt;</c> pulls a page and ingests it under <c>confluence://{id}</c>;
/// <c>list</c> shows configured workspaces. Reads only — <see cref="ConfluenceClient"/>
/// has no write path.
/// </summary>
internal static class ConfluenceCommand
{
    /// <summary>The env var the token reference defaults to when <c>--token-env</c> is omitted.</summary>
    private const string DefaultTokenVar = "CONFLUENCE_TOKEN";

    public static async Task<int> RunAsync(string[] args)
    {
        return args.FirstOrDefault() switch
        {
            "config" => await ConfigAsync(args[1..]).ConfigureAwait(false),
            "index" => await IndexAsync(args[1..]).ConfigureAwait(false),
            "watch" => await WatchAsync(args[1..]).ConfigureAwait(false),
            "purge" => await PurgeAsync(args[1..]).ConfigureAwait(false),
            "list" or null => List(),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            """
            usage: rtfm confluence config --url <workspace> --email <you> [--token-env CONFLUENCE_TOKEN]
                                          [--project <name>] [--max-depth <n>] [--max-pages <n>] [--poll <seconds>]
                   rtfm confluence index <PAGE|FOLDER|SPACE-URL | page-id> [--project <name>]
                                         [--space <KEY>] [--depth <n>] [--max-pages <n>] [--dry-run]
                   rtfm confluence watch [--project <name>] [--interval <seconds>] [--once]
                   rtfm confluence purge <PAGE-URL|id> [--project <name>]
                   rtfm confluence purge --all [--project <name>] [--yes]
                   rtfm confluence list

            index accepts a page URL (page + its subtree), a folder URL (its subtree),
            a space URL or --space <KEY> (the whole space), or a bare page id.

            The API token is read from the environment variable named by --token-env
            (default CONFLUENCE_TOKEN); only the reference is stored, never the token.
            """);
        return 2;
    }

    private static async Task<int> ConfigAsync(string[] args)
    {
        string? url = null, email = null, project = null, tokenVar = DefaultTokenVar;
        int? maxDepth = null, maxPages = null, poll = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--url" when i + 1 < args.Length: url = args[++i]; break;
                case "--email" when i + 1 < args.Length: email = args[++i]; break;
                case "--token-env" when i + 1 < args.Length: tokenVar = args[++i]; break;
                case "--project" or "-p" when i + 1 < args.Length: project = args[++i]; break;
                case "--max-depth" when i + 1 < args.Length && int.TryParse(args[i + 1], out var d): maxDepth = d; i++; break;
                case "--max-pages" when i + 1 < args.Length && int.TryParse(args[i + 1], out var m): maxPages = m; i++; break;
                case "--poll" when i + 1 < args.Length && int.TryParse(args[i + 1], out var s): poll = s; i++; break;
                default: return Usage();
            }
        }

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(email))
        {
            Ui.Err.MarkupLine("[red]--url and --email are required.[/]");
            return Usage();
        }

        project ??= "default";

        string baseUrl;
        try
        {
            baseUrl = ConfluenceConfig.NormalizeBaseUrl(url);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            Ui.Err.MarkupLine($"[red]Invalid workspace URL:[/] {Ui.E(ex.Message)}");
            return 1;
        }

        var config = new ConfluenceConfig(
            BaseUrl: baseUrl,
            Email: email.Trim(),
            Token: $"${{{tokenVar}}}",
            MaxDepth: maxDepth ?? ConfluenceConfig.DefaultMaxDepth,
            MaxPages: Math.Clamp(maxPages ?? ConfluenceConfig.DefaultMaxPages, 1, ConfluenceConfig.MaxPagesCeiling),
            PollSeconds: poll ?? ConfluenceConfig.DefaultPollSeconds);

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(tokenVar)))
        {
            ConfluenceConfigStore.Save(project, config);
            Ui.Err.MarkupLine($"[green]Saved[/] Confluence config for project [{Ui.Accent}]{Ui.E(project)}[/] [dim]({Ui.E(baseUrl)})[/].");
            Ui.Err.MarkupLine($"[yellow]Note:[/] environment variable [teal]{Ui.E(tokenVar)}[/] is not set — set it to your API token, then run [italic]rtfm confluence index <URL>[/].");
            return 0;
        }

        using var client = new ConfluenceClient(config);
        var (ok, displayName, error) = await Ui.Err.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Verifying Confluence credentials…", _ => client.VerifyAuthAsync());

        if (!ok)
        {
            Ui.Err.MarkupLine($"[red]Could not authenticate:[/] {Ui.E(error ?? "unknown error")}");
            Ui.Err.MarkupLine("[dim]Config not saved. Check the URL, email, and token.[/]");
            return 1;
        }

        ConfluenceConfigStore.Save(project, config);
        Ui.Err.MarkupLine($"[green]Saved[/] Confluence config for project [{Ui.Accent}]{Ui.E(project)}[/] — authenticated as [bold]{Ui.E(displayName ?? email)}[/] [dim]({Ui.E(baseUrl)})[/].");
        return 0;
    }

    private static async Task<int> IndexAsync(string[] args)
    {
        string? input = null, project = null, space = null;
        int? depth = null, maxPages = null;
        var dryRun = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--project" or "-p" when i + 1 < args.Length: project = args[++i]; break;
                case "--space" when i + 1 < args.Length: space = args[++i]; break;
                case "--depth" when i + 1 < args.Length && int.TryParse(args[i + 1], out var d): depth = d; i++; break;
                case "--max-pages" when i + 1 < args.Length && int.TryParse(args[i + 1], out var m): maxPages = m; i++; break;
                case "--dry-run": dryRun = true; break;
                default:
                    if (input is null) { input = args[i]; }
                    else { return Usage(); }

                    break;
            }
        }

        var seed = ConfluenceSource.ParseSeed(input ?? string.Empty, space);
        if (seed is null)
        {
            Ui.Err.MarkupLine("[red]Nothing to index.[/] Pass a page/folder/space URL, a numeric page id, or [italic]--space <KEY>[/].");
            return Usage();
        }

        project ??= "default";
        var config = ConfluenceConfigStore.Load(project);
        if (config is null)
        {
            Ui.Err.MarkupLine($"[red]No Confluence config for project[/] [bold]{Ui.E(project)}[/]. Run [italic]rtfm confluence config --project {Ui.E(project)} --url <workspace> --email <you>[/] first.");
            return 1;
        }

        var options = new ConfluenceCrawlOptions(
            MaxDepth: depth ?? config.MaxDepth,
            MaxPages: Math.Clamp(maxPages ?? config.MaxPages, 1, ConfluenceConfig.MaxPagesCeiling));

        try
        {
            using var client = new ConfluenceClient(config);
            var crawler = new ConfluenceCrawler(client, new ConfluenceDocumentRenderer());
            var indexedAt = DateTimeOffset.UtcNow;

            if (dryRun)
            {
                var scope = await Ui.Err.Status().Spinner(Spinner.Known.Dots)
                    .StartAsync($"Resolving {seed.Kind.ToString().ToLowerInvariant()} scope…", _ => crawler.ResolveScopeAsync(seed, ConfluenceConfig.MaxPagesCeiling));
                RenderScopePlan(scope, seed, options);
                Ui.Err.MarkupLine("[yellow]Dry run — nothing indexed.[/] Drop [italic]--dry-run[/] to index this set (plus in-body links).");
                return 0;
            }

            var result = await Ui.Err.Status().Spinner(Spinner.Known.Dots)
                .StartAsync($"Crawling {seed.Kind.ToString().ToLowerInvariant()} {Ui.E(seed.Value)} (links depth ≤ {options.MaxDepth})…",
                    async ctx => await crawler.CrawlAsync(seed, config.BaseUrl, indexedAt, options,
                        log: msg => ctx.Status($"[dim]{Ui.E(msg)}[/]")).ConfigureAwait(false));

            if (result.Nodes.Count == 0)
            {
                Ui.Err.MarkupLine($"[red]No pages indexed[/] for {seed.Kind.ToString().ToLowerInvariant()} [bold]{Ui.E(seed.Value)}[/]"
                    + (result.Skipped.Count > 0 ? $" [dim]({result.Skipped.Count} skipped)[/]" : "") + ".");
                return 1;
            }

            using var embedder = await EmbedderProvider.TryCreateAsync().ConfigureAwait(false);
            var gateway = new OpenSearchGateway();
            var ingestor = new DocumentIngestor(new DocumentIndexer(gateway), embedder, new ContradictionDetector(gateway));
            await ingestor.EnsureIndexAsync().ConfigureAwait(false);

            var totalChunks = 0;
            await Ui.Err.Progress()
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn(Spinner.Known.Dots))
                .StartAsync(async pctx =>
                {
                    var task = pctx.AddTask("[bold]Indexing pages[/]", maxValue: result.Nodes.Count);
                    foreach (var node in result.Nodes)
                    {
                        task.Description = $"[bold]Indexing[/] [dim]{Ui.E(node.Page.Title)}[/]";
                        totalChunks += await ingestor.IngestDocumentAsync(
                            ConfluenceSource.Key(node.PageId), node.Rendered.Markdown, node.Rendered.Title, node.Rendered.ModifiedAt, project, indexedAt)
                            .ConfigureAwait(false);
                        task.Increment(1);
                    }
                }).ConfigureAwait(false);

            await ingestor.RefreshAsync().ConfigureAwait(false);

            // Record the crawled pages as the project's monitored set (merging
            // with any earlier crawl) so `rtfm confluence watch` knows what to poll.
            var monitor = ConfluenceMonitorStore.Load(project);
            foreach (var node in result.Nodes)
            {
                monitor.Set(new MonitoredPage(node.PageId, node.Page.VersionNumber));
            }

            monitor.LastPolledAt = indexedAt;
            ConfluenceMonitorStore.Save(project, monitor);

            var scopeIndexed = result.Nodes.Count(n => n.Depth == 0);
            Ui.Err.MarkupLine($"[green]✓[/] Indexed [bold]{result.Nodes.Count}[/] page(s) → [bold]{totalChunks}[/] chunks "
                + $"[dim](project {Ui.E(project)}; {scopeIndexed} in-scope + {result.Nodes.Count - scopeIndexed} linked)[/]"
                + (embedder is null ? " · [yellow]lexical-only[/]" : " · [dim]embedded[/]"));
            Ui.Err.MarkupLine($"[dim]Monitoring {monitor.Count} page(s) in this project — run [italic]rtfm confluence watch --project {Ui.E(project)}[/] to keep them fresh.[/]");
            ReportLeash(result);
            return 0;
        }
        catch (ConfluenceException ex)
        {
            Ui.Err.MarkupLine($"[red]Confluence error:[/] {Ui.E(ex.Message)}");
            return 1;
        }
        catch (InvalidDataException ex)
        {
            Ui.Err.MarkupLine($"[red]Config error:[/] {Ui.E(ex.Message)}");
            return 1;
        }
        catch (HttpRequestException ex)
        {
            Ui.Err.MarkupLine($"[red]Could not reach OpenSearch:[/] {Ui.E(ex.Message)}. Is the stack up? Try [italic]rtfm ping[/].");
            return 1;
        }
    }

    private static void RenderScopePlan(IReadOnlyList<(string Id, string Title)> scope, ConfluenceSeed seed, ConfluenceCrawlOptions options)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title($"[bold]Scope[/] [dim]({seed.Kind.ToString().ToLowerInvariant()} {Ui.E(seed.Value)})[/]")
            .AddColumn("[bold]Page id[/]")
            .AddColumn("[bold]Title[/]");

        const int show = 30;
        foreach (var (id, title) in scope.Take(show))
        {
            table.AddRow($"[{Ui.Accent}]{Ui.E(id)}[/]", Ui.E(title.Length > 70 ? title[..67] + "…" : title));
        }

        if (scope.Count > show)
        {
            table.AddRow("[dim]…[/]", $"[dim]and {scope.Count - show} more[/]");
        }

        Ui.Err.Write(table);
        var willIndex = Math.Min(scope.Count, options.MaxPages);
        Ui.Err.MarkupLine($"[dim]{scope.Count} in-scope page(s); this run would index {willIndex}[/]"
            + (scope.Count > options.MaxPages ? $" [yellow](budget {options.MaxPages} — raise --max-pages for the rest)[/]" : "")
            + $"[dim], then follow in-body links up to depth {options.MaxDepth}.[/]");
    }

    private static void ReportLeash(ConfluenceCrawlResult result)
    {
        if (result.BudgetHit)
        {
            Ui.Err.MarkupLine($"[yellow]Budget reached:[/] pulled {result.Nodes.Count}, [bold]{result.Dropped}[/] more discovered but not followed. Raise [italic]--max-pages[/] to go wider.");
        }

        if (result.Skipped.Count > 0)
        {
            Ui.Err.MarkupLine($"[dim]{result.Skipped.Count} item(s) skipped (folder/whiteboard, deleted, or no permission).[/]");
        }
    }

    /// <summary>Minimum poll interval — a floor so a stray <c>--interval 1</c> can't hammer the API.</summary>
    private const int MinIntervalSeconds = 30;

    private static async Task<int> WatchAsync(string[] args)
    {
        string? project = null;
        int? interval = null;
        var once = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--project" or "-p" when i + 1 < args.Length: project = args[++i]; break;
                case "--interval" when i + 1 < args.Length && int.TryParse(args[i + 1], out var s): interval = s; i++; break;
                case "--once": once = true; break;
                default: return Usage();
            }
        }

        project ??= "default";
        var config = ConfluenceConfigStore.Load(project);
        if (config is null)
        {
            Ui.Err.MarkupLine($"[red]No Confluence config for project[/] [bold]{Ui.E(project)}[/]. Run [italic]rtfm confluence config[/] then [italic]rtfm confluence index[/] first.");
            return 1;
        }

        if (ConfluenceMonitorStore.Load(project).Count == 0)
        {
            Ui.Err.MarkupLine($"[yellow]Nothing to watch[/] in project [bold]{Ui.E(project)}[/]. Run [italic]rtfm confluence index <URL>[/] first to build a monitored set.");
            return 1;
        }

        var pollSeconds = Math.Max(MinIntervalSeconds, interval ?? config.PollSeconds);

        using var embedder = await EmbedderProvider.TryCreateAsync().ConfigureAwait(false);
        var gateway = new OpenSearchGateway();
        var ingestor = new DocumentIngestor(new DocumentIndexer(gateway), embedder, new ContradictionDetector(gateway));

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // clean shutdown rather than hard-kill
            cts.Cancel();
        };

        try
        {
            await ingestor.EnsureIndexAsync(cts.Token).ConfigureAwait(false);
            using var client = new ConfluenceClient(config);
            var renderer = new ConfluenceDocumentRenderer();

            Ui.Err.MarkupLine($"[green]● Watching[/] project [{Ui.Accent}]{Ui.E(project)}[/] — polling Confluence every [bold]{pollSeconds}s[/]"
                + (once ? " [dim](once)[/]" : "") + ". [dim]Ctrl+C to stop.[/]");

            while (!cts.IsCancellationRequested)
            {
                await PollOnceAsync(client, renderer, ingestor, config, project, cts.Token).ConfigureAwait(false);
                if (once)
                {
                    break;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(pollSeconds), cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (!once)
            {
                Console.Error.WriteLine("Stopped.");
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Stopped.");
            return 0;
        }
        catch (InvalidDataException ex)
        {
            Ui.Err.MarkupLine($"[red]Config error:[/] {Ui.E(ex.Message)}");
            return 1;
        }
        catch (HttpRequestException ex)
        {
            Ui.Err.MarkupLine($"[red]Could not reach OpenSearch:[/] {Ui.E(ex.Message)}. Is the stack up? Try [italic]rtfm ping[/].");
            return 1;
        }
    }

    private static async Task PollOnceAsync(
        ConfluenceClient client, ConfluenceDocumentRenderer renderer, DocumentIngestor ingestor, ConfluenceConfig config, string project, CancellationToken cancellationToken)
    {
        // Reload each poll so a concurrent `rtfm confluence index` is picked up.
        var monitor = ConfluenceMonitorStore.Load(project);
        if (monitor.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var stamp = now.ToLocalTime().ToString("HH:mm:ss");

        var versions = await client.FetchVersionsAsync(monitor.Pages.Keys.ToList(), cancellationToken).ConfigureAwait(false);
        var changed = monitor.SelectChanged(versions);

        if (changed.Count == 0)
        {
            monitor.LastPolledAt = now;
            ConfluenceMonitorStore.Save(project, monitor);
            Ui.Err.MarkupLine($"[dim]{stamp}  polled {monitor.Count} page(s) — no changes.[/]");
            return;
        }

        var reindexed = 0;
        var totalChunks = 0;
        foreach (var id in changed)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var page = await client.FetchPageAsync(id, cancellationToken).ConfigureAwait(false);
                if (!page.IsPage)
                {
                    continue;
                }

                var rendered = renderer.Render(page, config.BaseUrl, now);
                var n = await ingestor.IngestDocumentAsync(
                    ConfluenceSource.Key(page.Id), rendered.Markdown, rendered.Title, rendered.ModifiedAt, project, now, cancellationToken).ConfigureAwait(false);
                monitor.Set(new MonitoredPage(page.Id, page.VersionNumber));
                reindexed++;
                totalChunks += n;
                Ui.Err.MarkupLine($"[green]{stamp}[/]  re-indexed [bold]{Ui.E(page.Title)}[/] [dim]({id}) → {n} chunks[/]");
            }
            catch (ConfluenceException ex)
            {
                Ui.Err.MarkupLine($"[yellow]{stamp}[/]  {Ui.E(id)} changed but could not re-pull: {Ui.E(ex.Message)}");
            }
        }

        await ingestor.RefreshAsync(cancellationToken).ConfigureAwait(false);
        monitor.LastPolledAt = now;
        ConfluenceMonitorStore.Save(project, monitor);
        Ui.Err.MarkupLine($"[green]{stamp}[/]  {reindexed}/{changed.Count} changed page(s) re-indexed [dim]→ {totalChunks} chunks[/].");
    }

    private static async Task<int> PurgeAsync(string[] args)
    {
        string? input = null, project = null;
        bool all = false, yes = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--project" or "-p" when i + 1 < args.Length: project = args[++i]; break;
                case "--all": all = true; break;
                case "--yes" or "-y": yes = true; break;
                default:
                    if (input is null) { input = args[i]; }
                    else { return Usage(); }

                    break;
            }
        }

        project ??= "default";

        if (all == (input is not null))
        {
            Ui.Err.MarkupLine("[red]Pass either a page URL/id or --all, not both (or neither).[/]");
            return Usage();
        }

        var gateway = new OpenSearchGateway();
        var detector = new ContradictionDetector(gateway);

        if (all)
        {
            return await PurgeAllAsync(gateway, detector, project, yes).ConfigureAwait(false);
        }

        var pageId = ConfluenceSource.ParsePageId(input!);
        if (pageId is null)
        {
            Ui.Err.MarkupLine($"[red]Could not find a page id in[/] [bold]{Ui.E(input!)}[/]. Pass a page URL or a numeric id.");
            return 1;
        }

        var sourcePath = ConfluenceSource.Key(pageId);
        var deleted = await gateway.DeleteByQueryAsync(RtfmIndex.Name, DeleteQuery(project, sourcePath)).ConfigureAwait(false);
        await gateway.RefreshAsync(RtfmIndex.Name).ConfigureAwait(false);
        await detector.RemoveForPathAsync(sourcePath, onlyOpen: false).ConfigureAwait(false);

        var monitor = ConfluenceMonitorStore.Load(project);
        var wasMonitored = monitor.Remove(pageId);
        if (wasMonitored)
        {
            ConfluenceMonitorStore.Save(project, monitor);
        }

        if (deleted == 0 && !wasMonitored)
        {
            Ui.Err.MarkupLine($"Page [bold]{Ui.E(pageId)}[/] was not indexed under project [{Ui.Accent}]{Ui.E(project)}[/] — nothing to purge.");
            return 0;
        }

        Ui.Err.MarkupLine($"[green]Purged[/] page [bold]{Ui.E(pageId)}[/] from project [{Ui.Accent}]{Ui.E(project)}[/]: "
            + $"[bold]{deleted}[/] chunk{(deleted == 1 ? "" : "s")} deleted"
            + (wasMonitored ? ", removed from the monitored set." : "."));
        return 0;
    }

    private static async Task<int> PurgeAllAsync(OpenSearchGateway gateway, ContradictionDetector detector, string project, bool yes)
    {
        var monitor = ConfluenceMonitorStore.Load(project);
        var count = await CountAsync(gateway, project, exactSourcePath: null).ConfigureAwait(false);

        if (count == 0 && monitor.Count == 0)
        {
            Ui.Err.MarkupLine($"No Confluence pages indexed under project [{Ui.Accent}]{Ui.E(project)}[/] — nothing to purge.");
            return 0;
        }

        Ui.Err.MarkupLine($"Project [{Ui.Accent}]{Ui.E(project)}[/]: [bold]{count}[/] Confluence chunk(s), [bold]{monitor.Count}[/] monitored page(s).");

        if (!yes)
        {
            if (!Ui.Fancy)
            {
                Console.Error.WriteLine("rtfm confluence purge --all: refusing to delete without --yes when not running interactively.");
                return 2;
            }

            if (!Ui.Err.Confirm("[red]Remove all Confluence pages from this project?[/]", defaultValue: false))
            {
                Console.Error.WriteLine("Aborted.");
                return 1;
            }
        }

        var deleted = await gateway.DeleteByQueryAsync(RtfmIndex.Name, DeleteQuery(project, exactSourcePath: null)).ConfigureAwait(false);
        await gateway.RefreshAsync(RtfmIndex.Name).ConfigureAwait(false);

        foreach (var monitoredId in monitor.Pages.Keys)
        {
            await detector.RemoveForPathAsync(ConfluenceSource.Key(monitoredId), onlyOpen: false).ConfigureAwait(false);
        }

        ConfluenceMonitorStore.Remove(project);

        Ui.Err.MarkupLine($"[green]Purged[/] all Confluence pages from project [{Ui.Accent}]{Ui.E(project)}[/]: "
            + $"[bold]{deleted}[/] chunk(s) deleted, monitored set cleared.");
        return 0;
    }

    /// <summary>The bool query matching a project's Confluence chunks — one exact page, or every <c>confluence://</c> doc.</summary>
    private static object ConfluenceChunkQuery(string project, string? exactSourcePath)
    {
        object sourceClause = exactSourcePath is null
            ? new { prefix = new Dictionary<string, string> { ["source_path"] = ConfluenceSource.Scheme } }
            : new { term = new Dictionary<string, string> { ["source_path"] = exactSourcePath } };

        return new
        {
            @bool = new
            {
                must = new object[]
                {
                    sourceClause,
                    new { term = new Dictionary<string, string> { ["project"] = project } },
                },
            },
        };
    }

    private static string DeleteQuery(string project, string? exactSourcePath)
        => System.Text.Json.JsonSerializer.Serialize(new { query = ConfluenceChunkQuery(project, exactSourcePath) });

    private static async Task<long> CountAsync(OpenSearchGateway gateway, string project, string? exactSourcePath)
    {
        if (!await gateway.IndexExistsAsync(RtfmIndex.Name).ConfigureAwait(false))
        {
            return 0;
        }

        var body = System.Text.Json.JsonSerializer.Serialize(new { size = 0, query = ConfluenceChunkQuery(project, exactSourcePath) });
        var json = await gateway.SearchAsync(RtfmIndex.Name, body).ConfigureAwait(false);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("hits").GetProperty("total").GetProperty("value").GetInt64();
    }

    private static int List()
    {
        var configs = ConfluenceConfigStore.List();
        if (configs.Count == 0)
        {
            Ui.Out.MarkupLine("[yellow]No Confluence workspaces configured.[/] Add one with [italic]rtfm confluence config --url <workspace> --email <you>[/].");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title("[bold]Confluence workspaces[/]")
            .AddColumn("[bold]Project[/]")
            .AddColumn("[bold]Workspace[/]")
            .AddColumn("[bold]Email[/]")
            .AddColumn("[bold]Token[/]")
            .AddColumn(new TableColumn("[bold]Depth[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Max[/]").RightAligned());

        foreach (var (project, config) in configs)
        {
            var tokenVar = TokenVarName(config.Token);
            var tokenSet = tokenVar is not null && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(tokenVar));
            table.AddRow(
                $"[{Ui.Accent}]{Ui.E(project)}[/]",
                Ui.E(config.BaseUrl),
                Ui.E(config.Email),
                tokenVar is null ? "[dim]—[/]" : (tokenSet ? $"[green]${{{Ui.E(tokenVar)}}}[/]" : $"[red]${{{Ui.E(tokenVar)}}} (unset)[/]"),
                config.MaxDepth.ToString(),
                config.MaxPages.ToString());
        }

        Ui.Out.Write(table);
        return 0;
    }

    private static string? TokenVarName(string token)
    {
        var t = token.Trim();
        return t.StartsWith("${", StringComparison.Ordinal) && t.EndsWith('}')
            ? t[2..^1]
            : null;
    }
}
