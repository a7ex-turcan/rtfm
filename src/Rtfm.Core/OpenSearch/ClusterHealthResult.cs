namespace Rtfm.Core.OpenSearch;

/// <summary>
/// Outcome of a cluster health probe. <see cref="Reachable"/> distinguishes
/// "couldn't talk to OpenSearch at all" from "talked to it, here's its status".
/// </summary>
public sealed record ClusterHealthResult(
    bool Reachable,
    string? Status,
    string? ClusterName,
    int? NumberOfNodes,
    string? Error)
{
    /// <summary>A reachable cluster reporting green or yellow status is healthy enough to use.</summary>
    public bool IsHealthy => Reachable && Status is "green" or "yellow";
}
