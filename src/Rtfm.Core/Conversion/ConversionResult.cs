namespace Rtfm.Core.Conversion;

/// <summary>
/// The markdown produced from one source document, plus what we could learn
/// about it. <see cref="Title"/> is best-effort (first heading / document title)
/// and may be null. <see cref="SourceModifiedAt"/> is the document's *embedded*
/// modified date when the format carries one (§2.13 A); it is null when there is
/// none, and the indexer falls back to file mtime.
/// </summary>
public sealed record ConversionResult(
    string SourcePath,
    DocumentFormat Format,
    string Markdown,
    string? Title,
    DateTimeOffset? SourceModifiedAt = null);
