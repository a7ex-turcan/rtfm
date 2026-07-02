using Rtfm.Core.Chunking;
using Rtfm.Core.Contradictions;
using Rtfm.Core.Conversion;
using Rtfm.Core.Embeddings;

namespace Rtfm.Core.Indexing;

/// <summary>
/// The single "ingest one file" path shared by <c>rtfm index</c> (batch) and
/// <c>rtfm watch</c> (incremental): convert → chunk → embed → bulk-upsert, plus
/// remove-by-path. Keeping this in Core means both entry points agree on the
/// supported formats, the normalized source-path key (§2.12), and the
/// modified-date fallback (§2.13 A). With an <see cref="ITextEmbedder"/> each
/// chunk's indexed text is embedded into <c>content_vector</c> (§2.10 Tier 2);
/// without one, chunks index lexical-only exactly as before.
/// </summary>
public sealed class DocumentIngestor
{
    /// <summary>Extensions RTFM will index (§2.5). Content is sniffed on convert; the extension only gates discovery.</summary>
    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".doc", ".docx", ".md", ".markdown", ".pdf", ".xlsx", ".csv" };

    private readonly DocumentConverter _converter;
    private readonly MarkdownChunker _chunker;
    private readonly DocumentIndexer _indexer;
    private readonly ITextEmbedder? _embedder;
    private readonly ContradictionDetector? _detector;

    public DocumentIngestor(
        DocumentIndexer indexer,
        ITextEmbedder? embedder = null,
        ContradictionDetector? detector = null,
        DocumentConverter? converter = null,
        MarkdownChunker? chunker = null)
    {
        _indexer = indexer;
        _embedder = embedder;
        _detector = detector;
        _converter = converter ?? new DocumentConverter();
        _chunker = chunker ?? new MarkdownChunker();
    }

    public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path));

    /// <summary>All supported documents under <paramref name="folder"/>, in a stable order.</summary>
    public static IEnumerable<string> EnumerateSupportedFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(IsSupported)
            .OrderBy(f => f, StringComparer.Ordinal);

    public async Task<bool> EnsureIndexAsync(CancellationToken cancellationToken = default)
    {
        var created = await _indexer.EnsureIndexAsync(cancellationToken).ConfigureAwait(false);
        if (_detector is not null)
        {
            await _detector.EnsureIndexAsync(cancellationToken).ConfigureAwait(false);
        }

        return created;
    }

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
            await RemovePairsAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        // Embed the same text that gets indexed (breadcrumb + body, §2.7) so
        // lexical and semantic retrieval see an identical document.
        var vectors = _embedder is null
            ? null
            : chunks.Select(c => _embedder.Embed(c.ContentWithBreadcrumb)).ToList();

        var count = await _indexer.IndexDocumentAsync(chunks, indexedAt, vectors, cancellationToken).ConfigureAwait(false);

        // Contradiction pass (§2.13, Phase 12): drop pairs referencing the old
        // version of this doc, then re-evaluate from the fresh chunks.
        if (_detector is not null)
        {
            await _detector.RemoveForPathAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            await _detector.DetectForDocumentAsync(chunks, vectors, indexedAt, cancellationToken).ConfigureAwait(false);
        }

        return count;
    }

    /// <summary>Removes every chunk for the document at <paramref name="path"/> (delete/rename handling), plus its contradiction pairs.</summary>
    public async Task RemoveFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = PathNormalizer.Normalize(path);
        await _indexer.RemoveDocumentAsync(normalized, cancellationToken).ConfigureAwait(false);
        await RemovePairsAsync(normalized, cancellationToken).ConfigureAwait(false);
    }

    private Task RemovePairsAsync(string normalizedPath, CancellationToken cancellationToken)
        => _detector?.RemoveForPathAsync(normalizedPath, cancellationToken) ?? Task.CompletedTask;
}
