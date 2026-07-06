using System.Text;
using System.Text.Json;
using Rtfm.Core.Embeddings;
using Rtfm.Core.Indexing;
using Rtfm.Core.OpenSearch;

namespace Rtfm.Core.Search;

/// <summary>
/// The Phase 8 catalog operations — everything here is a read over fields the
/// index already carries (no ingest/mapping changes):
/// <list type="bullet">
///   <item><c>ListSources</c> — one row per indexed document via a
///     <c>terms</c> aggregation on <c>source_path</c>.</item>
///   <item><c>GetDocument</c> — a document's chunks in ordinal order,
///     reassembled into readable markdown (the converted form; the source file
///     itself may be MHTML/docx, and the stored path key is lower-cased so
///     re-reading it from disk is not portable — §2.12).</item>
///   <item><c>FindSimilar</c> — mean of the document's chunk vectors against
///     <c>content_vector</c>, best chunk per candidate document decides.</item>
/// </list>
/// Paths are matched exactly first, then by <c>*/filename</c> suffix — so the
/// short <c>source</c> filenames that <c>search_docs</c> returns also resolve.
/// </summary>
public sealed class DocumentCatalog(OpenSearchGateway gateway)
{
    private const int MaxChunksPerDoc = 1000;

    /// <summary>Enumerates indexed documents, optionally scoped to one project (§2.14).</summary>
    public async Task<IReadOnlyList<SourceInfo>> ListSourcesAsync(string? project = null, CancellationToken cancellationToken = default)
    {
        var body = BuildListSourcesQuery(project);
        var json = await gateway.SearchAsync(RtfmIndex.Name, body, cancellationToken: cancellationToken).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var sources = new List<SourceInfo>();

        if (!doc.RootElement.TryGetProperty("aggregations", out var aggs))
        {
            return sources;
        }

        foreach (var bucket in aggs.GetProperty("docs").GetProperty("buckets").EnumerateArray())
        {
            var first = bucket.GetProperty("meta").GetProperty("hits").GetProperty("hits")[0].GetProperty("_source");
            sources.Add(new SourceInfo(
                Path: bucket.GetProperty("key").GetString() ?? string.Empty,
                Title: GetString(first, "title"),
                Project: GetString(first, "project") ?? string.Empty,
                SourceModifiedAt: TryGetDate(first, "source_modified_at"),
                ChunkCount: bucket.GetProperty("doc_count").GetInt64()));
        }

        return sources;
    }

    /// <summary>
    /// The reassembled document, or null when no chunks match the path. With
    /// <paramref name="aroundOrdinal"/> set, only the chunks within
    /// <paramref name="radius"/> ordinals of it are fetched (Phase 21 — "the
    /// section, not the doc"); the result is marked partial.
    /// </summary>
    public async Task<DocumentContent?> GetDocumentAsync(
        string path, string? project = null, int? aroundOrdinal = null, int radius = 2, CancellationToken cancellationToken = default)
    {
        (int From, int To)? window = aroundOrdinal is { } center
            ? (Math.Max(0, center - radius), center + radius)
            : null;

        var chunks = await FetchChunksAsync(path, project, withVectors: false, window, cancellationToken).ConfigureAwait(false);
        if (chunks.Count == 0)
        {
            return null;
        }

        var first = chunks[0];
        var markdown = ReassembleMarkdown(first.Title, chunks.Select(c => (c.HeadingPath, c.Content)).ToList());

        return new DocumentContent(first.SourcePath, first.Title, first.Project, first.SourceModifiedAt, chunks.Count, markdown, Partial: window is not null);
    }

