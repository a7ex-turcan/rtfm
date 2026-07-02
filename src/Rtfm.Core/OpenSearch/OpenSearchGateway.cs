using System.Text.Json;
using OpenSearch.Net;
using Rtfm.Core.Configuration;
using OsHttpMethod = global::OpenSearch.Net.HttpMethod;

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
            .RequestTimeout(TimeSpan.FromSeconds(30));
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

    /// <summary>True if the index exists.</summary>
    public async Task<bool> IndexExistsAsync(string index, CancellationToken cancellationToken = default)
    {
        var response = await _client.Indices
            .ExistsAsync<StringResponse>(index, ctx: cancellationToken)
            .ConfigureAwait(false);

        return response.HttpStatusCode == 200;
    }

    /// <summary>Creates the index with the given settings + mapping JSON if it does not exist. Returns true if created.</summary>
    public async Task<bool> EnsureIndexAsync(string index, string definitionJson, CancellationToken cancellationToken = default)
    {
        if (await IndexExistsAsync(index, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var response = await _client.Indices
            .CreateAsync<StringResponse>(index, PostData.String(definitionJson), ctx: cancellationToken)
            .ConfigureAwait(false);

        EnsureSuccess(response, $"create index '{index}'");
        return true;
    }

    /// <summary>
    /// Deletes all documents whose exact <paramref name="field"/> equals
    /// <paramref name="value"/> (§2.9). Returns the number of documents deleted.
    /// </summary>
    public Task<long> DeleteByTermAsync(string index, string field, string value, CancellationToken cancellationToken = default)
    {
        var query = new { query = new { term = new Dictionary<string, string> { [field] = value } } };
        return DeleteByQueryAsync(index, JsonSerializer.Serialize(query), cancellationToken);
    }

    /// <summary>Runs an arbitrary delete-by-query body. Returns the number of documents deleted.</summary>
    public async Task<long> DeleteByQueryAsync(string index, string queryJson, CancellationToken cancellationToken = default)
    {
        var response = await _client
            .DeleteByQueryAsync<StringResponse>(index, PostData.String(queryJson), ctx: cancellationToken)
            .ConfigureAwait(false);

        EnsureSuccess(response, $"delete_by_query on {index}");

        using var doc = JsonDocument.Parse(response.Body);
        return doc.RootElement.TryGetProperty("deleted", out var deleted) && deleted.ValueKind == JsonValueKind.Number
            ? deleted.GetInt64()
            : 0;
    }

    /// <summary>Sends a pre-built NDJSON <c>_bulk</c> payload and throws if any item failed.</summary>
    public async Task BulkAsync(string ndjson, CancellationToken cancellationToken = default)
    {
        var response = await _client
            .BulkAsync<StringResponse>(PostData.String(ndjson), ctx: cancellationToken)
            .ConfigureAwait(false);

        EnsureSuccess(response, "bulk index");

        using var doc = JsonDocument.Parse(response.Body);
        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.GetBoolean())
        {
            var firstError = doc.RootElement.GetProperty("items").EnumerateArray()
                .SelectMany(item => item.EnumerateObject())
                .Where(op => op.Value.TryGetProperty("error", out _))
                .Select(op => op.Value.GetProperty("error").ToString())
                .FirstOrDefault();

            throw new InvalidOperationException($"Bulk index reported errors: {firstError}");
        }
    }

    /// <summary>Makes recent writes visible to search (call after index/delete).</summary>
    public async Task RefreshAsync(string index, CancellationToken cancellationToken = default)
    {
        var response = await _client.Indices
            .RefreshAsync<StringResponse>(index, ctx: cancellationToken)
            .ConfigureAwait(false);

        EnsureSuccess(response, $"refresh index '{index}'");
    }

    /// <summary>
    /// Runs a raw query body and returns the raw JSON response. When
    /// <paramref name="searchPipeline"/> is set, the query runs through that
    /// search pipeline (hybrid score fusion, §2.10 Tier 2).
    /// </summary>
    public async Task<string> SearchAsync(string index, string queryJson, string? searchPipeline = null, CancellationToken cancellationToken = default)
    {
        // The client has no first-class search_pipeline param in this version;
        // passing it through the request's query string works on any endpoint.
        SearchRequestParameters? parameters = null;
        if (searchPipeline is not null)
        {
            parameters = new SearchRequestParameters();
            parameters.QueryString["search_pipeline"] = searchPipeline;
        }

        var response = await _client
            .SearchAsync<StringResponse>(index, PostData.String(queryJson), parameters, ctx: cancellationToken)
            .ConfigureAwait(false);

        EnsureSuccess(response, $"search '{index}'");
        return response.Body;
    }

    /// <summary>Creates or replaces a search pipeline (idempotent PUT).</summary>
    public async Task PutSearchPipelineAsync(string name, string definitionJson, CancellationToken cancellationToken = default)
    {
        var response = await _client
            .DoRequestAsync<StringResponse>(
                OsHttpMethod.PUT,
                $"_search/pipeline/{Uri.EscapeDataString(name)}",
                cancellationToken,
                PostData.String(definitionJson))
            .ConfigureAwait(false);

        EnsureSuccess(response, $"put search pipeline '{name}'");
    }

    private static void EnsureSuccess(StringResponse response, string operation)
    {
        if (!response.Success)
        {
            var detail = response.OriginalException?.Message ?? response.Body ?? response.DebugInformation;
            throw new InvalidOperationException($"OpenSearch {operation} failed: {detail}");
        }
    }
}
