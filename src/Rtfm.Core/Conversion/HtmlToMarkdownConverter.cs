using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Rtfm.Core.Conversion;

/// <summary>
/// The shared back end for every conversion route (§2.5): strip boilerplate
/// with a DOM pass, then render HTML → markdown with ReverseMarkdown. Working on
/// the DOM (not regexes over markdown) keeps the boilerplate rules legible.
/// </summary>
public sealed class HtmlToMarkdownConverter
{
    private const char NonBreakingSpace = ' ';

    /// <summary>Elements removed entirely — noise that must not reach the markdown.</summary>
    private static readonly string[] RemoveSelectors =
    [
        "script", "style", "head", "noscript",
        "img",                       // MHTML images are unresolvable base64 parts; drop them
        "#footer", ".footer",        // Confluence page footer ("Created by … last modified …")
        ".page-metadata",
    ];

    /// <summary>
    /// Attributes worth keeping. Everything else (Confluence editor cruft like
    /// <c>local-id</c>, <c>class</c>, <c>data-*</c>, <c>style</c>) is dropped so
    /// it can't leak into raw-HTML table cells that ReverseMarkdown passes through.
    /// </summary>
    private static readonly HashSet<string> KeepAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "colspan", "rowspan", "start",
    };

    private static readonly Regex BrRuns = new(@"(?:<br\s*/?>\s*){2,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BlankLines = new(@"\n{3,}", RegexOptions.Compiled);
    // Collapse the leftover space where a Confluence heading emoji used to sit: "##  Title" → "## Title".
    private static readonly Regex HeadingSpace = new(@"^(#{1,6}) +", RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly HtmlParser _parser = new();
    private readonly ReverseMarkdown.Converter _markdown = new(new ReverseMarkdown.Config
    {
        GithubFlavored = true,                                       // pipe tables
        UnknownTags = ReverseMarkdown.Config.UnknownTagsOption.Bypass, // keep text of unknown tags
        RemoveComments = true,
        SmartHrefHandling = true,
    });

    /// <summary>Converts a full HTML document to markdown and extracts a best-effort title.</summary>
    public HtmlConversion Convert(string html)
    {
        var document = _parser.ParseDocument(html);

        // 1) Remove boilerplate by selector (must run before attributes are
        //    stripped, since it matches on class/id).
        foreach (var selector in RemoveSelectors)
        {
            foreach (var element in document.QuerySelectorAll(selector).ToArray())
            {
                element.Remove();
            }
        }

        var title = ExtractTitle(document);

        // 2) Strip non-essential attributes everywhere.
        foreach (var element in document.All)
        {
            var drop = element.Attributes
                .Select(a => a.Name)
                .Where(name => !KeepAttributes.Contains(name))
                .ToArray();
            foreach (var name in drop)
            {
                element.RemoveAttribute(name);
            }
        }

        // Prefer the body; some fragments have no <body> element.
        var content = document.Body?.InnerHtml ?? document.DocumentElement.InnerHtml;
        var markdown = Normalize(_markdown.Convert(content));

        return new HtmlConversion(markdown, title);
    }

    /// <summary>Tidies conversion artifacts: nbsp, stray entities, and runs of breaks/blank lines.</summary>
    private static string Normalize(string markdown)
    {
        markdown = markdown
            .Replace(NonBreakingSpace, ' ')   // Confluence emoji spacers
            .Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("&gt;", ">");

        markdown = BrRuns.Replace(markdown, "<br>");
        markdown = BlankLines.Replace(markdown, "\n\n");
        markdown = HeadingSpace.Replace(markdown, "$1 ");
        return markdown.Trim();
    }

    private static string? ExtractTitle(IDocument document)
    {
        var heading = document.QuerySelector("h1")?.TextContent?.Trim();
        if (!string.IsNullOrEmpty(heading))
        {
            return heading;
        }

        var docTitle = document.Title?.Trim();
        return string.IsNullOrEmpty(docTitle) ? null : docTitle;
    }
}

/// <summary>Markdown plus the title teased out of the same HTML pass.</summary>
public readonly record struct HtmlConversion(string Markdown, string? Title);
