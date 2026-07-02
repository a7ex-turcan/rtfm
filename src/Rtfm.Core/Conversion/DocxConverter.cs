using System.IO.Compression;
using System.Xml.Linq;

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
    private static readonly XNamespace DcTerms = "http://purl.org/dc/terms/";

    private readonly HtmlToMarkdownConverter _html = new();
    private readonly Mammoth.DocumentConverter _mammoth = new();

    public ConversionResult Convert(Stream input, string sourcePath)
    {
        // Pull the embedded modified date from docProps/core.xml before Mammoth
        // reads the stream (§2.13 A). Mammoth keys off real heading styles;
        // manually-bolded "headings" flatten to paragraphs (§2.5 caveat).
        var modifiedAt = TryReadModified(input);

        var result = _mammoth.ConvertToHtml(input);
        var conversion = _html.Convert(result.Value);

        return new ConversionResult(
            SourcePath: sourcePath,
            Format: SourceFormat.Docx,
            Markdown: conversion.Markdown,
            Title: conversion.Title,
            SourceModifiedAt: modifiedAt);
    }

    private static DateTimeOffset? TryReadModified(Stream input)
    {
        if (!input.CanSeek)
        {
            return null;
        }

        var origin = input.Position;
        try
        {
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            var core = zip.GetEntry("docProps/core.xml");
            if (core is null)
            {
                return null;
            }

            using var stream = core.Open();
            var modified = XDocument.Load(stream).Descendants(DcTerms + "modified").FirstOrDefault();
            return modified is not null && DateTimeOffset.TryParse(modified.Value, out var value)
                ? value
                : null;
        }
        catch (InvalidDataException)
        {
            return null; // not a valid zip / unreadable core props — fall back to mtime
        }
        finally
        {
            input.Position = origin;
        }
    }
}
