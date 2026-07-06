namespace Rtfm.Core.Search;

/// <summary>An override note attached to a document hit (§2.13 C — the "annotates" half).</summary>
public sealed record NoteAnnotation(
    string Id,
    string Text,
    string Author,
    DateTimeOffset CreatedAt);

/// <summary>
/// One search result (§2.13 B, §2.14). <see cref="Origin"/> is <c>"doc"</c> for
/// indexed document chunks and <c>"note"</c> for user-confirmed override notes
/// (Phase 13) — overrides are always visibly attributed, never disguised as
/// source text. Doc hits may carry <see cref="Annotations"/>: override notes
/// anchored to their source document.
/// </summary>
public sealed record SearchHit(
    double Score,
    string Project,
    string SourcePath,
    string HeadingPath,
    string? Title,
    string Content,
    DateTimeOffset? SourceModifiedAt,
    string Origin = "doc",
    string? Author = null,
    IReadOnlyList<NoteAnnotation>? Annotations = null,
    int? Ordinal = null);
