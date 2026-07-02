namespace Rtfm.Core.Search;

/// <summary>One indexed document, as enumerated by <c>list_sources</c> (Phase 8).</summary>
public sealed record SourceInfo(
    string Path,
    string? Title,
    string Project,
    DateTimeOffset? SourceModifiedAt,
    long ChunkCount);

/// <summary>
/// A full document reassembled from its chunks in ordinal order (Phase 8).
/// The markdown is the *converted, indexed* form — headings deduplicated from
/// the per-chunk breadcrumbs. Sections that were split with overlap may repeat
/// a little text at the seams; harmless for LLM reading.
/// </summary>
public sealed record DocumentContent(
    string Path,
    string? Title,
    string Project,
    DateTimeOffset? SourceModifiedAt,
    int ChunkCount,
    string Markdown);

/// <summary>A semantically similar document (Phase 8): best-matching chunk decides the score.</summary>
public sealed record SimilarDoc(
    string Path,
    string? Title,
    string Project,
    double Score,
    string BestMatchingHeading,
    DateTimeOffset? SourceModifiedAt);

/// <summary>
/// <c>find_similar</c> outcome. <see cref="VectorsAvailable"/> is false when the
/// reference document has no embeddings (indexed lexical-only) — distinct from
/// "no similar documents exist".
/// </summary>
public sealed record SimilarDocsResult(
    bool VectorsAvailable,
    IReadOnlyList<SimilarDoc> Similar);
