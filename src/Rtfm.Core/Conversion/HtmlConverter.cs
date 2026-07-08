using System.Globalization;
using System.Text.RegularExpressions;

namespace Rtfm.Core.Conversion;

/// <summary>
/// Converts a bare HTML document to markdown — Jira's "Export to Word" output
/// (a <c>.doc</c> that is plain HTML, not MHTML) and ordinary <c>.html</c>/
/// <c>.htm</c> files. The shared <see cref="HtmlToMarkdownConverter"/> does the
/// strip → ReverseMarkdown work; this route only supplies what the shared tail
/// can't see once <c>&lt;head&gt;</c> is stripped: the document <c>&lt;title&gt;</c>
/// and, for Jira exports, the "Updated:" byline as a recency signal (§2.13 A).
/// </summary>
public sealed partial class HtmlConverter
{
    private readonly HtmlToMarkdownConverter _html = new();

    public ConversionResult Convert(Stream input, string sourcePath)
    {
        using var reader = new StreamReader(input);
        var html = reader.ReadToEnd();

        var conversion = _html.Convert(html);

        // The shared tail strips <head>, so its title falls back to the first
        // <h1>. Jira exports have no <h1> — recover the <title> ourselves.
        var title = conversion.Title ?? ExtractTitle(html);

        return new ConversionResult(
            SourcePath: sourcePath,
            Format: SourceFormat.Html,
            Markdown: conversion.Markdown,
            Title: title,
            SourceModifiedAt: ExtractUpdated(html));
    }

    private static string? ExtractTitle(string html)
    {
        var match = TitleTag().Match(html);
        if (!match.Success)
        {
            return null;
        }

        var title = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
        return string.IsNullOrEmpty(title) ? null : title;
    }

    /// <summary>
    /// Jira stamps <c>Updated: yyyy/MM/dd</c> in the issue byline — the closest
    /// analog to Confluence's "last modified" line (§2.13 A). Best-effort: absent
    /// or unparseable → null, and the indexer falls back to file mtime.
    /// </summary>
    private static DateTimeOffset? ExtractUpdated(string html)
    {
        var match = UpdatedByline().Match(html);
        if (match.Success
            && DateTimeOffset.TryParseExact(
                match.Groups[1].Value,
                "yyyy/MM/dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var updated))
        {
            return updated;
        }

        return null;
    }

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"Updated:\s*(\d{4}/\d{2}/\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex UpdatedByline();
}
