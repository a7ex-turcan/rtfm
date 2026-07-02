using Rtfm.Core.Chunking;
using Rtfm.Core.Conversion;

namespace Rtfm.Core.Indexing;

/// <summary>
/// The single "ingest one file" path shared by <c>rtfm index</c> (batch) and
/// <c>rtfm watch</c> (incremental): convert → chunk → bulk-upsert, plus
/// remove-by-path. Keeping this in Core means both entry points agree on the
/// supported formats, the normalized source-path key (§2.12), and the
/// modified-date fallback (§2.13 A).
/// </summary>
public sealed class DocumentIngestor
{
    /// <summary>Extensions RTFM will index (§2.5). Content is sniffed on convert; the extension only gates discovery.</summary>
    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".doc", ".docx", ".md", ".markdown" };

    private readonly DocumentConverter _converter;
    private readonly MarkdownChunker _chunker;
    private readonly DocumentIndexer _indexer;

    public DocumentIngestor(DocumentIndexer indexer, DocumentConverter? converter = null, MarkdownChunker? chunker = null)
    {
        _indexer = indexer;
        _converter = converter ?? new DocumentConverter();
        _chunker = chunker ?? new MarkdownChunker();
    }

    public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path));

    /// <summary>All supported documents under <paramref name="folder"/>, in a stable order.</summary>
    public static IEnumerable<string> EnumerateSupportedFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(IsSupported)
            .OrderBy(f => f, StringComparer.Ordinal);

    public Task<bool> EnsureIndexAsync(CancellationToken cancellationToken = default)
        => _indexer.EnsureIndexAsync(cancellationToken);

    public Task RefreshAsync(CancellationToken cancellationToken = default)
        => _indexer.RefreshAsync(cancellationToken);

    /// <summary>
    /// Converts, chunks, and upserts one file into the index, returning the chunk
    /// count. If the file yields no chunks (e.g. it became empty), any prior
    /// chunks are removed so nothing stale is left behind.
    /// </summary>
    public async Task<int> IngestFileAsync(string path, string project, DateTimeOffset indexedAt, CancellationToken cancellationToken = default)
    {
        var conversion = _converter.Convert(path);
        var sourcePath = PathNormalizer.Normalize(path);
        var modifiedAt = conversion.SourceModifiedAt
            ?? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);

        var metadata = new ChunkMetadata(sourcePath, conversion.Title, modifiedAt, project);
        var chunks = _chunker.Chunk(conversion.Markdown, metadata);

        if (chunks.Count == 0)
        {
            await _indexer.RemoveDocumentAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        return await _indexer.IndexDocumentAsync(chunks, indexedAt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes every chunk for the document at <paramref name="path"/> (delete/rename handling).</summary>
    public Task RemoveFileAsync(string path, CancellationToken cancellationToken = default)
        => _indexer.RemoveDocumentAsync(PathNormalizer.Normalize(path), cancellationToken);
}
