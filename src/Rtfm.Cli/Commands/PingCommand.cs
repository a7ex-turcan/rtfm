using Rtfm.Core.Configuration;
using Rtfm.Core.OpenSearch;
using Spectre.Console;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm ping</c> — Phase 0 health check. Confirms the CLI can reach the
/// local OpenSearch cluster and reports its status (Phase 7: as a color-coded
/// panel with a spinner while probing).
/// </summary>
internal static class PingCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var endpoint = RtfmEnvironment.ResolveOpenSearchUrl();
        var gateway = new OpenSearchGateway(endpoint);

        var health = await Ui.Err.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Pinging OpenSearch at {Ui.E(endpoint.ToString())} …", _ => gateway.PingAsync());

        if (!health.Reachable)
        {
            Ui.Err.Write(new Panel(
                    new Markup($"[red]UNREACHABLE[/]  {Ui.E(health.Error)}\n[dim]Is OpenSearch running? Try:[/] docker compose up -d"))
                .Header($"[bold] {Ui.E(endpoint.ToString())} [/]")
                .BorderColor(Color.Red));
            return 1;
        }

        var statusColor = health.Status switch
        {
            "green" => Color.Green,
            "yellow" => Color.Yellow,
            _ => Color.Red,
        };

        var report = new Grid().AddColumn().AddColumn();
        report.AddRow(new Markup("[dim]cluster[/]"), new Text(health.ClusterName ?? "?"));
        report.AddRow(new Markup("[dim]status[/]"), new Markup($"[bold {statusColor}]{Ui.E(health.Status ?? "?")}[/]"));
        report.AddRow(new Markup("[dim]nodes[/]"), new Text(health.NumberOfNodes?.ToString() ?? "?"));
        report.AddRow(new Markup("[dim]endpoint[/]"), new Text(endpoint.ToString()));

        Ui.Out.Write(new Panel(report)
            .Header(health.IsHealthy ? "[bold green] OpenSearch: healthy [/]" : $"[bold {statusColor}] OpenSearch: {Ui.E(health.Status ?? "?")} [/]")
            .BorderColor(statusColor));

        return health.IsHealthy ? 0 : 1;
    }
}
