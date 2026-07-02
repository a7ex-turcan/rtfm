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

    /// <summary>Creates the index with the RTFM mapping if it is missing. Returns true if created.</summary>
    public Task<bool> EnsureIndexAsync(CancellationToken cancellationToken = default)
        => gateway.EnsureIndexAsync(RtfmIndex.Name, RtfmIndex.DefinitionJson, cancellationToken);

    /// <summary>Replaces all chunks for one document (delete-by-path, then bulk index). Returns chunk count.</summary>
    public async Task<int> IndexDocumentAsync(
        IReadOnlyList<Chunk> chunks,
        DateTimeOffset indexedAt,
        CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0)
        {
            return 0;
        }

        var sourcePath = chunks[0].SourcePath;
        await gateway.DeleteByTermAsync(RtfmIndex.Name, "source_path", sourcePath, cancellationToken).ConfigureAwait(false);
        await gateway.BulkAsync(BuildBulkPayload(chunks, indexedAt), cancellationToken).ConfigureAwait(false);
        return chunks.Count;
    }

    /// <summary>Makes writes visible to search — call once after a batch.</summary>
    public Task RefreshAsync(CancellationToken cancellationToken = default)
        => gateway.RefreshAsync(RtfmIndex.Name, cancellationToken);

    internal static string BuildBulkPayload(IReadOnlyList<Chunk> chunks, DateTimeOffset indexedAt)
    {
        var builder = new StringBuilder();

        foreach (var chunk in chunks)
        {
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
            };
            builder.Append(JsonSerializer.Serialize(document, Json)).Append('\n');
        }

        return builder.ToString();
    }
}
