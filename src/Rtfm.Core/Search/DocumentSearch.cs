using System.Text.Json;
using Rtfm.Core.Embeddings;
using Rtfm.Core.Indexing;
using Rtfm.Core.OpenSearch;

namespace Rtfm.Core.Search;

/// <summary>
/// Retrieval (§2.10). With an <see cref="ITextEmbedder"/> this runs Tier 2
/// hybrid search — a <c>hybrid</c> query pairing BM25 (<c>multi_match</c> over
/// the analyzed fields) with kNN over <c>content_vector</c>, fused by the
/// <c>rtfm-hybrid</c> normalization pipeline — so technical lookups keep their
/// exact-token wins while conceptual questions match on meaning. Without an
/// embedder (or if embedding fails, e.g. model not downloadable) it degrades to
/// the Tier 1 BM25 query, loudly, rather than failing the search.
/// </summary>
public sealed class DocumentSearch(OpenSearchGateway gateway, ITextEmbedder? embedder = null, Action<string>? log = null)
{
    private readonly Action<string> _log = log ?? (_ => { });
    private bool _pipelineEnsured;
    private bool _embedderBroken;

    /// <summary>
    /// Runs a search. When <paramref name="project"/> is null/empty the search
    /// spans all projects (§2.14); otherwise it is filtered to that one.
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int topK = 5, string? project = null, CancellationToken cancellationToken = default)
    {
        var vector = TryEmbedQuery(query);

        string body, json;
        if (vector is not null)
        {
            await EnsurePipelineAsync(cancellationToken).ConfigureAwait(false);
            body = BuildHybridQuery(query, vector, topK, project);
            json = await gateway.SearchAsync(RtfmIndex.Name, body, RtfmIndex.HybridPipelineName, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            body = BuildQuery(query, topK, project);
            json = await gateway.SearchAsync(RtfmIndex.Name, body, searchPipeline: null, cancellationToken).ConfigureAwait(false);
        }

        return ParseHits(json);
    }

    /// <summary>
    /// Embeds the query, or returns null when running lexical-only (no embedder,
    /// or it failed once — don't retry per keystroke, a broken model stays broken).
    /// </summary>
    private float[]? TryEmbedQuery(string query)
    {
        if (embedder is null || _embedderBroken)
        {
            return null;
        }

        try
        {
            return embedder.Embed(query);
        }
        catch (Exception ex)
        {
            _embedderBroken = true;
            _log($"rtfm: semantic search unavailable, falling back to lexical-only: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The MCP server never runs the indexing path that puts the pipeline, so
    /// ensure it exists once per process before the first hybrid query.
    /// </summary>
    private async Task EnsurePipelineAsync(CancellationToken cancellationToken)
    {
        if (_pipelineEnsured)
        {
            return;
        }

        await gateway.PutSearchPipelineAsync(RtfmIndex.HybridPipelineName, RtfmIndex.HybridPipelineJson, cancellationToken).ConfigureAwait(false);
        _pipelineEnsured = true;
    }

    /// <summary>Tier 1: BM25 only. Kept as the no-embedder / degraded path.</summary>
    internal static string BuildQuery(string query, int topK, string? project = null)
    {
        var request = new
        {
            size = topK,
            query = WithProjectFilter(LexicalClause(query), project),
            _source = SourceFields,
        };

        return JsonSerializer.Serialize(request);
    }

    /// <summary>
    /// Tier 2: hybrid BM25 + kNN. Each sub-query carries the project filter
    /// itself (the <c>hybrid</c> query has no shared top-level filter in this
    /// OpenSearch version): the lexical clause via a <c>bool</c> filter, the knn
    /// clause via its own <c>filter</c> (efficient on the lucene engine).
    /// </summary>
    internal static string BuildHybridQuery(string query, float[] vector, int topK, string? project = null)
    {
        // Give the fusion more than topK candidates per side so a hit that is
        // mediocre lexically but strong semantically (or vice versa) survives.
        var k = Math.Clamp(topK * 5, 25, 100);

        object knnInner = string.IsNullOrWhiteSpace(project)
            ? new { vector, k }
            : new { vector, k, filter = ProjectTerm(project) };

        var request = new
        {
            size = topK,
            query = new
            {
                hybrid = new
                {
                    // Order matters: the pipeline's weights are [lexical, semantic].
                    queries = new object[]
                    {
                        WithProjectFilter(LexicalClause(query), project),
                        new { knn = new Dictionary<string, object> { ["content_vector"] = knnInner } },
                    },
                },
            },
            _source = SourceFields,
        };

        return JsonSerializer.Serialize(request);
    }

    private static readonly string[] SourceFields =
        ["project", "source_path", "heading_path", "title", "content", "source_modified_at"];

    private static object LexicalClause(string query) => new
    {
        multi_match = new
        {
            query,
            fields = new[] { "content", "heading_path^2", "title^2" },
            type = "best_fields",
        },
    };

    private static object ProjectTerm(string project)
        => new { term = new Dictionary<string, string> { ["project"] = project } };

    /// <summary>No project → the clause as-is; otherwise wrap it with a project filter.</summary>
    private static object WithProjectFilter(object clause, string? project)
        => string.IsNullOrWhiteSpace(project)
            ? clause
            : new { @bool = new { must = new[] { clause }, filter = new[] { ProjectTerm(project) } } };

    private static List<SearchHit> ParseHits(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var hits = new List<SearchHit>();

        foreach (var hit in doc.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray())
        {
            var source = hit.GetProperty("_source");
            hits.Add(new SearchHit(
                Score: hit.TryGetProperty("_score", out var score) && score.ValueKind == JsonValueKind.Number ? score.GetDouble() : 0,
                Project: GetString(source, "project") ?? string.Empty,
                SourcePath: GetString(source, "source_path") ?? string.Empty,
                HeadingPath: GetString(source, "heading_path") ?? string.Empty,
                Title: GetString(source, "title"),
                Content: GetString(source, "content") ?? string.Empty,
                SourceModifiedAt: TryGetDate(source, "source_modified_at")));
        }

        return hits;
    }

    private static string? GetString(JsonElement source, string name)
        => source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? TryGetDate(JsonElement source, string name)
        => source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
}
