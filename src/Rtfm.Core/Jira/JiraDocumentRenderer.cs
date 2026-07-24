using System.Text;
using System.Text.RegularExpressions;
using Rtfm.Core.Conversion;

namespace Rtfm.Core.Jira;

/// <summary>The markdown for one pulled ticket, plus the facts the ingester needs.</summary>
public sealed record RenderedJiraDocument(string Key, string Title, string Markdown, DateTimeOffset ModifiedAt);

/// <summary>
/// Renders a <see cref="JiraIssue"/> as thread-granular markdown (§2.16): the
/// ticket is one document, the description and each comment their own
/// <c>##</c> section, so the chunker yields one chunk per comment with the
/// breadcrumb <c>KEY: summary &gt; Comment by author, date</c>. Body fields
/// arrive as rendered HTML and pass through the shared
/// <see cref="HtmlToMarkdownConverter"/> tail — the same one every other format
/// uses — so no new HTML→markdown code exists here.
/// </summary>
public sealed partial class JiraDocumentRenderer
{
    private readonly HtmlToMarkdownConverter _html = new();

    public RenderedJiraDocument Render(JiraIssue issue, string baseUrl, DateTimeOffset pulledAt)
    {
        var title = $"{issue.Key}: {issue.Summary}";
        var modifiedAt = issue.Updated ?? issue.Created ?? pulledAt;

        var sb = new StringBuilder();
        sb.Append("# ").Append(EscapeInline(title)).Append("\n\n");

        // Metadata blockquote — rides with the title chunk (before the first ##).
        var meta = new List<string>();
        Add(meta, "Type", issue.IssueType);
        Add(meta, "Status", issue.Status);
        Add(meta, "Priority", issue.Priority);
        Add(meta, "Reporter", issue.Reporter);
        Add(meta, "Assignee", issue.Assignee);
        if (issue.Updated is { } u)
        {
            Add(meta, "Updated", u.ToUniversalTime().ToString("yyyy-MM-dd"));
        }

        if (meta.Count > 0)
        {
            sb.Append("> ").Append(string.Join(" · ", meta)).Append('\n');
        }

        if (issue.Labels.Count > 0)
        {
            sb.Append("> Labels: ").Append(EscapeInline(string.Join(", ", issue.Labels))).Append('\n');
        }

        if (issue.ParentKey is { } parent)
        {
            sb.Append("> Parent: ").Append(parent).Append('\n');
        }

        sb.Append("> Pulled from ").Append(baseUrl).Append(" on ")
            .Append(pulledAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm")).Append(" UTC.\n\n");

        var description = ToMarkdown(issue.DescriptionHtml);
        if (description.Length > 0)
        {
            sb.Append("## Description\n\n").Append(description).Append("\n\n");
        }

        if (issue.Links.Count > 0)
        {
            sb.Append("## Linked issues\n\n");
            foreach (var link in issue.Links)
            {
                sb.Append("- ").Append(EscapeInline(link.Relationship)).Append(' ').Append(link.Key).Append('\n');
            }

            sb.Append('\n');
        }

        foreach (var comment in issue.Comments)
        {
            var when = comment.Created is { } c ? c.ToUniversalTime().ToString("yyyy-MM-dd HH:mm") : "unknown date";
            sb.Append("## Comment by ").Append(EscapeInline(comment.Author)).Append(", ").Append(when).Append("\n\n");
            var body = ToMarkdown(comment.BodyHtml);
            sb.Append(body.Length > 0 ? body : "_(no text)_").Append("\n\n");
        }

        return new RenderedJiraDocument(issue.Key.ToUpperInvariant(), title, sb.ToString().TrimEnd(), modifiedAt);
    }

    private string ToMarkdown(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var markdown = _html.Convert(html).Markdown;
        // A description/comment may itself contain heading markup; escape leading
        // '#' so it can't be read as a top-level section and shatter the
        // per-message structure (the §2.16 / Phase 24 heading-escape lesson).
        return LeadingHeading().Replace(markdown, @"\$1").Trim();
    }

    private static void Add(List<string> parts, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{label}: {EscapeInline(value)}");
        }
    }

    // Keep the metadata line and headings from being broken by stray markup
    // characters in user-entered text (summaries, labels, author names).
    private static string EscapeInline(string value) => value.Replace("\r", " ").Replace("\n", " ").Trim();

    [GeneratedRegex(@"^(#{1,6}\s)", RegexOptions.Multiline)]
    private static partial Regex LeadingHeading();
}
