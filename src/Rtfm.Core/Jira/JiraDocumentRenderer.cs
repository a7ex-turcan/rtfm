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
    /// <summary>
    /// How many commits one ticket renders. A long-lived ticket can accumulate
    /// dozens; the overflow is <em>reported</em> in the output rather than
    /// silently dropped (root CLAUDE.md §5, "no silent caps").
    /// </summary>
    internal const int MaxCommitsRendered = 20;

    private readonly HtmlToMarkdownConverter _html = new();

    public RenderedJiraDocument Render(
        JiraIssue issue,
        string baseUrl,
        DateTimeOffset pulledAt,
        JiraDevelopment? development = null)
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

        if (development is { IsEmpty: false })
        {
            AppendDevelopment(sb, development);
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

    /// <summary>
    /// The Development panel as its own section. Pull requests, branches, and
    /// commits get <c>###</c> sub-sections so the heading-aware chunker gives
    /// each its own chunk (breadcrumb <c>KEY: summary &gt; Development &gt; Pull
    /// requests</c>) — the per-object granularity lesson of §§2.5/15/18/24
    /// applied once more, so "which PR implemented this ticket" lands on the pull
    /// request chunk instead of competing with the description.
    /// </summary>
    private static void AppendDevelopment(StringBuilder sb, JiraDevelopment development)
    {
        sb.Append("## Development\n\n");

        if (development.PullRequests.Count > 0)
        {
            sb.Append("### Pull requests\n\n");
            foreach (var pr in development.PullRequests)
            {
                sb.Append("**").Append(EscapeInline(pr.Name)).Append("**");
                if (!string.IsNullOrWhiteSpace(pr.Status))
                {
                    sb.Append(" — ").Append(EscapeInline(pr.Status));
                }

                sb.Append("\n\n");

                if (pr.SourceBranch is { } source)
                {
                    sb.Append("- Branch: `").Append(EscapeInline(source)).Append('`');
                    if (pr.DestinationBranch is { } destination)
                    {
                        sb.Append(" → `").Append(EscapeInline(destination)).Append('`');
                    }

                    sb.Append('\n');
                }

                AppendField(sb, "Repository", pr.Repository);
                AppendField(sb, "Author", pr.Author);

                if (pr.Reviewers.Count > 0)
                {
                    AppendField(sb, "Reviewers", string.Join(", ", pr.Reviewers.Select(r => r.Approved ? $"{r.Name} (approved)" : r.Name)));
                }

                if (pr.LastUpdate is { } updated)
                {
                    AppendField(sb, "Updated", updated.ToUniversalTime().ToString("yyyy-MM-dd HH:mm") + " UTC");
                }

                AppendField(sb, "URL", pr.Url);
                sb.Append('\n');
            }
        }

        if (development.Branches.Count > 0)
        {
            sb.Append("### Branches\n\n");
            foreach (var branch in development.Branches)
            {
                sb.Append("- `").Append(EscapeInline(branch.Name)).Append('`');
                if (!string.IsNullOrWhiteSpace(branch.Repository))
                {
                    sb.Append(" — ").Append(EscapeInline(branch.Repository));
                }

                sb.Append('\n');
            }

            sb.Append('\n');
        }

        if (development.Commits.Count > 0)
        {
            sb.Append("### Commits\n\n");
            foreach (var commit in development.Commits.Take(MaxCommitsRendered))
            {
                sb.Append("**`").Append(EscapeInline(commit.DisplayId)).Append("`**");

                var meta = new List<string>();
                if (!string.IsNullOrWhiteSpace(commit.Author))
                {
                    meta.Add(EscapeInline(commit.Author));
                }

                if (commit.AuthoredAt is { } authoredAt)
                {
                    meta.Add(authoredAt.ToUniversalTime().ToString("yyyy-MM-dd"));
                }

                if (!string.IsNullOrWhiteSpace(commit.Repository))
                {
                    meta.Add(EscapeInline(commit.Repository));
                }

                if (meta.Count > 0)
                {
                    sb.Append(" — ").Append(string.Join(" · ", meta));
                }

                sb.Append("\n\n");

                // The commit message is the whole point of pulling this section:
                // render it as prose, never as a fenced block, so the semantic
                // tier can reach the rationale written in it.
                var message = PlainBlock(commit.Message);
                if (message.Length > 0)
                {
                    sb.Append(message).Append("\n\n");
                }
            }

            if (development.Commits.Count > MaxCommitsRendered)
            {
                sb.Append("_… ").Append(development.Commits.Count - MaxCommitsRendered)
                    .Append(" more commit(s) on this ticket, not shown._\n\n");
            }
        }
    }

    private static void AppendField(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            sb.Append("- ").Append(label).Append(": ").Append(EscapeInline(value)).Append('\n');
        }
    }

    /// <summary>
    /// Plain (non-HTML) multi-line text — a commit message — made safe to embed:
    /// newlines normalized and any leading <c>#</c> escaped so a message line
    /// cannot be read as a heading and shatter the section structure (the §2.16 /
    /// Phase 24 heading-escape lesson, which bites plain text exactly as it bites
    /// converted HTML).
    /// </summary>
    internal static string PlainBlock(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return LeadingHeading().Replace(normalized, @"\$1").Trim();
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
