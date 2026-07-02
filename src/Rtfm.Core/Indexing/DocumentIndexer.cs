using System.Text;
using System.Text.Json;
using Rtfm.Core.Chunking;
using Rtfm.Core.OpenSearch;

namespace Rtfm.Core.Indexing;

/// <summary>
/// Writes a document's chunks into OpenSearch. Per §2.9 an update is
/// delete-by-query on the normalized <c>source_path</c> followed by a bulk
/// re-index, which also makes re-runs idempotent. The caller is responsible for
/// passing chunks whose <see cref="Chunk.SourcePath"/> is already normalized
/// (§2.12) — same key on index and delete.
/// </summary>
public sealed class DocumentIndexer(OpenSearchGateway gateway)
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// Creates the index with the RTFM mapping if it is missing, and (re)puts the
    /// hybrid search pipeline (idempotent). Returns true if the index was created.
    /// </summary>
    public async Task<bool> EnsureIndexAsync(CancellationToken cancellationToken = default)
    {
        var created = await gateway.EnsureIndexAsync(RtfmIndex.Name, RtfmIndex.DefinitionJson, cancellationToken).ConfigureAwait(false);
        await gateway.PutSearchPipelineAsync(RtfmIndex.HybridPipelineName, RtfmIndex.HybridPipelineJson, cancellationToken).ConfigureAwait(false);
        return created;
    }

    /// <summary>
    /// Replaces all chunks for one document (delete-by-path, then bulk index).
    /// <paramref name="vectors"/>, when given, must align with
    /// <paramref name="chunks"/> by index and populates <c>content_vector</c>
    /// (§2.10 Tier 2). Returns chunk count.
    /// </summary>
    public async Task<int> IndexDocumentAsync(
        IReadOnlyList<Chunk> chunks,
        DateTimeOffset indexedAt,
        IReadOnlyList<float[]>? vectors = null,
        CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0)
        {
            return 0;
        }

        var sourcePath = chunks[0].SourcePath;
        await gateway.DeleteByTermAsync(RtfmIndex.Name, "source_path", sourcePath, cancellationToken).ConfigureAwait(false);
        await gateway.BulkAsync(BuildBulkPayload(chunks, indexedAt, vectors), cancellationToken).ConfigureAwait(false);
        return chunks.Count;
    }

    /// <summary>
    /// Removes every chunk for one document (§2.9). Used for deletes/renames in
    /// watch mode. <paramref name="normalizedSourcePath"/> must already be run
    /// through <see cref="PathNormalizer"/> — the same key used at index time.
    /// </summary>
    public Task RemoveDocumentAsync(string normalizedSourcePath, CancellationToken cancellationToken = default)
        => gateway.DeleteByTermAsync(RtfmIndex.Name, "source_path", normalizedSourcePath, cancellationToken);

    /// <summary>Makes writes visible to search — call once after a batch.</summary>
    public Task RefreshAsync(CancellationToken cancellationToken = default)
        => gateway.RefreshAsync(RtfmIndex.Name, cancellationToken);

    internal static string BuildBulkPayload(IReadOnlyList<Chunk> chunks, DateTimeOffset indexedAt, IReadOnlyList<float[]>? vectors = null)
    {
        if (vectors is not null && vectors.Count != chunks.Count)
        {
            throw new ArgumentException($"Got {vectors.Count} vectors for {chunks.Count} chunks — they must align by index.", nameof(vectors));
        }

        var builder = new StringBuilder();

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var action = new { index = new { _index = RtfmIndex.Name, _id = $"{chunk.SourcePath}#{chunk.Ordinal}" } };
            builder.Append(JsonSerializer.Serialize(action)).Append('\n');

            var document = new
            {
                project = chunk.Project,
                source_path = chunk.SourcePath,
                ordinal = chunk.Ordinal,
                title = chunk.DocumentTitle,
                heading_path = chunk.HeadingPath,
                content = chunk.ContentWithBreadcrumb,
                source_modified_at = chunk.SourceModifiedAt?.ToUniversalTime(),
                indexed_at = indexedAt.ToUniversalTime(),
                content_vector = vectors?[i],
            };
            builder.Append(JsonSerializer.Serialize(document, Json)).Append('\n');
        }

        return builder.ToString();
    }
}
