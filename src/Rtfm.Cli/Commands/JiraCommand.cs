using Rtfm.Core.Contradictions;
using Rtfm.Core.Indexing;
using Rtfm.Core.Jira;
using Rtfm.Core.OpenSearch;
using Spectre.Console;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm jira</c> — the Phase 25 Jira integration (§2.16). <c>config</c> stores
/// a per-project workspace descriptor (URL + email + a <c>${ENV}</c> token
/// reference) and verifies auth read-only; <c>index &lt;KEY&gt;</c> pulls a
/// ticket and ingests it as thread-granular chunks under <c>jira://KEY</c>;
/// <c>list</c> shows configured workspaces. Reads only — <see cref="JiraClient"/>
/// has no write path.
/// </summary>
internal static class JiraCommand
{
    /// <summary>The env var the token reference defaults to when <c>--token-env</c> is omitted.</summary>
    private const string DefaultTokenVar = "JIRA_TOKEN";

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
            usage: rtfm jira config --url <workspace> --email <you> [--token-env JIRA_TOKEN]
                                    [--project <name>] [--max-depth <n>] [--max-tickets <n>]
                                    [--follow-mentions] [--poll <seconds>]
                   rtfm jira index <ISSUE-KEY> [--project <name>] [--depth <n>]
                                    [--max-tickets <n>] [--follow-mentions] [--dry-run]
                   rtfm jira watch [--project <name>] [--interval <seconds>] [--once]
                   rtfm jira purge <ISSUE-KEY> [--project <name>]
                   rtfm jira purge --all [--project <name>] [--yes]
                   rtfm jira list

