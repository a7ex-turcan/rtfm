using System.Text;
using System.Text.RegularExpressions;
using Rtfm.Core.Conversion;

namespace Rtfm.Core.Confluence;

/// <summary>The markdown for one pulled page, plus the facts the ingester needs.</summary>
public sealed record RenderedConfluenceDocument(string PageId, string Title, string Markdown, DateTimeOffset ModifiedAt);

/// <summary>
/// Renders a <see cref="ConfluencePage"/> as markdown (§2.17). The page title
/// becomes the <c>#</c> heading and the rendered <c>body.view</c> HTML — which
/// already carries its own <c>##</c>/<c>###</c> headings — passes through the
/// shared <see cref="HtmlToMarkdownConverter"/> tail, so the existing
/// heading-aware chunker yields sensibly-breadcrumbed chunks with no synthetic
/// structure. A metadata blockquote (space · ancestors · version · author) rides
/// with the title chunk. Comments — footer (general) and inline (with the
/// highlighted passage they annotate) — are appended as their own <c>##</c>
/// sections so each becomes its own retrievable chunk, like the Jira renderer's
/// per-comment sections.
/// </summary>
public sealed partial class ConfluenceDocumentRenderer
{
    private readonly HtmlToMarkdownConverter _html = new();

    public RenderedConfluenceDocument Render(
        ConfluencePage page,
        IReadOnlyList<ConfluenceComment> comments,
        string baseUrl,
        DateTimeOffset pulledAt)
    {
        var modifiedAt = page.VersionWhen ?? pulledAt;

        var sb = new StringBuilder();
        sb.Append("# ").Append(EscapeInline(page.Title)).Append("\n\n");

        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(page.SpaceKey))
        {
            meta.Add($"Space: {EscapeInline(page.SpaceKey!)}");
        }

        if (page.Ancestors.Count > 0)
        {
            meta.Add($"Path: {EscapeInline(string.Join(" > ", page.Ancestors))}");
        }

        meta.Add($"Version {page.VersionNumber}");
        if (!string.IsNullOrWhiteSpace(page.VersionBy))
        {
            meta.Add($"by {EscapeInline(page.VersionBy!)}");
        }

        if (page.VersionWhen is { } when)
        {
            meta.Add($"Updated {when.ToUniversalTime():yyyy-MM-dd}");
        }

        sb.Append("> ").Append(string.Join(" · ", meta)).Append('\n');

        var pageUrl = string.IsNullOrWhiteSpace(page.SpaceKey)
            ? $"{baseUrl}/wiki/pages/viewpage.action?pageId={page.Id}"
            : $"{baseUrl}/wiki/spaces/{page.SpaceKey}/pages/{page.Id}";
        sb.Append("> Pulled from ").Append(pageUrl).Append(" on ")
            .Append(pulledAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm")).Append(" UTC.\n\n");

        var body = string.IsNullOrWhiteSpace(page.BodyHtml) ? string.Empty : _html.Convert(page.BodyHtml).Markdown.Trim();
        sb.Append(body.Length > 0 ? body : "_(no content)_");

        AppendComments(sb, comments);

        return new RenderedConfluenceDocument(page.Id, page.Title, sb.ToString().TrimEnd(), modifiedAt);
    }

    private void AppendComments(StringBuilder sb, IReadOnlyList<ConfluenceComment> comments)
    {
        // Oldest first so a thread reads in order.
        foreach (var comment in comments.OrderBy(c => c.Created ?? DateTimeOffset.MinValue))
        {
            var when = comment.Created is { } c ? c.ToUniversalTime().ToString("yyyy-MM-dd") : "unknown date";

            sb.Append("\n\n## ").Append(comment.IsInline ? "Inline comment by " : "Comment by ")
                .Append(EscapeInline(comment.Author)).Append(", ").Append(when);
            if (comment.IsInline && string.Equals(comment.Resolution, "resolved", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(" (resolved)");
            }

            sb.Append("\n\n");

            // The highlighted passage an inline comment annotates — the context
            // that makes the comment searchable by what it's about.
            if (comment.IsInline && !string.IsNullOrWhiteSpace(comment.AnchorText))
            {
                sb.Append("> On: \"").Append(EscapeInline(Excerpt(comment.AnchorText!))).Append("\"\n\n");
            }

            var body = string.IsNullOrWhiteSpace(comment.BodyHtml) ? string.Empty : ToMarkdown(comment.BodyHtml);
            sb.Append(body.Length > 0 ? body : "_(no text)_");
        }
    }

    private string ToMarkdown(string html)
    {
        var markdown = _html.Convert(html).Markdown;
        // Escape leading '#' so a comment body can't be read as a section and
        // shatter the per-comment structure (the §2.16 / Phase 24 lesson).
        return LeadingHeading().Replace(markdown, @"\$1").Trim();
    }

    private static string Excerpt(string text)
    {
        var flat = EscapeInline(text.Replace("\"", "'"));
        return flat.Length > 160 ? flat[..157] + "…" : flat;
    }

    private static string EscapeInline(string value) => value.Replace("\r", " ").Replace("\n", " ").Trim();

    [GeneratedRegex(@"^(#{1,6}\s)", RegexOptions.Multiline)]
    private static partial Regex LeadingHeading();
}
