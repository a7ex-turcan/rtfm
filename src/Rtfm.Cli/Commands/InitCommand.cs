using System.Diagnostics;
using Rtfm.Core.Configuration;
using Rtfm.Core.Indexing;
using Rtfm.Core.OpenSearch;
using Spectre.Console;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm init [--with-model]</c> — one-shot machine bootstrap: start the
/// OpenSearch container (<c>docker compose up -d --wait</c>; the compose file
/// has a healthcheck, so --wait blocks until the cluster answers), verify
/// connectivity, and create the index + hybrid search pipeline. With
/// <c>--with-model</c> it also pre-downloads the embedding model so the first
/// index/search doesn't pay the ~90 MB wait.
///
/// The compose file resolves in this order: current directory (repo dev) →
/// <c>RTFM_HOME</c> → a copy of the *embedded* compose file materialized under
/// LocalApplicationData. The embedded copy is what makes init work from any
/// directory today and without a repo at all after Phase 14 packaging.
/// </summary>
internal static class InitCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var withModel = false;
        foreach (var arg in args)
        {
            if (arg is "--with-model")
            {
                withModel = true;
            }
            else
            {
                Console.Error.WriteLine("usage: rtfm init [--with-model]");
                return 2;
            }
        }

        // 1. Docker there at all?
        if (await RunDockerAsync("compose version", echo: false).ConfigureAwait(false) != 0)
        {
            Console.Error.WriteLine("rtfm init: `docker compose` is not available. Install/start Docker Desktop (or docker + compose v2) and retry.");
            return 1;
        }

        // 2. Which compose file?
        var (composePath, origin) = ResolveComposeFile();
        Ui.Err.MarkupLine($"Using compose file: [dim]{Ui.E(composePath)}[/] [dim]({origin})[/]");

        // 3. Start (idempotent) and wait for the healthcheck.
        Ui.Err.MarkupLine("Starting OpenSearch ([dim]docker compose up -d --wait[/]) …");
        if (await RunDockerAsync($"compose -f \"{composePath}\" up -d --wait", echo: true).ConfigureAwait(false) != 0)
        {
            Console.Error.WriteLine("rtfm init: docker compose failed — see output above."
                + (OperatingSystem.IsLinux() ? " On native Linux, check vm.max_map_count=262144." : string.Empty));
            return 1;
        }

        // 4. Confirm from our side of the fence.
        var gateway = new OpenSearchGateway();
        var health = await gateway.PingAsync().ConfigureAwait(false);
        if (!health.Reachable)
        {
            Console.Error.WriteLine($"rtfm init: container is up but {gateway.Endpoint} is not answering: {health.Error}");
            return 1;
        }

        // 5. Index + hybrid pipeline (idempotent).
        var created = await new DocumentIndexer(gateway).EnsureIndexAsync().ConfigureAwait(false);
        Ui.Err.MarkupLine(created
            ? $"Created index [bold]{RtfmIndex.Name}[/] and search pipeline."
            : $"Index [bold]{RtfmIndex.Name}[/] and search pipeline already in place.");

        // 6. Optional model warm-up (both tiers: embedder + reranker).
        var modelReady = false;
        if (withModel)
        {
            modelReady = await EmbedderProvider.TryCreateAsync().ConfigureAwait(false) is not null
                && await EmbedderProvider.TryCreateRerankerAsync().ConfigureAwait(false) is not null;
            if (modelReady)
            {
                Ui.Err.MarkupLine("Embedding + reranking models ready.");
            }
        }

        var summary = new Grid().AddColumn().AddColumn();
        summary.AddRow(new Markup("[dim]OpenSearch[/]"), new Markup($"[green]● {Ui.E(health.Status ?? "up")}[/]  [dim]{Ui.E(gateway.Endpoint.ToString())}[/]"));
        summary.AddRow(new Markup("[dim]index[/]"), new Markup($"[green]ready[/]  [dim]{RtfmIndex.Name} + {RtfmIndex.HybridPipelineName}[/]"));
        summary.AddRow(new Markup("[dim]model[/]"), new Markup(withModel
            ? modelReady ? "[green]cached[/]" : "[yellow]unavailable — lexical-only until it downloads[/]"
            : "[dim]deferred to first index/search[/]"));
        Ui.Out.Write(new Panel(summary).Header("[bold] RTFM ready [/]").BorderColor(Color.Green));
        Ui.Out.MarkupLine($"Next: [bold]rtfm index <folder> --project <name>[/]");

        return 0;
    }

    /// <summary>cwd → RTFM_HOME → embedded copy under LocalApplicationData.</summary>
    private static (string Path, string Origin) ResolveComposeFile()
    {
        var cwd = Path.Combine(Directory.GetCurrentDirectory(), "docker-compose.yml");
        if (File.Exists(cwd))
        {
            return (cwd, "current directory");
        }

        if (RtfmEnvironment.ResolveRtfmHome() is { } home)
        {
            var fromHome = Path.Combine(home, "docker-compose.yml");
            if (File.Exists(fromHome))
            {
                return (fromHome, "RTFM_HOME");
            }
        }

        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Path.GetTempPath();
        }

        var dir = Path.Combine(baseDir, "rtfm");
        Directory.CreateDirectory(dir);
        var materialized = Path.Combine(dir, "docker-compose.yml");

        if (!File.Exists(materialized))
        {
            using var resource = typeof(InitCommand).Assembly.GetManifestResourceStream("rtfm.docker-compose.yml")
                ?? throw new InvalidOperationException("embedded compose file missing from build");
            using var target = File.Create(materialized);
            resource.CopyTo(target);
        }

        return (materialized, "embedded copy");
    }

    /// <summary>Runs <c>docker &lt;args&gt;</c>; docker's own output streams to stderr when <paramref name="echo"/>.</summary>
    private static async Task<int> RunDockerAsync(string arguments, bool echo)
    {
        try
        {
            var psi = new ProcessStartInfo("docker", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(psi)!;
            var stdout = PumpAsync(process.StandardOutput, echo);
            var stderr = PumpAsync(process.StandardError, echo);
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return -1; // docker binary not found
        }
    }

    private static async Task PumpAsync(StreamReader reader, bool echo)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (echo)
            {
                Console.Error.WriteLine($"  | {line}");
            }
        }
    }
}
