using System.ComponentModel;
using ModelContextProtocol.Server;
using Rtfm.Core.OpenSearch;

namespace Rtfm.Mcp;

/// <summary>
/// Liveness probe (Phase 21). The observed failure mode: an agent commits to
/// an expensive search call against a dead stack and burns a multi-minute
/// client timeout. This tool answers "is RTFM up?" in seconds — the cap here
/// is deliberately much shorter than the gateway's 30s request timeout.
/// </summary>
[McpServerToolType]
public static class PingTool
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [McpServerTool(Name = "ping")]
    [Description("""
        Check whether the RTFM search backend (OpenSearch) is reachable. Answers within ~5 seconds
        either way. Call this before expensive queries when the stack may be down (e.g. after an
        earlier tool call timed out or errored), or to verify a restart worked — not before every call.
        """)]
    public static async Task<PingResult> Ping(
        OpenSearchGateway gateway,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);

        try
        {
            var health = await gateway.PingAsync(cts.Token).ConfigureAwait(false);
            return new PingResult(
                Reachable: health.Reachable,
                Endpoint: gateway.Endpoint.ToString(),
                ClusterStatus: health.Status,
                Error: health.Reachable
                    ? null
                    : $"OpenSearch is not reachable: {health.Error}. Start it with 'docker compose up -d' in the rtfm repo (or 'rtfm init').");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PingResult(
                Reachable: false,
                Endpoint: gateway.Endpoint.ToString(),
                ClusterStatus: null,
                Error: $"No response within {Timeout.TotalSeconds:0}s — OpenSearch is down or unreachable. "
                    + "Start it with 'docker compose up -d' in the rtfm repo (or 'rtfm init').");
        }
    }
}

/// <summary>Structured tool output — serialized to JSON for the LLM by the SDK.</summary>
public sealed record PingResult(bool Reachable, string Endpoint, string? ClusterStatus, string? Error);
