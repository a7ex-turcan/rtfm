using Rtfm.Core.Configuration;
using Rtfm.Core.OpenSearch;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm ping</c> — Phase 0 health check. Confirms the CLI can reach the
/// local OpenSearch cluster and reports its status.
/// </summary>
internal static class PingCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var endpoint = RtfmEnvironment.ResolveOpenSearchUrl();
        Console.WriteLine($"Pinging OpenSearch at {endpoint} ...");

        var gateway = new OpenSearchGateway(endpoint);
        var health = await gateway.PingAsync();

        if (!health.Reachable)
        {
            Console.Error.WriteLine($"  UNREACHABLE: {health.Error}");
            Console.Error.WriteLine("Is OpenSearch running? Try: docker compose up -d");
            return 1;
        }

        Console.WriteLine($"  cluster:  {health.ClusterName}");
        Console.WriteLine($"  status:   {health.Status}");
        Console.WriteLine($"  nodes:    {health.NumberOfNodes}");

        if (health.IsHealthy)
        {
            Console.WriteLine("OK — cluster is healthy.");
            return 0;
        }

        Console.Error.WriteLine($"Cluster reachable but status is '{health.Status}'.");
        return 1;
    }
}
