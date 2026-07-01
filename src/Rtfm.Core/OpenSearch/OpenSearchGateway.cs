using System.Text.Json;
using OpenSearch.Net;
using Rtfm.Core.Configuration;

namespace Rtfm.Core.OpenSearch;

/// <summary>
/// Thin wrapper over the low-level OpenSearch client. Per CLAUDE.md §2.10 we
/// deliberately favour the low-level client + raw JSON over the strongly-typed
/// client, which is awkward for the analyzer / hybrid config we need later.
/// For now it just exposes a connectivity probe used by <c>rtfm ping</c>.
/// </summary>
public sealed class OpenSearchGateway
{
    private readonly OpenSearchLowLevelClient _client;

    public Uri Endpoint { get; }

    public OpenSearchGateway(Uri endpoint)
    {
        Endpoint = endpoint;
        var pool = new SingleNodeConnectionPool(endpoint);
        var config = new ConnectionConfiguration(pool)
            .RequestTimeout(TimeSpan.FromSeconds(10));
        _client = new OpenSearchLowLevelClient(config);
    }

    /// <summary>Uses the endpoint resolved from the environment (see <see cref="RtfmEnvironment"/>).</summary>
    public OpenSearchGateway()
        : this(RtfmEnvironment.ResolveOpenSearchUrl())
    {
    }

    /// <summary>
    /// Probes <c>GET /_cluster/health</c>. Never throws for an unreachable
    /// cluster — the failure is reported on the returned result instead.
    /// </summary>
    public async Task<ClusterHealthResult> PingAsync(CancellationToken cancellationToken = default)
    {
        var response = await _client.Cluster
            .HealthAsync<StringResponse>(ctx: cancellationToken)
            .ConfigureAwait(false);

        if (!response.Success || string.IsNullOrEmpty(response.Body))
        {
            var error = response.OriginalException?.Message ?? response.DebugInformation;
            return new ClusterHealthResult(false, null, null, null, error);
        }

        using var doc = JsonDocument.Parse(response.Body);
        var root = doc.RootElement;

        return new ClusterHealthResult(
            Reachable: true,
            Status: root.TryGetProperty("status", out var status) ? status.GetString() : null,
            ClusterName: root.TryGetProperty("cluster_name", out var name) ? name.GetString() : null,
            NumberOfNodes: root.TryGetProperty("number_of_nodes", out var nodes) ? nodes.GetInt32() : null,
            Error: null);
    }
}