    /// <summary>
    /// Documents semantically nearest to the one at <paramref name="path"/>
    /// (itself excluded). Null when the path matches nothing.
    /// </summary>
    public async Task<SimilarDocsResult?> FindSimilarAsync(string path, int topK = 5, string? project = null, CancellationToken cancellationToken = default)
    {
        var chunks = await FetchChunksAsync(path, project, withVectors: true, ordinalRange: null, cancellationToken).ConfigureAwait(false);
        if (chunks.Count == 0)
        {
            return null;
        }

        var vectors = chunks.Where(c => c.Vector is not null).Select(c => c.Vector!).ToList();
        if (vectors.Count == 0)
        {
            return new SimilarDocsResult(VectorsAvailable: false, []);
        }

        var centroid = MeanVector(vectors);
        var body = BuildSimilarQuery(centroid, chunks[0].SourcePath, project);
        var json = await gateway.SearchAsync(RtfmIndex.Name, body, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Best chunk per candidate document wins; hits arrive score-descending.
        var byDoc = new Dictionary<string, SimilarDoc>(StringComparer.Ordinal);
        using (var doc = JsonDocument.Parse(json))
        {
            foreach (var hit in doc.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray())
            {
                var source = hit.GetProperty("_source");
                var docPath = GetString(source, "source_path") ?? string.Empty;
                if (byDoc.ContainsKey(docPath))
                {
                    continue;
                }

                byDoc[docPath] = new SimilarDoc(
                    Path: docPath,
                    Title: GetString(source, "title"),
                    Project: GetString(source, "project") ?? string.Empty,
                    Score: hit.TryGetProperty("_score", out var s) && s.ValueKind == JsonValueKind.Number ? Math.Round(s.GetDouble(), 4) : 0,
                    BestMatchingHeading: GetString(source, "heading_path") ?? string.Empty,
                    SourceModifiedAt: TryGetDate(source, "source_modified_at"));
            }
        }

        return new SimilarDocsResult(VectorsAvailable: true, byDoc.Values.Take(topK).ToList());
    }

    private sealed record ChunkRow(
        string SourcePath, string HeadingPath, string Content, string? Title,
        string Project, DateTimeOffset? SourceModifiedAt, float[]? Vector);

    /// <summary>Fetches a document's chunks in ordinal order — exact path first, then <c>*/filename</c> fallback.</summary>
    private async Task<IReadOnlyList<ChunkRow>> FetchChunksAsync(
        string path, string? project, bool withVectors, (int From, int To)? ordinalRange, CancellationToken cancellationToken)
    {
        var normalized = NormalizeLookupPath(path);

        var rows = await RunChunkQueryAsync(BuildChunksQuery(normalized, exact: true, project, withVectors, ordinalRange), withVectors, cancellationToken).ConfigureAwait(false);
        if (rows.Count > 0)
        {
            return rows;
        }

        return await RunChunkQueryAsync(BuildChunksQuery(LastSegment(normalized), exact: false, project, withVectors, ordinalRange), withVectors, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ChunkRow>> RunChunkQueryAsync(string body, bool withVectors, CancellationToken cancellationToken)
    {
        var json = await gateway.SearchAsync(RtfmIndex.Name, body, cancellationToken: cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var rows = new List<ChunkRow>();
        foreach (var hit in doc.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray())
        {
            var source = hit.GetProperty("_source");

            float[]? vector = null;
            if (withVectors && source.TryGetProperty("content_vector", out var v) && v.ValueKind == JsonValueKind.Array)
            {
                vector = v.EnumerateArray().Select(e => e.GetSingle()).ToArray();
            }

            rows.Add(new ChunkRow(
                SourcePath: GetString(source, "source_path") ?? string.Empty,
                HeadingPath: GetString(source, "heading_path") ?? string.Empty,
                Content: GetString(source, "content") ?? string.Empty,
                Title: GetString(source, "title"),
                Project: GetString(source, "project") ?? string.Empty,
                SourceModifiedAt: TryGetDate(source, "source_modified_at"),
                Vector: vector));
        }

        // A filename fallback could theoretically match several docs; keep the first path only.
        if (rows.Count > 0)
        {
            var firstPath = rows[0].SourcePath;
            rows.RemoveAll(r => !string.Equals(r.SourcePath, firstPath, StringComparison.Ordinal));
        }

        return rows;
    }

    // ---- pure helpers (internal for tests) ----

    /// <summary>Lookup normalization: separators + casing only — no filesystem resolution (§2.12 keys are stored lower-cased).</summary>
    internal static string NormalizeLookupPath(string path)
        => path.Trim().Replace('\\', '/').ToLowerInvariant();

    internal static string LastSegment(string normalizedPath)
        => normalizedPath[(normalizedPath.LastIndexOf('/') + 1)..];

    /// <summary>
    /// Chunks → readable markdown: document title once, each heading breadcrumb
    /// once (skipped when it just repeats the title), breadcrumb prefixes
    /// stripped from chunk bodies.
    /// </summary>
    internal static string ReassembleMarkdown(string? title, IReadOnlyList<(string HeadingPath, string Content)> chunks)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(title))
        {
            sb.Append("# ").Append(title).Append("\n\n");
        }

        string? lastHeading = null;
        foreach (var (heading, content) in chunks)
        {
            var text = StripBreadcrumb(content, heading);

            if (heading.Length > 0 && heading != lastHeading)
            {
                if (heading != title)
                {
                    sb.Append("## ").Append(heading).Append("\n\n");
                }

                lastHeading = heading;
            }

            if (text.Length > 0)
            {
                sb.Append(text.TrimEnd()).Append("\n\n");
            }
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>Undoes <c>Chunk.ContentWithBreadcrumb</c>: content is either the breadcrumb alone or breadcrumb + blank line + body.</summary>
    internal static string StripBreadcrumb(string content, string headingPath)
        => content == headingPath
            ? string.Empty
            : content.StartsWith(headingPath + "\n\n", StringComparison.Ordinal)
                ? content[(headingPath.Length + 2)..]
                : content;

    /// <summary>Element-wise mean, L2-normalized — the document centroid used as the kNN query vector.</summary>
    internal static float[] MeanVector(IReadOnlyList<float[]> vectors)
    {
        var mean = new float[vectors[0].Length];
        foreach (var vector in vectors)
        {
            for (var i = 0; i < mean.Length; i++)
            {
                mean[i] += vector[i];
            }
        }

        for (var i = 0; i < mean.Length; i++)
        {
            mean[i] /= vectors.Count;
        }

        return LocalEmbedder.NormalizeL2(mean);
    }

    internal static string BuildListSourcesQuery(string? project)
    {
        object query = string.IsNullOrWhiteSpace(project)
            ? new { match_all = new { } }
            : new { term = new Dictionary<string, string> { ["project"] = project } };

        var request = new
        {
            size = 0,
            query,
            aggs = new
            {
                docs = new
                {
                    terms = new { field = "source_path", size = 1000, order = new { _key = "asc" } },
                    aggs = new
                    {
                        meta = new
                        {
                            top_hits = new
                            {
                                size = 1,
                                sort = new object[] { new { ordinal = "asc" } },
                                _source = new[] { "title", "project", "source_modified_at" },
                            },
                        },
                    },
                },
            },
        };

        return JsonSerializer.Serialize(request);
    }

    internal static string BuildChunksQuery(string pathOrName, bool exact, string? project, bool withVectors, (int From, int To)? ordinalRange = null)
    {
        object pathClause = exact
            ? new { term = new Dictionary<string, string> { ["source_path"] = pathOrName } }
            : new { wildcard = new Dictionary<string, object> { ["source_path"] = new { value = $"*{pathOrName}" } } };

        var filters = new List<object> { pathClause };
        if (!string.IsNullOrWhiteSpace(project))
        {
            filters.Add(new { term = new Dictionary<string, string> { ["project"] = project } });
        }

        if (ordinalRange is { } range)
        {
            filters.Add(new { range = new Dictionary<string, object> { ["ordinal"] = new { gte = range.From, lte = range.To } } });
        }

        var sourceFields = new List<string> { "source_path", "heading_path", "content", "title", "project", "source_modified_at" };
        if (withVectors)
        {
            sourceFields.Add("content_vector");
        }

        var request = new
        {
            size = MaxChunksPerDoc,
            query = new { @bool = new { filter = filters } },
            sort = new object[] { new { ordinal = "asc" } },
            _source = sourceFields,
        };

        return JsonSerializer.Serialize(request);
    }

    internal static string BuildSimilarQuery(float[] vector, string selfPath, string? project, int k = 50)
    {
        var filter = new List<object>();
        if (!string.IsNullOrWhiteSpace(project))
        {
            filter.Add(new { term = new Dictionary<string, string> { ["project"] = project } });
        }

        var request = new
        {
            size = k,
            query = new
            {
                knn = new Dictionary<string, object>
                {
                    ["content_vector"] = new
                    {
                        vector,
                        k,
                        filter = new
                        {
                            @bool = new
                            {
                                filter,
                                must_not = new object[] { new { term = new Dictionary<string, string> { ["source_path"] = selfPath } } },
                            },
                        },
                    },
                },
            },
            _source = new[] { "source_path", "title", "project", "heading_path", "source_modified_at" },
        };

        return JsonSerializer.Serialize(request);
    }

    private static string? GetString(JsonElement source, string name)
        => source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? TryGetDate(JsonElement source, string name)
        => source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
}