            The API token is read from the environment variable named by --token-env
            (default JIRA_TOKEN); only the reference is stored, never the token.
            """);
        return 2;
    }

    private static async Task<int> ConfigAsync(string[] args)
    {
        string? url = null, email = null, project = null, tokenVar = DefaultTokenVar;
        int? maxDepth = null, maxTickets = null, poll = null;
        var followMentions = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--url" when i + 1 < args.Length: url = args[++i]; break;
                case "--email" when i + 1 < args.Length: email = args[++i]; break;
                case "--token-env" when i + 1 < args.Length: tokenVar = args[++i]; break;
                case "--project" or "-p" when i + 1 < args.Length: project = args[++i]; break;
                case "--max-depth" when i + 1 < args.Length && int.TryParse(args[i + 1], out var d): maxDepth = d; i++; break;
                case "--max-tickets" when i + 1 < args.Length && int.TryParse(args[i + 1], out var t): maxTickets = t; i++; break;
                case "--poll" when i + 1 < args.Length && int.TryParse(args[i + 1], out var s): poll = s; i++; break;
                case "--follow-mentions": followMentions = true; break;
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
            baseUrl = Rtfm.Core.Jira.JiraConfig.NormalizeBaseUrl(url);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            Ui.Err.MarkupLine($"[red]Invalid workspace URL:[/] {Ui.E(ex.Message)}");
            return 1;
        }

        var config = new JiraConfig(
            BaseUrl: baseUrl,
            Email: email.Trim(),
            Token: $"${{{tokenVar}}}",
            MaxDepth: maxDepth ?? JiraConfig.DefaultMaxDepth,
            MaxTickets: Math.Clamp(maxTickets ?? JiraConfig.DefaultMaxTickets, 1, JiraConfig.MaxTicketsCeiling),
            FollowMentions: followMentions,
            PollSeconds: poll ?? JiraConfig.DefaultPollSeconds);

        // Verify against the live API when the token env var is present; otherwise
        // save anyway (the reference is valid) and tell the user to set it.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(tokenVar)))
        {
            JiraConfigStore.Save(project, config);
            Ui.Err.MarkupLine($"[green]Saved[/] Jira config for project [{Ui.Accent}]{Ui.E(project)}[/] [dim]({Ui.E(baseUrl)})[/].");
            Ui.Err.MarkupLine($"[yellow]Note:[/] environment variable [teal]{Ui.E(tokenVar)}[/] is not set — set it to your API token, then run [italic]rtfm jira index <KEY>[/].");
            return 0;
        }

        using var client = new JiraClient(config);
        var (ok, displayName, error) = await Ui.Err.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Verifying Jira credentials…", _ => client.VerifyAuthAsync());

        if (!ok)
        {
            Ui.Err.MarkupLine($"[red]Could not authenticate:[/] {Ui.E(error ?? "unknown error")}");
            Ui.Err.MarkupLine("[dim]Config not saved. Check the URL, email, and token.[/]");
            return 1;
        }

        JiraConfigStore.Save(project, config);
        Ui.Err.MarkupLine($"[green]Saved[/] Jira config for project [{Ui.Accent}]{Ui.E(project)}[/] — authenticated as [bold]{Ui.E(displayName ?? email)}[/] [dim]({Ui.E(baseUrl)})[/].");
        return 0;
    }

    private static async Task<int> IndexAsync(string[] args)
    {
        string? key = null, project = null;
        int? depth = null, maxTickets = null;
        bool followMentions = false, dryRun = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--project" or "-p" when i + 1 < args.Length: project = args[++i]; break;
                case "--depth" when i + 1 < args.Length && int.TryParse(args[i + 1], out var d): depth = d; i++; break;
                case "--max-tickets" when i + 1 < args.Length && int.TryParse(args[i + 1], out var m): maxTickets = m; i++; break;
                case "--follow-mentions": followMentions = true; break;
                case "--dry-run": dryRun = true; break;
                default:
                    if (key is null) { key = args[i]; }
                    else { return Usage(); }

                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return Usage();
        }

        project ??= "default";
        var config = JiraConfigStore.Load(project);
        if (config is null)
        {
            Ui.Err.MarkupLine($"[red]No Jira config for project[/] [bold]{Ui.E(project)}[/]. Run [italic]rtfm jira config --project {Ui.E(project)} --url <workspace> --email <you>[/] first.");
            return 1;
        }

        var options = new JiraCrawlOptions(
            MaxDepth: depth ?? config.MaxDepth,
            MaxTickets: Math.Clamp(maxTickets ?? config.MaxTickets, 1, JiraConfig.MaxTicketsCeiling),
            FollowMentions: followMentions || config.FollowMentions);

        try
        {
            using var client = new JiraClient(config);
            var crawler = new JiraCrawler(client, new JiraDocumentRenderer());
            var indexedAt = DateTimeOffset.UtcNow;
            var seed = key.Trim().ToUpperInvariant();

            var result = await Ui.Err.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Crawling from {Ui.E(seed)} (depth ≤ {options.MaxDepth})…", async ctx =>
                    await crawler.CrawlAsync(seed, config.BaseUrl, indexedAt, options,
                        log: msg => ctx.Status($"[dim]{Ui.E(msg)}[/]")).ConfigureAwait(false));

            if (result.Nodes.Count == 0)
            {
                Ui.Err.MarkupLine($"[red]Nothing pulled[/] for [bold]{Ui.E(seed)}[/]" + (result.Skipped.Count > 0 ? $" [dim]({result.Skipped.Count} skipped)[/]" : "") + ".");
                return 1;
            }

            if (dryRun)
            {
                RenderCrawlPlan(result, seed, options);
                Ui.Err.MarkupLine("[yellow]Dry run — nothing indexed.[/] Drop [italic]--dry-run[/] to index this set.");
                return 0;
            }

            using var embedder = await EmbedderProvider.TryCreateAsync().ConfigureAwait(false);
            var gateway = new OpenSearchGateway();
            var indexer = new DocumentIndexer(gateway);
            var ingestor = new DocumentIngestor(indexer, embedder, new ContradictionDetector(gateway));
            await ingestor.EnsureIndexAsync().ConfigureAwait(false);

            var totalChunks = 0;
            await Ui.Err.Progress()
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn(Spinner.Known.Dots))
                .StartAsync(async pctx =>
                {
                    var task = pctx.AddTask("[bold]Indexing tickets[/]", maxValue: result.Nodes.Count);
                    foreach (var node in result.Nodes)
                    {
                        task.Description = $"[bold]Indexing[/] [dim]{Ui.E(node.Key)}[/]";
                        totalChunks += await ingestor.IngestDocumentAsync(
                            JiraSource.Key(node.Key), node.Rendered.Markdown, node.Rendered.Title, node.Rendered.ModifiedAt, project, indexedAt)
                            .ConfigureAwait(false);
                        task.Increment(1);
                    }
                }).ConfigureAwait(false);

            await ingestor.RefreshAsync().ConfigureAwait(false);

            // Record the crawled set as the project's monitored tickets (merging
            // with any earlier crawl) so `rtfm jira watch` knows what to re-poll.
            var monitor = JiraMonitorStore.Load(project);
            foreach (var node in result.Nodes)
            {
                monitor.Set(new MonitoredTicket(node.Key, node.Issue.Updated, Full: node.Depth == 0));
            }

            monitor.LastPolledAt = indexedAt;
            JiraMonitorStore.Save(project, monitor);

            Ui.Err.MarkupLine($"[green]✓[/] Indexed [bold]{result.Nodes.Count}[/] ticket(s) → [bold]{totalChunks}[/] chunks "
                + $"[dim](project {Ui.E(project)})[/]"
                + (embedder is null ? " · [yellow]lexical-only[/]" : " · [dim]embedded[/]"));
            Ui.Err.MarkupLine($"[dim]Monitoring {monitor.Count} ticket(s) in this project — run [italic]rtfm jira watch --project {Ui.E(project)}[/] to keep them fresh.[/]");
            ReportLeash(result);
            return 0;
        }
        catch (JiraException ex)
        {
            Ui.Err.MarkupLine($"[red]Jira error:[/] {Ui.E(ex.Message)}");
            return 1;
        }
        catch (InvalidDataException ex)
        {
            // Token env var missing (lazy expansion, §2.16).
            Ui.Err.MarkupLine($"[red]Config error:[/] {Ui.E(ex.Message)}");
            return 1;
        }
        catch (HttpRequestException ex)
        {
            Ui.Err.MarkupLine($"[red]Could not reach OpenSearch:[/] {Ui.E(ex.Message)}. Is the stack up? Try [italic]rtfm ping[/].");
            return 1;
        }
    }

    private static void RenderCrawlPlan(JiraCrawlResult result, string seed, JiraCrawlOptions options)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title($"[bold]Crawl plan[/] [dim]from {Ui.E(seed)}, depth ≤ {options.MaxDepth}, budget {options.MaxTickets}[/]")
            .AddColumn(new TableColumn("[bold]Depth[/]").RightAligned())
            .AddColumn("[bold]Key[/]")
            .AddColumn("[bold]Title[/]");

        foreach (var node in result.Nodes)
        {
            var title = node.Rendered.Title;
            table.AddRow(node.Depth.ToString(), $"[{Ui.Accent}]{Ui.E(node.Key)}[/]", Ui.E(title.Length > 70 ? title[..67] + "…" : title));
        }

        Ui.Err.Write(table);
        ReportLeash(result);
    }

    private static void ReportLeash(JiraCrawlResult result)
    {
        if (result.BudgetHit)
        {
            Ui.Err.MarkupLine($"[yellow]Budget reached:[/] pulled {result.Nodes.Count}, [bold]{result.Dropped}[/] more discovered but not followed. Raise [italic]--max-tickets[/] to go wider.");
        }

        if (result.Skipped.Count > 0)
        {
            Ui.Err.MarkupLine($"[dim]{result.Skipped.Count} linked ticket(s) skipped (deleted or no permission): {Ui.E(string.Join(", ", result.Skipped.Take(8)))}{(result.Skipped.Count > 8 ? "…" : "")}[/]");
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
        var config = JiraConfigStore.Load(project);
        if (config is null)
        {
            Ui.Err.MarkupLine($"[red]No Jira config for project[/] [bold]{Ui.E(project)}[/]. Run [italic]rtfm jira config[/] then [italic]rtfm jira index[/] first.");
            return 1;
        }

        if (JiraMonitorStore.Load(project).Count == 0)
        {
            Ui.Err.MarkupLine($"[yellow]Nothing to watch[/] in project [bold]{Ui.E(project)}[/]. Run [italic]rtfm jira index <KEY>[/] first to build a monitored set.");
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
            using var client = new JiraClient(config);
            var renderer = new JiraDocumentRenderer();

            Ui.Err.MarkupLine($"[green]● Watching[/] project [{Ui.Accent}]{Ui.E(project)}[/] — polling Jira every [bold]{pollSeconds}s[/]"
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
        JiraClient client, JiraDocumentRenderer renderer, DocumentIngestor ingestor, JiraConfig config, string project, CancellationToken cancellationToken)
    {
        // Reload each poll so a concurrent `rtfm jira index` (which adds tickets)
        // is picked up without restarting the watcher.
        var monitor = JiraMonitorStore.Load(project);
        if (monitor.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var stamp = now.ToLocalTime().ToString("HH:mm:ss");

        var stamps = await client.FetchUpdatedAsync(monitor.Tickets.Keys.ToList(), cancellationToken).ConfigureAwait(false);
        var changed = monitor.SelectChanged(stamps);

        if (changed.Count == 0)
        {
            monitor.LastPolledAt = now;
            JiraMonitorStore.Save(project, monitor);
            Ui.Err.MarkupLine($"[dim]{stamp}  polled {monitor.Count} ticket(s) — no changes.[/]");
            return;
        }

        var reindexed = 0;
        var totalChunks = 0;
        foreach (var key in changed)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var entry = monitor.Tickets[key];
            try
            {
                var issue = await client.FetchIssueAsync(key, includeComments: entry.Full, cancellationToken).ConfigureAwait(false);
                var rendered = renderer.Render(issue, config.BaseUrl, now);
                var n = await ingestor.IngestDocumentAsync(
                    JiraSource.Key(issue.Key), rendered.Markdown, rendered.Title, rendered.ModifiedAt, project, now, cancellationToken).ConfigureAwait(false);
                monitor.Set(new MonitoredTicket(issue.Key, issue.Updated, entry.Full));
                reindexed++;
                totalChunks += n;
                Ui.Err.MarkupLine($"[green]{stamp}[/]  re-indexed [bold]{Ui.E(key)}[/] [dim]→ {n} chunks[/]");
            }
            catch (JiraException ex)
            {
                Ui.Err.MarkupLine($"[yellow]{stamp}[/]  {Ui.E(key)} changed but could not re-pull: {Ui.E(ex.Message)}");
            }
        }

        await ingestor.RefreshAsync(cancellationToken).ConfigureAwait(false);
        monitor.LastPolledAt = now;
        JiraMonitorStore.Save(project, monitor);
        Ui.Err.MarkupLine($"[green]{stamp}[/]  {reindexed}/{changed.Count} changed ticket(s) re-indexed [dim]→ {totalChunks} chunks[/].");
    }

    private static async Task<int> PurgeAsync(string[] args)
    {
        string? key = null, project = null;
        bool all = false, yes = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--project" or "-p" when i + 1 < args.Length: project = args[++i]; break;
                case "--all": all = true; break;
                case "--yes" or "-y": yes = true; break;
                default:
                    if (key is null) { key = args[i]; }
                    else { return Usage(); }

                    break;
            }
        }

        project ??= "default";

        if (all == (key is not null))
        {
            Ui.Err.MarkupLine("[red]Pass either an issue key or --all, not both (or neither).[/]");
            return Usage();
        }

        var gateway = new OpenSearchGateway();
        var detector = new ContradictionDetector(gateway);

        if (all)
        {
            return await PurgeAllAsync(gateway, detector, project, yes).ConfigureAwait(false);
        }

        // Single ticket: scope by source_path + project so a ticket shared with
        // another project isn't collaterally removed.
        var canonical = key!.Trim().ToUpperInvariant();
        var sourcePath = JiraSource.Key(canonical);
        var deleted = await gateway.DeleteByQueryAsync(RtfmIndex.Name, DeleteQuery(project, sourcePath)).ConfigureAwait(false);
        await gateway.RefreshAsync(RtfmIndex.Name).ConfigureAwait(false);
        await detector.RemoveForPathAsync(sourcePath, onlyOpen: false).ConfigureAwait(false);

        var monitor = JiraMonitorStore.Load(project);
        var wasMonitored = monitor.Remove(canonical);
        if (wasMonitored)
        {
            JiraMonitorStore.Save(project, monitor);
        }

        if (deleted == 0 && !wasMonitored)
        {
            Ui.Err.MarkupLine($"[yellow]{Ui.E(canonical)}[/] was not indexed under project [{Ui.Accent}]{Ui.E(project)}[/] — nothing to purge.");
            return 0;
        }

        Ui.Err.MarkupLine($"[green]Purged[/] [bold]{Ui.E(canonical)}[/] from project [{Ui.Accent}]{Ui.E(project)}[/]: "
            + $"[bold]{deleted}[/] chunk{(deleted == 1 ? "" : "s")} deleted"
            + (wasMonitored ? ", removed from the monitored set." : "."));
        return 0;
    }

    private static async Task<int> PurgeAllAsync(OpenSearchGateway gateway, ContradictionDetector detector, string project, bool yes)
    {
        var monitor = JiraMonitorStore.Load(project);
        var count = await CountAsync(gateway, project, exactSourcePath: null).ConfigureAwait(false);

        if (count == 0 && monitor.Count == 0)
        {
            Ui.Err.MarkupLine($"No Jira tickets indexed under project [{Ui.Accent}]{Ui.E(project)}[/] — nothing to purge.");
            return 0;
        }

        Ui.Err.MarkupLine($"Project [{Ui.Accent}]{Ui.E(project)}[/]: [bold]{count}[/] Jira chunk(s), [bold]{monitor.Count}[/] monitored ticket(s).");

        if (!yes)
        {
            if (!Ui.Fancy)
            {
                Console.Error.WriteLine("rtfm jira purge --all: refusing to delete without --yes when not running interactively.");
                return 2;
            }

            if (!Ui.Err.Confirm("[red]Remove all Jira tickets from this project?[/]", defaultValue: false))
            {
                Console.Error.WriteLine("Aborted.");
                return 1;
            }
        }

        var deleted = await gateway.DeleteByQueryAsync(RtfmIndex.Name, DeleteQuery(project, exactSourcePath: null)).ConfigureAwait(false);
        await gateway.RefreshAsync(RtfmIndex.Name).ConfigureAwait(false);

        // Drop contradiction pairs for the monitored tickets (the set --all targets).
        foreach (var monitoredKey in monitor.Tickets.Keys)
        {
            await detector.RemoveForPathAsync(JiraSource.Key(monitoredKey), onlyOpen: false).ConfigureAwait(false);
        }

        JiraMonitorStore.Remove(project);

        Ui.Err.MarkupLine($"[green]Purged[/] all Jira tickets from project [{Ui.Accent}]{Ui.E(project)}[/]: "
            + $"[bold]{deleted}[/] chunk(s) deleted, monitored set cleared.");
        return 0;
    }

    /// <summary>The bool query matching a project's Jira chunks — one exact ticket, or every <c>jira://</c> doc.</summary>
    private static object JiraChunkQuery(string project, string? exactSourcePath)
    {
        object sourceClause = exactSourcePath is null
            ? new { prefix = new Dictionary<string, string> { ["source_path"] = JiraSource.Scheme } }
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
        => System.Text.Json.JsonSerializer.Serialize(new { query = JiraChunkQuery(project, exactSourcePath) });

    private static async Task<long> CountAsync(OpenSearchGateway gateway, string project, string? exactSourcePath)
    {
        if (!await gateway.IndexExistsAsync(RtfmIndex.Name).ConfigureAwait(false))
        {
            return 0;
        }

        var body = System.Text.Json.JsonSerializer.Serialize(new { size = 0, query = JiraChunkQuery(project, exactSourcePath) });
        var json = await gateway.SearchAsync(RtfmIndex.Name, body).ConfigureAwait(false);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("hits").GetProperty("total").GetProperty("value").GetInt64();
    }

    private static int List()
    {
        var configs = JiraConfigStore.List();
        if (configs.Count == 0)
        {
            Ui.Out.MarkupLine("[yellow]No Jira workspaces configured.[/] Add one with [italic]rtfm jira config --url <workspace> --email <you>[/].");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title("[bold]Jira workspaces[/]")
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
                config.MaxTickets.ToString());
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
