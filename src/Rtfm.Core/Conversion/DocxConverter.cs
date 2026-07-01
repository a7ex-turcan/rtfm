namespace Rtfm.Core.Conversion;

/// <summary>
/// Converts a genuine Word / Open XML (.docx) document to markdown (§2.5, Route
/// for docx). Mammoth maps Word *paragraph styles* to semantic HTML (a
/// "Heading 1" style becomes <c>&lt;h1&gt;</c>), then the shared
/// <see cref="HtmlToMarkdownConverter"/> strips and renders it — the same tail
/// the MHTML route uses. Mammoth embeds images as base64 data URIs; the tail
/// drops <c>&lt;img&gt;</c> anyway.
/// </summary>
public sealed class DocxConverter
{
    private readonly HtmlToMarkdownConverter _html = new();
    private readonly Mammoth.DocumentConverter _mammoth = new();

    public ConversionResult Convert(Stream input, string sourcePath)
    {
        // Mammoth keys off real heading styles; manually-bolded "headings" flatten
        // to paragraphs (§2.5 caveat). Warnings are available on result.Warnings
        // if we ever need to surface style problems.
        var result = _mammoth.ConvertToHtml(input);
        var conversion = _html.Convert(result.Value);

        return new ConversionResult(
            SourcePath: sourcePath,
            Format: DocumentFormat.Docx,
            Markdown: conversion.Markdown,
            Title: conversion.Title);
    }
}
