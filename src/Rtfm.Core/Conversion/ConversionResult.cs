namespace Rtfm.Core.Conversion;

/// <summary>
/// The markdown produced from one source document, plus what we could learn
/// about it. <see cref="Title"/> is best-effort (first heading / document title)
/// and may be null.
/// </summary>
public sealed record ConversionResult(
    string SourcePath,
    DocumentFormat Format,
    string Markdown,
    string? Title);
