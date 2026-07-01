using MimeKit;

namespace Rtfm.Core.Conversion;

/// <summary>
/// Converts a Confluence "Export to Word" file (MHTML) to markdown. MimeKit
/// parses the <c>multipart/related</c> container and transparently decodes the
/// quoted-printable <c>text/html</c> part (honouring its charset); the shared
/// <see cref="HtmlToMarkdownConverter"/> does the rest.
/// </summary>
public sealed class MhtmlConverter
{
    private readonly HtmlToMarkdownConverter _html = new();

    public ConversionResult Convert(Stream input, string sourcePath)
    {
        var message = MimeMessage.Load(input);

        var html = message.HtmlBody
            ?? throw new InvalidDataException(
                $"No text/html part found in MHTML document: {sourcePath}");

        var conversion = _html.Convert(html);

        return new ConversionResult(
            SourcePath: sourcePath,
            Format: DocumentFormat.Mhtml,
            Markdown: conversion.Markdown,
            Title: conversion.Title);
    }
}
