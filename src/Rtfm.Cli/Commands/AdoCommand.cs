using System.Globalization;
using Rtfm.Core.AzureDevOps;
using Rtfm.Core.Contradictions;
using Rtfm.Core.Indexing;
using Rtfm.Core.OpenSearch;
using Spectre.Console;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm ado</c> — Azure DevOps work items (§2.18), the Jira connector's model
/// applied to Boards. <c>config</c> stores a per-project organization descriptor
/// (the org URL plus <em>how</em> to authenticate — the Azure CLI login by
/// default, or a <c>${ENV}</c> PAT reference) and verifies it read-only;
/// <c>index &lt;ID|URL&gt;…</c> crawls one or more work items and their links
/// into thread-granular chunks under <c>ado://{org}/{id}</c>; <c>watch</c> polls
/// the monitored set on <c>System.Rev</c>; <c>purge</c> drops items. Reads only —
/// <see cref="AdoClient"/> issues nothing but GETs.
/// </summary>
internal static class AdoCommand
{
    /// <summary>Minimum poll interval — the "no more than once a minute" etiquette, with margin.</summary>
    private const int MinIntervalSeconds = 60;

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
            usage: rtfm ado config --url <organization URL> [--project <name>]
                                   [--auth az|pat] [--token-env AZURE_DEVOPS_EXT_PAT]
                                   [--resource <entra resource>] [--tenant <tenant id>]
                                   [--max-depth <n>] [--max-items <n>] [--follow-mentions] [--poll <seconds>]
                   rtfm ado index <ID|URL>... [--project <name>] [--depth <n>]
                                   [--max-items <n>] [--follow-mentions] [--dry-run]
                   rtfm ado watch [--project <name>] [--interval <seconds>] [--once]
                   rtfm ado purge <ID|URL> [--project <name>]
                   rtfm ado purge --all [--project <name>] [--yes]
                   rtfm ado list

            Auth defaults to your Azure CLI login ('az login') — nothing secret is stored.
            --token-env (or --auth pat) uses a Personal Access Token from that environment
            variable instead (Work Items: Read; add Code: Read for pull requests/commits).

