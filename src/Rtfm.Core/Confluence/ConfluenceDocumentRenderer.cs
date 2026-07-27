using System.Text;
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
/// structure (contrast the Jira renderer, which had to invent per-comment
/// sections). A metadata blockquote (space · ancestors · version · author) rides
/// with the title chunk.
/// </summary>
public sealed class ConfluenceDocumentRenderer
{
    private readonly HtmlToMarkdownConverter _html = new();

    public RenderedConfluenceDocument Render(ConfluencePage page, string baseUrl, DateTimeOffset pulledAt)
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

        return new RenderedConfluenceDocument(page.Id, page.Title, sb.ToString().TrimEnd(), modifiedAt);
    }

    private static string EscapeInline(string value) => value.Replace("\r", " ").Replace("\n", " ").Trim();
}
