using System.Text.Json;
using Rtfm.Core.Embeddings;
using Rtfm.Core.Indexing;
using Rtfm.Core.Notes;
using Rtfm.Core.OpenSearch;

namespace Rtfm.Core.Search;

/// <summary>
/// Retrieval (§2.10). With an <see cref="ITextEmbedder"/> this runs Tier 2
/// hybrid search — a <c>hybrid</c> query pairing BM25 (<c>multi_match</c> over
/// the analyzed fields) with kNN over <c>content_vector</c>, fused by the
/// <c>rtfm-hybrid</c> normalization pipeline — so technical lookups keep their
/// exact-token wins while conceptual questions match on meaning. With an
/// <see cref="IReranker"/> (Tier 3, Phase 11) it retrieves a generous
/// candidate set and reorders it by cross-encoder relevance, returning
/// sigmoid-squashed scores. Every tier degrades loudly rather than failing the
/// search: no embedder → BM25; no reranker → the fused order stands.
/// </summary>
public sealed class DocumentSearch(
    OpenSearchGateway gateway,
    ITextEmbedder? embedder = null,
    Action<string>? log = null,
    IReranker? reranker = null,
    NotesStore? notes = null)
{
    private readonly Action<string> _log = log ?? (_ => { });
    private bool _pipelineEnsured;
    private bool _embedderBroken;
    private bool _rerankerBroken;

    /// <summary>
    /// Runs a search. When <paramref name="project"/> is null/empty the search
    /// spans all projects (§2.14); otherwise it is filtered to that one.
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int topK = 5, string? project = null, CancellationToken cancellationToken = default)
    {
        var vector = TryEmbedQuery(query);

        // With a reranker, over-fetch so it has real candidates to reorder;
        // without one, fetch exactly what the caller asked for. (Kept a bit
        // tighter than the hybrid k because reranking cost is per *window*.)
        var wantRerank = reranker is not null && !_rerankerBroken;
        var fetch = wantRerank ? Math.Clamp(topK * 3, 12, 20) : topK;

        string json;
        if (vector is not null)
        {
            await EnsurePipelineAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var body = BuildHybridQuery(query, vector, fetch, project);
                json = await gateway.SearchAsync(RtfmIndex.Name, body, RtfmIndex.HybridPipelineName, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Server-side hybrid failures happen (observed: an OpenSearch
                // 2.17 hybrid-scoring bug 500ing on specific queries). Degrade
                // to lexical for THIS query — no sticky flag; the next query
                // tries hybrid again.
                _log($"rtfm: hybrid search failed, retrying lexical-only: {ex.Message}");
                json = await gateway.SearchAsync(RtfmIndex.Name, BuildQuery(query, fetch, project), searchPipeline: null, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            json = await gateway.SearchAsync(RtfmIndex.Name, BuildQuery(query, fetch, project), searchPipeline: null, cancellationToken).ConfigureAwait(false);
        }

        var hits = ParseHits(json);

        // Override notes (§2.13 C / Phase 13): matching notes join the pool as
        // first-class, attributed candidates.
        var noteHits = await TryGetNoteHitsAsync(query, vector, project, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<SearchHit> results;
        if (wantRerank && hits.Count + noteHits.Count > 1)
        {
            try
            {
                // MS MARCO cross-encoders are trained on short passages; a full
                // 1600-char chunk dilutes the signal until everything scores
                // uniformly low (observed on the real corpus). Max-window
                // scoring: split each candidate into overlapping windows (each
                // re-carrying its breadcrumb), score all windows, and let a
                // hit's best window speak for it.
                var pool = hits.Concat(noteHits).ToList();
                var (windows, ownerHit) = BuildRerankWindows(pool);
                var windowScores = reranker!.Score(query, windows);
                var best = MaxPerHit(windowScores, ownerHit, pool.Count);
                results = Rerank(pool, best, topK);
            }
            catch (Exception ex)
            {
                _rerankerBroken = true; // don't retry per query — a broken model stays broken
                _log($"rtfm: reranking unavailable, keeping fused order: {ex.Message}");
                results = MergeWithoutReranker(hits, noteHits, topK);
            }
        }
        else
        {
            results = MergeWithoutReranker(hits, noteHits, topK);
        }

        return await TryAnnotateAsync(results, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Without a reranker there is no shared score scale across the two
    /// indexes — matching notes are few, human-confirmed, and query-relevant,
    /// so they lead. Internal for tests.
    /// </summary>
    internal static IReadOnlyList<SearchHit> MergeWithoutReranker(
        IReadOnlyList<SearchHit> docHits, IReadOnlyList<SearchHit> noteHits, int topK)
        => noteHits.OrderByDescending(n => n.Score).Concat(docHits).Take(topK).ToList();

    /// <summary>Matching override notes as searchable hits. Never fails the search.</summary>
    private async Task<IReadOnlyList<SearchHit>> TryGetNoteHitsAsync(
        string query, float[]? queryVector, string? project, CancellationToken cancellationToken)
    {
        if (notes is null)
        {
            return [];
        }

        try
        {
            var matches = await notes.SearchAsync(query, queryVector, project, cancellationToken).ConfigureAwait(false);
            return matches.Select(m => NoteToHit(m.Note, m.Score)).ToList();
        }
        catch (Exception ex)
        {
            _log($"rtfm: note lookup failed, continuing without overrides: {ex.Message}");
            return [];
        }
    }

    /// <summary>An override note dressed as a hit — attributed, never masquerading as source text. Internal for tests.</summary>
    internal static SearchHit NoteToHit(Note note, double score) => new(
        Score: Math.Round(score, 4),
        Project: note.Project,
        SourcePath: $"note://{note.Id}",
        HeadingPath: "Override note",
        Title: null,
        Content: note.Text,
        SourceModifiedAt: note.CreatedAt,
        Origin: "note",
        Author: note.Author);

    /// <summary>Attaches anchored notes to the doc hits whose source they correct (the "annotates" half).</summary>
    private async Task<IReadOnlyList<SearchHit>> TryAnnotateAsync(IReadOnlyList<SearchHit> results, CancellationToken cancellationToken)
    {
        if (notes is null)
        {
            return results;
        }

        try
        {
            var paths = results.Where(h => h.Origin == "doc").Select(h => h.SourcePath).Distinct(StringComparer.Ordinal).ToList();
            var anchored = await notes.FindAnchoredAsync(paths, cancellationToken).ConfigureAwait(false);
            return anchored.Count == 0 ? results : Annotate(results, anchored);
        }
        catch (Exception ex)
        {
            _log($"rtfm: note annotation failed, returning hits unannotated: {ex.Message}");
            return results;
        }
    }

    /// <summary>Pure attach: a note annotates hits from the same project whose source path it targets. Internal for tests.</summary>
    internal static IReadOnlyList<SearchHit> Annotate(IReadOnlyList<SearchHit> results, IReadOnlyList<Note> anchored)
        => results.Select(hit =>
        {
            if (hit.Origin != "doc")
            {
                return hit;
            }

            var mine = anchored
                .Where(n => string.Equals(n.TargetPath, hit.SourcePath, StringComparison.Ordinal)
                    && string.Equals(n.Project, hit.Project, StringComparison.Ordinal))
                .Select(n => new NoteAnnotation(n.Id, n.Text, n.Author, n.CreatedAt))
                .ToList();

            return mine.Count == 0 ? hit : hit with { Annotations = mine };
        }).ToList();

    /// <summary>Window size in chars (≈250 wordpieces — near the passage length the reranker was trained on).</summary>
    private const int RerankWindowChars = 1000;

    private const int RerankWindowOverlap = 200;

    /// <summary>
    /// Flattens hits into scoring windows. Short chunks pass through whole;
    /// long ones split with overlap, every window prefixed by the heading
    /// breadcrumb so it stays self-describing. Internal for tests.
    /// </summary>
    internal static (IReadOnlyList<string> Windows, IReadOnlyList<int> OwnerHit) BuildRerankWindows(IReadOnlyList<SearchHit> hits)
    {
        var windows = new List<string>();
        var owner = new List<int>();

        for (var i = 0; i < hits.Count; i++)
        {
            var content = hits[i].Content;
            if (content.Length <= RerankWindowChars)
            {
                windows.Add(content);
                owner.Add(i);
                continue;
            }

            var heading = hits[i].HeadingPath;
            var body = content.StartsWith(heading + "\n\n", StringComparison.Ordinal)
                ? content[(heading.Length + 2)..]
                : content;

            var stride = RerankWindowChars - RerankWindowOverlap;
            for (var pos = 0; pos < body.Length; pos += stride)
            {
                var slice = body.Substring(pos, Math.Min(RerankWindowChars, body.Length - pos));
                windows.Add($"{heading}\n\n{slice}");
                owner.Add(i);

                if (pos + RerankWindowChars >= body.Length)
                {
                    break;
                }
            }
        }

        return (windows, owner);
    }

    /// <summary>Best window score per hit. Internal for tests.</summary>
    internal static float[] MaxPerHit(IReadOnlyList<float> windowScores, IReadOnlyList<int> ownerHit, int hitCount)
    {
        var best = Enumerable.Repeat(float.MinValue, hitCount).ToArray();
        for (var w = 0; w < windowScores.Count; w++)
        {
            best[ownerHit[w]] = Math.Max(best[ownerHit[w]], windowScores[w]);
        }

        return best;
    }

    /// <summary>Reorders hits by cross-encoder score (sigmoid-squashed into [0,1] for display). Internal for tests.</summary>
    internal static IReadOnlyList<SearchHit> Rerank(IReadOnlyList<SearchHit> hits, IReadOnlyList<float> scores, int topK)
        => hits.Zip(scores, (hit, score) => hit with { Score = Math.Round(Sigmoid(score), 4) })
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();

    internal static double Sigmoid(double logit) => 1.0 / (1.0 + Math.Exp(-logit));

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
        ["project", "source_path", "heading_path", "title", "content", "source_modified_at", "ordinal"];

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
                SourceModifiedAt: TryGetDate(source, "source_modified_at"),
                Ordinal: source.TryGetProperty("ordinal", out var ordinal) && ordinal.ValueKind == JsonValueKind.Number
                    ? ordinal.GetInt32()
                    : null));
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