            index takes several ids or work item URLs (space- or comma-separated), crawled
            as one run with one shared --max-items budget. Pass bare ids: an unquoted
            #123 is a comment in most shells.
            """);
        return 2;
    }

    private static async Task<int> ConfigAsync(string[] args)
    {
        string? url = null, project = null, auth = null, tokenVar = null, resource = null, tenant = null;
        int? maxDepth = null, maxItems = null, poll = null;
        var followMentions = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--url" or "--org" when i + 1 < args.Length: url = args[++i]; break;
                case "--project" or "-p" when i + 1 < args.Length: project = args[++i]; break;
                case "--auth" when i + 1 < args.Length: auth = args[++i].Trim().ToLowerInvariant(); break;
                case "--token-env" when i + 1 < args.Length: tokenVar = args[++i]; break;
                case "--resource" when i + 1 < args.Length: resource = args[++i]; break;
                case "--tenant" when i + 1 < args.Length: tenant = args[++i]; break;
                case "--max-depth" when i + 1 < args.Length && int.TryParse(args[i + 1], out var d): maxDepth = d; i++; break;
                case "--max-items" when i + 1 < args.Length && int.TryParse(args[i + 1], out var m): maxItems = m; i++; break;
                case "--poll" when i + 1 < args.Length && int.TryParse(args[i + 1], out var s): poll = s; i++; break;
                case "--follow-mentions": followMentions = true; break;
                default: return Usage();
            }
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            Ui.Err.MarkupLine("[red]--url is required[/] (e.g. https://dev.azure.com/<organization>).");
            return Usage();
        }

        // A named token variable implies PAT auth; otherwise the Azure CLI.
        auth ??= tokenVar is null ? AdoConfig.AuthAzCli : AdoConfig.AuthPat;
        if (auth is not (AdoConfig.AuthAzCli or AdoConfig.AuthPat))
        {
            Ui.Err.MarkupLine("[red]--auth must be 'az' or 'pat'.[/]");
            return 2;
        }

        project ??= "default";

        AdoOrganization organization;
        try
        {
            organization = AdoOrganization.Parse(url);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            Ui.Err.MarkupLine($"[red]Invalid organization URL:[/] {Ui.E(ex.Message)}");
            return 1;
        }

        var usesPat = auth == AdoConfig.AuthPat;
        tokenVar ??= AdoConfig.DefaultTokenVar;

        var config = new AdoConfig(
            OrganizationUrl: organization.Url,
            Auth: auth,
            Token: usesPat ? $"${{{tokenVar}}}" : null,
            Resource: usesPat ? null : resource,
            Tenant: usesPat ? null : tenant,
            MaxDepth: maxDepth ?? AdoConfig.DefaultMaxDepth,
            MaxItems: Math.Clamp(maxItems ?? AdoConfig.DefaultMaxItems, 1, AdoConfig.MaxItemsCeiling),
            FollowMentions: followMentions,
            PollSeconds: Math.Max(MinIntervalSeconds, poll ?? AdoConfig.DefaultPollSeconds));

        if (usesPat && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(tokenVar)))
        {
            AdoConfigStore.Save(project, config);
            Ui.Err.MarkupLine($"[green]Saved[/] Azure DevOps config for project [{Ui.Accent}]{Ui.E(project)}[/] [dim]({Ui.E(organization.Url)}, PAT)[/].");
            Ui.Err.MarkupLine($"[yellow]Note:[/] environment variable [teal]{Ui.E(tokenVar)}[/] is not set — set it to your PAT, then run [italic]rtfm ado index <ID>[/].");
            return 0;
        }

        try
        {
            using var client = new AdoClient(config);
            var (ok, displayName, error) = await Ui.Err.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync(usesPat ? "Verifying the PAT…" : "Getting a token from the Azure CLI and verifying it…", _ => client.VerifyAuthAsync());

            if (!ok)
            {
                Ui.Err.MarkupLine($"[red]Could not authenticate:[/] {Ui.E(error ?? "unknown error")}");
                Ui.Err.MarkupLine("[dim]Config not saved.[/]");
                return 1;
            }

            AdoConfigStore.Save(project, config);
            Ui.Err.MarkupLine($"[green]Saved[/] Azure DevOps config for project [{Ui.Accent}]{Ui.E(project)}[/] — authenticated as "
                + $"[bold]{Ui.E(displayName ?? "(unknown)")}[/] [dim]({Ui.E(organization.Url)}, {(usesPat ? "PAT" : "Azure CLI")})[/].");
            return 0;
        }
        catch (ArgumentException ex)
        {
            // An unsafe --resource / --tenant value (rejected before it can reach a shell).
            Ui.Err.MarkupLine($"[red]Invalid option:[/] {Ui.E(ex.Message)}");
            return 2;
        }
    }

    private static async Task<int> IndexAsync(string[] args)
    {
        string? project = null;
        var seeds = new List<int>();
        int? depth = null, maxItems = null;
        bool followMentions = false, dryRun = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--project" or "-p" when i + 1 < args.Length: project = args[++i]; break;
                case "--depth" when i + 1 < args.Length && int.TryParse(args[i + 1], out var d): depth = d; i++; break;
                case "--max-items" when i + 1 < args.Length && int.TryParse(args[i + 1], out var m): maxItems = m; i++; break;
                case "--follow-mentions": followMentions = true; break;
                case "--dry-run": dryRun = true; break;
                default:
                    if (args[i].StartsWith('-')) { return Usage(); }

                    foreach (var part in args[i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (AdoSource.ParseWorkItemId(part) is not { } id)
                        {
                            Ui.Err.MarkupLine($"[red]Not a work item id or URL:[/] {Ui.E(part)}");
                            return 2;
                        }

                        seeds.Add(id);
                    }

                    break;
            }
        }

        seeds = seeds.Distinct().ToList();
        if (seeds.Count == 0)
        {
            return Usage();
        }

        project ??= "default";
        var config = AdoConfigStore.Load(project);
        if (config is null)
        {
            Ui.Err.MarkupLine($"[red]No Azure DevOps config for project[/] [bold]{Ui.E(project)}[/]. Run [italic]rtfm ado config --project {Ui.E(project)} --url <organization URL>[/] first.");
            return 1;
        }

        var budget = Math.Clamp(maxItems ?? config.MaxItems, 1, AdoConfig.MaxItemsCeiling);
        if (seeds.Count > budget)
        {
            budget = Math.Min(seeds.Count, AdoConfig.MaxItemsCeiling);
            Ui.Err.MarkupLine($"[dim]{seeds.Count} ids given — budget raised to {budget} to cover them.[/]");
        }

        var options = new AdoCrawlOptions(
            MaxDepth: depth ?? config.MaxDepth,
            MaxItems: budget,
            FollowMentions: followMentions || config.FollowMentions);

        try
        {
            using var client = new AdoClient(config);
            var crawler = new AdoCrawler(client, new AdoDocumentRenderer());
            var indexedAt = DateTimeOffset.UtcNow;
            var label = seeds.Count == 1
                ? $"#{seeds[0]}"
                : $"{seeds.Count} work items ({string.Join(", ", seeds.Take(3).Select(s => $"#{s}"))}{(seeds.Count > 3 ? ", …" : "")})";

            using var title = new TerminalTitle();
            title.Reconciling($"crawling {label}");

            var result = await Ui.Err.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Crawling from {Ui.E(label)} (depth ≤ {options.MaxDepth})…", async ctx =>
                    await crawler.CrawlAsync(seeds, indexedAt, options,
                        log: msg =>
                        {
                            ctx.Status($"[dim]{Ui.E(msg)}[/]");
                            title.Reconciling(msg);
                        }).ConfigureAwait(false));

            if (result.Nodes.Count == 0)
            {
                Ui.Err.MarkupLine($"[red]Nothing pulled[/] for [bold]{Ui.E(label)}[/]"
                    + (result.Skipped.Count > 0 ? $" [dim]({result.Skipped.Count} skipped)[/]" : "") + ".");
                ReportLeash(result);
                return 1;
            }

            if (dryRun)
            {
                RenderCrawlPlan(result, label, options);
                Ui.Err.MarkupLine("[yellow]Dry run — nothing indexed.[/] Drop [italic]--dry-run[/] to index this set.");
                return 0;
            }

            using var embedder = await EmbedderProvider.TryCreateAsync().ConfigureAwait(false);
            var gateway = new OpenSearchGateway();
            var ingestor = new DocumentIngestor(new DocumentIndexer(gateway), embedder, new ContradictionDetector(gateway));
            await ingestor.EnsureIndexAsync().ConfigureAwait(false);

            var totalChunks = 0;
            var chunksById = new Dictionary<int, int>();
            await Ui.Err.Progress()
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn(Spinner.Known.Dots))
                .StartAsync(async pctx =>
                {
                    var task = pctx.AddTask("[bold]Indexing work items[/]", maxValue: result.Nodes.Count);
                    var done = 0;
                    foreach (var node in result.Nodes)
                    {
                        task.Description = $"[bold]Indexing[/] [dim]#{node.Id}[/]";
                        title.Progress(++done, result.Nodes.Count, $"indexing #{node.Id}");
                        var chunks = await ingestor.IngestDocumentAsync(
                            AdoSource.Key(client.Organization, node.Id), node.Rendered.Markdown, node.Rendered.Title, node.Rendered.ModifiedAt, project, indexedAt)
                            .ConfigureAwait(false);
                        chunksById[node.Id] = chunks;
                        totalChunks += chunks;
                        task.Increment(1);
                    }
                }).ConfigureAwait(false);

            await ingestor.RefreshAsync().ConfigureAwait(false);

            var monitor = AdoMonitorStore.Load(project);
            foreach (var node in result.Nodes)
            {
                // A later shallow crawl must not downgrade an item an earlier run
                // pulled as a seed.
                var full = node.Depth == 0 || (monitor.Items.TryGetValue(node.Id, out var existing) && existing.Full);
                monitor.Set(new MonitoredWorkItem(node.Id, node.Item.Rev, full));
            }

            monitor.LastPolledAt = indexedAt;
            AdoMonitorStore.Save(project, monitor);

            Ui.Err.MarkupLine($"[green]✓[/] Indexed [bold]{result.Nodes.Count}[/] work item(s) → [bold]{totalChunks}[/] chunks "
                + $"[dim](project {Ui.E(project)})[/]"
                + (embedder is null ? " · [yellow]lexical-only[/]" : " · [dim]embedded[/]"));

            RenderTree(result, chunksById, project, seeds);
            Ui.Err.MarkupLine($"[dim]Monitoring {monitor.Count} work item(s) in this project — run [italic]rtfm ado watch --project {Ui.E(project)}[/] to keep them fresh.[/]");
            ReportLeash(result);
            return 0;
        }
        catch (AdoException ex)
        {
            Ui.Err.MarkupLine($"[red]Azure DevOps error:[/] {Ui.E(ex.Message)}");
            return 1;
        }
        catch (InvalidDataException ex)
        {
            // PAT env var missing (lazy expansion, §2.18).
            Ui.Err.MarkupLine($"[red]Config error:[/] {Ui.E(ex.Message)}");
            return 1;
        }
        catch (HttpRequestException ex)
        {
            Ui.Err.MarkupLine($"[red]Could not reach OpenSearch:[/] {Ui.E(ex.Message)}. Is the stack up? Try [italic]rtfm ping[/].");
            return 1;
        }
    }

    private static void RenderCrawlPlan(AdoCrawlResult result, string seedLabel, AdoCrawlOptions options)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title($"[bold]Crawl plan[/] [dim]from {Ui.E(seedLabel)}, depth ≤ {options.MaxDepth}, budget {options.MaxItems}[/]")
            .AddColumn(new TableColumn("[bold]Depth[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Id[/]").RightAligned())
            .AddColumn("[bold]Type[/]")
            .AddColumn("[bold]Title[/]");

        foreach (var node in result.Nodes)
        {
            var itemTitle = node.Item.Title;
            table.AddRow(
                node.Depth.ToString(CultureInfo.InvariantCulture),
                $"[{Ui.Accent}]{node.Id}[/]",
                Ui.E(node.Item.WorkItemType ?? ""),
                Ui.E(itemTitle.Length > 70 ? itemTitle[..67] + "…" : itemTitle));
        }

        Ui.Err.Write(table);
        ReportLeash(result);
    }

    /// <summary>The crawled items as the epic → feature → story → task tree Boards defines (parents from <c>System.Parent</c>).</summary>
    private static void RenderTree(AdoCrawlResult result, IReadOnlyDictionary<int, int> chunksById, string project, IReadOnlyList<int> seeds)
    {
        static string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";

        var items = result.Nodes.Select(node =>
        {
            var detail = new List<string>();
            if (chunksById.TryGetValue(node.Id, out var chunks))
            {
                detail.Add(Plural(chunks, "chunk"));
            }

            if (node.Development.PullRequests.Count > 0)
            {
                detail.Add(Plural(node.Development.PullRequests.Count, "PR"));
            }

            if (node.Development.Commits.Count > 0)
            {
                detail.Add(Plural(node.Development.Commits.Count, "commit"));
            }

            return new IndexTreeItem(
                Id: node.Id.ToString(CultureInfo.InvariantCulture),
                ParentId: node.Item.ParentId?.ToString(CultureInfo.InvariantCulture),
                Depth: node.Depth,
                Label: $"#{node.Id}  {(node.Item.WorkItemType is { } type ? type + ": " : "")}{node.Item.Title}",
                Detail: detail.Count > 0 ? string.Join(" · ", detail) : null);
        }).ToList();

        IndexTree.Render(items, $"{result.Nodes.Count} work item(s) indexed into {project}", seeds.Select(s => s.ToString(CultureInfo.InvariantCulture)).ToList());
    }

    private static void ReportLeash(AdoCrawlResult result)
    {
        if (result.BudgetHit)
        {
            Ui.Err.MarkupLine($"[yellow]Budget reached:[/] pulled {result.Nodes.Count}, [bold]{result.Dropped}[/] more discovered but not followed. Raise [italic]--max-items[/] to go wider.");
        }

        if (result.Skipped.Count > 0)
        {
            Ui.Err.MarkupLine($"[dim]{result.Skipped.Count} work item(s) skipped (deleted or no permission): "
                + $"{Ui.E(string.Join(", ", result.Skipped.Take(8).Select(s => $"#{s}")))}{(result.Skipped.Count > 8 ? "…" : "")}[/]");
        }

        foreach (var warning in result.Warnings)
        {
            Ui.Err.MarkupLine($"[yellow]Warning:[/] {Ui.E(warning)}");
        }
    }

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
        var config = AdoConfigStore.Load(project);
        if (config is null)
        {
            Ui.Err.MarkupLine($"[red]No Azure DevOps config for project[/] [bold]{Ui.E(project)}[/]. Run [italic]rtfm ado config[/] then [italic]rtfm ado index[/] first.");
            return 1;
        }

        if (AdoMonitorStore.Load(project).Count == 0)
        {
            Ui.Err.MarkupLine($"[yellow]Nothing to watch[/] in project [bold]{Ui.E(project)}[/]. Run [italic]rtfm ado index <ID>[/] first to build a monitored set.");
            return 1;
        }

        var pollSeconds = Math.Max(MinIntervalSeconds, interval ?? config.PollSeconds);

        using var embedder = await EmbedderProvider.TryCreateAsync().ConfigureAwait(false);
        var gateway = new OpenSearchGateway();
        var ingestor = new DocumentIngestor(new DocumentIndexer(gateway), embedder, new ContradictionDetector(gateway));

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await ingestor.EnsureIndexAsync(cts.Token).ConfigureAwait(false);
            using var client = new AdoClient(config);
            var renderer = new AdoDocumentRenderer();

            Ui.Err.MarkupLine($"[green]● Watching[/] project [{Ui.Accent}]{Ui.E(project)}[/] — polling Azure DevOps every [bold]{pollSeconds}s[/]"
                + (once ? " [dim](once)[/]" : "") + ". [dim]Ctrl+C to stop.[/]");

            while (!cts.IsCancellationRequested)
            {
                await PollOnceAsync(client, renderer, ingestor, project, cts.Token).ConfigureAwait(false);
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

    private static async Task PollOnceAsync(AdoClient client, AdoDocumentRenderer renderer, DocumentIngestor ingestor, string project, CancellationToken cancellationToken)
    {
        // Reloaded each poll so a concurrent `rtfm ado index` is picked up.
        var monitor = AdoMonitorStore.Load(project);
        if (monitor.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var stamp = now.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        void Warn(string msg) => Ui.Err.MarkupLine($"[yellow]{stamp}[/]  {Ui.E(msg)}");

        IReadOnlyDictionary<int, int> revisions;
        try
        {
            revisions = await client.FetchRevisionsAsync(monitor.Items.Keys.ToList(), cancellationToken).ConfigureAwait(false);
        }
        catch (AdoException ex)
        {
            // The credential itself failed (e.g. the az session expired) — say so
            // and try again next cycle rather than killing the watcher.
            Warn($"could not poll: {ex.Message}");
            return;
        }

        var changed = monitor.SelectChanged(revisions);
        if (changed.Count == 0)
        {
            monitor.LastPolledAt = now;
            AdoMonitorStore.Save(project, monitor);
            Ui.Err.MarkupLine($"[dim]{stamp}  polled {monitor.Count} work item(s) — no changes.[/]");
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

            var entry = monitor.Items[id];
            try
            {
                var item = await client.FetchWorkItemAsync(id, includeComments: entry.Full, Warn, cancellationToken).ConfigureAwait(false);
                var development = await client.FetchDevelopmentAsync(item, Warn, cancellationToken).ConfigureAwait(false);
                var rendered = renderer.Render(item, client.OrganizationUrl, now, development);
                var n = await ingestor.IngestDocumentAsync(
                    AdoSource.Key(client.Organization, item.Id), rendered.Markdown, rendered.Title, rendered.ModifiedAt, project, now, cancellationToken).ConfigureAwait(false);
                monitor.Set(new MonitoredWorkItem(item.Id, item.Rev, entry.Full));
                reindexed++;
                totalChunks += n;
                Ui.Err.MarkupLine($"[green]{stamp}[/]  re-indexed [bold]#{id}[/] [dim](rev {entry.LastRev} → {item.Rev}) → {n} chunks[/]");
            }
            catch (AdoException ex)
            {
                Warn($"#{id} changed but could not re-pull: {ex.Message}");
            }
        }

        await ingestor.RefreshAsync(cancellationToken).ConfigureAwait(false);
        monitor.LastPolledAt = now;
        AdoMonitorStore.Save(project, monitor);
        Ui.Err.MarkupLine($"[green]{stamp}[/]  {reindexed}/{changed.Count} changed work item(s) re-indexed [dim]→ {totalChunks} chunks[/].");
    }

    private static async Task<int> PurgeAsync(string[] args)
    {
        string? target = null, project = null;
        bool all = false, yes = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--project" or "-p" when i + 1 < args.Length: project = args[++i]; break;
                case "--all": all = true; break;
                case "--yes" or "-y": yes = true; break;
                default:
                    if (target is null) { target = args[i]; }
                    else { return Usage(); }

                    break;
            }
        }

        project ??= "default";

        if (all == (target is not null))
        {
            Ui.Err.MarkupLine("[red]Pass either a work item id/URL or --all, not both (or neither).[/]");
            return Usage();
        }

        var gateway = new OpenSearchGateway();
        var detector = new ContradictionDetector(gateway);

        if (all)
        {
            return await PurgeAllAsync(gateway, detector, project, yes).ConfigureAwait(false);
        }

        if (AdoSource.ParseWorkItemId(target) is not { } id)
        {
            Ui.Err.MarkupLine($"[red]Not a work item id or URL:[/] {Ui.E(target!)}");
            return 2;
        }

        if (AdoConfigStore.Load(project) is not { } config)
        {
            Ui.Err.MarkupLine($"[red]No Azure DevOps config for project[/] [bold]{Ui.E(project)}[/] — the source key needs its organization.");
            return 1;
        }

        // Scoped by source_path + project, so the same item indexed into another
        // project isn't collaterally removed.
        var sourcePath = AdoSource.Key(config.Organization, id);
        var deleted = await gateway.DeleteByQueryAsync(RtfmIndex.Name, DeleteQuery(project, sourcePath)).ConfigureAwait(false);
        await gateway.RefreshAsync(RtfmIndex.Name).ConfigureAwait(false);
        await detector.RemoveForPathAsync(sourcePath, onlyOpen: false).ConfigureAwait(false);

        var monitor = AdoMonitorStore.Load(project);
        var wasMonitored = monitor.Remove(id);
        if (wasMonitored)
        {
            AdoMonitorStore.Save(project, monitor);
        }

        if (deleted == 0 && !wasMonitored)
        {
            Ui.Err.MarkupLine($"[yellow]#{id}[/] was not indexed under project [{Ui.Accent}]{Ui.E(project)}[/] — nothing to purge.");
            return 0;
        }

        Ui.Err.MarkupLine($"[green]Purged[/] [bold]#{id}[/] from project [{Ui.Accent}]{Ui.E(project)}[/]: "
            + $"[bold]{deleted}[/] chunk{(deleted == 1 ? "" : "s")} deleted"
            + (wasMonitored ? ", removed from the monitored set." : "."));
        return 0;
    }

    private static async Task<int> PurgeAllAsync(OpenSearchGateway gateway, ContradictionDetector detector, string project, bool yes)
    {
        var monitor = AdoMonitorStore.Load(project);
        var count = await CountAsync(gateway, project).ConfigureAwait(false);

        if (count == 0 && monitor.Count == 0)
        {
            Ui.Err.MarkupLine($"No Azure DevOps work items indexed under project [{Ui.Accent}]{Ui.E(project)}[/] — nothing to purge.");
            return 0;
        }

        Ui.Err.MarkupLine($"Project [{Ui.Accent}]{Ui.E(project)}[/]: [bold]{count}[/] Azure DevOps chunk(s), [bold]{monitor.Count}[/] monitored work item(s).");

        if (!yes)
        {
            if (!Ui.Fancy)
            {
                Console.Error.WriteLine("rtfm ado purge --all: refusing to delete without --yes when not running interactively.");
                return 2;
            }

            if (!Ui.Err.Confirm("[red]Remove all Azure DevOps work items from this project?[/]", defaultValue: false))
            {
                Console.Error.WriteLine("Aborted.");
                return 1;
            }
        }

        var deleted = await gateway.DeleteByQueryAsync(RtfmIndex.Name, DeleteQuery(project, exactSourcePath: null)).ConfigureAwait(false);
        await gateway.RefreshAsync(RtfmIndex.Name).ConfigureAwait(false);

        if (AdoConfigStore.Load(project) is { } config)
        {
            foreach (var id in monitor.Items.Keys)
            {
                await detector.RemoveForPathAsync(AdoSource.Key(config.Organization, id), onlyOpen: false).ConfigureAwait(false);
            }
        }

        AdoMonitorStore.Remove(project);

        Ui.Err.MarkupLine($"[green]Purged[/] all Azure DevOps work items from project [{Ui.Accent}]{Ui.E(project)}[/]: "
            + $"[bold]{deleted}[/] chunk(s) deleted, monitored set cleared.");
        return 0;
    }

    /// <summary>The bool query matching a project's work item chunks — one exact item, or every <c>ado://</c> doc.</summary>
    private static object ChunkQuery(string project, string? exactSourcePath)
    {
        object sourceClause = exactSourcePath is null
            ? new { prefix = new Dictionary<string, string> { ["source_path"] = AdoSource.Scheme } }
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
        => System.Text.Json.JsonSerializer.Serialize(new { query = ChunkQuery(project, exactSourcePath) });

    private static async Task<long> CountAsync(OpenSearchGateway gateway, string project)
    {
        if (!await gateway.IndexExistsAsync(RtfmIndex.Name).ConfigureAwait(false))
        {
            return 0;
        }

        var body = System.Text.Json.JsonSerializer.Serialize(new { size = 0, query = ChunkQuery(project, exactSourcePath: null) });
        var json = await gateway.SearchAsync(RtfmIndex.Name, body).ConfigureAwait(false);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("hits").GetProperty("total").GetProperty("value").GetInt64();
    }

    private static int List()
    {
        var configs = AdoConfigStore.List();
        if (configs.Count == 0)
        {
            Ui.Out.MarkupLine("[yellow]No Azure DevOps organizations configured.[/] Add one with [italic]rtfm ado config --url https://dev.azure.com/<organization>[/].");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title("[bold]Azure DevOps organizations[/]")
            .AddColumn("[bold]Project[/]")
            .AddColumn("[bold]Organization[/]")
            .AddColumn("[bold]Auth[/]")
            .AddColumn(new TableColumn("[bold]Depth[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Max[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Monitored[/]").RightAligned());

        foreach (var (project, config) in configs)
        {
            table.AddRow(
                $"[{Ui.Accent}]{Ui.E(project)}[/]",
                Ui.E(config.OrganizationUrl),
                DescribeAuth(config),
                config.MaxDepth.ToString(CultureInfo.InvariantCulture),
                config.MaxItems.ToString(CultureInfo.InvariantCulture),
                AdoMonitorStore.Load(project).Count.ToString(CultureInfo.InvariantCulture));
        }

        Ui.Out.Write(table);
        return 0;
    }

    private static string DescribeAuth(AdoConfig config)
    {
        if (!config.UsesPat)
        {
            return config.Tenant is null ? "Azure CLI" : $"Azure CLI [dim](tenant {Ui.E(config.Tenant)})[/]";
        }

        var token = (config.Token ?? string.Empty).Trim();
        var tokenVar = token.StartsWith("${", StringComparison.Ordinal) && token.EndsWith('}') ? token[2..^1] : null;
        if (tokenVar is null)
        {
            return "PAT";
        }

        return string.IsNullOrEmpty(Environment.GetEnvironmentVariable(tokenVar))
            ? $"PAT [red]${{{Ui.E(tokenVar)}}} (unset)[/]"
            : $"PAT [green]${{{Ui.E(tokenVar)}}}[/]";
    }
}
