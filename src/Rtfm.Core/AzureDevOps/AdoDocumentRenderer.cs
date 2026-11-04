using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Rtfm.Core.Conversion;

namespace Rtfm.Core.AzureDevOps;

/// <summary>The markdown for one pulled work item, plus the facts the ingester needs.</summary>
public sealed record RenderedAdoDocument(int Id, string Title, string Markdown, DateTimeOffset ModifiedAt);

/// <summary>
/// Renders an <see cref="AdoWorkItem"/> as thread-granular markdown (§2.18, the
/// §2.16 model): the work item is one document; each rich field (Description,
/// Acceptance criteria, Repro steps, custom ones), each linked pull request, and
/// each comment is its own heading — so the chunker gives each its own chunk,
/// breadcrumbed <c>User Story 123: title &gt; Comment by author, date</c> or
/// <c>… &gt; Development &gt; Pull requests &gt; PR 456: title</c>.
///
/// <para><b>Pull requests get a heading each, unlike Jira's.</b> Jira's
/// Development panel carries PR titles only, so they share one section; Azure
/// DevOps returns each PR's <em>description</em> — where the implementation
/// rationale is written — so each is worth its own retrievable chunk (the
/// per-object granularity lesson of §§2.5/15/18/24 once more).</para>
///
/// HTML bodies pass through the shared <see cref="HtmlToMarkdownConverter"/>
/// tail; Markdown bodies (an org with Markdown fields enabled, PR descriptions)
/// pass through with leading <c>#</c> escaped so they can't break the structure.
/// </summary>
public sealed partial class AdoDocumentRenderer
{
    /// <summary>
    /// The top-level sections that hold tracker <em>records</em> rather than
    /// written claims — exempt from contradiction nomination, along with the
    /// metadata lead chunk (<see cref="Contradictions.ContradictionDetector.IsRecordChunk"/>).
    /// Rename a section here, not just in <see cref="Render"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> RecordSections =
        new HashSet<string>(StringComparer.Ordinal) { LinkedSection, LinksSection, DevelopmentSection };

    private const string LinkedSection = "Linked work items";
    private const string LinksSection = "Links";
    private const string DevelopmentSection = "Development";

    private readonly HtmlToMarkdownConverter _html = new();

    public RenderedAdoDocument Render(AdoWorkItem item, string organizationUrl, DateTimeOffset pulledAt, AdoDevelopment? development = null)
    {
        var title = TitleOf(item);
        var modifiedAt = item.Changed ?? item.Created ?? pulledAt;

        var sb = new StringBuilder();
        sb.Append("# ").Append(EscapeInline(title)).Append("\n\n");

        // Metadata blockquote — rides with the title chunk (before the first ##).
        var meta = new List<string>();
        Add(meta, "Type", item.WorkItemType);
        Add(meta, "State", item.State is null ? null : item.Reason is null ? item.State : $"{item.State} ({item.Reason})");
        Add(meta, "Priority", item.Priority);
        Add(meta, "Assigned to", item.AssignedTo);
        Add(meta, "Created by", item.CreatedBy);
        if (item.Changed is { } changed)
        {
            Add(meta, "Updated", changed.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (meta.Count > 0)
        {
            sb.Append("> ").Append(string.Join(" · ", meta)).Append('\n');
        }

        var placement = new List<string>();
        Add(placement, "Area", item.AreaPath);
        Add(placement, "Iteration", item.IterationPath);
        if (placement.Count > 0)
        {
            sb.Append("> ").Append(string.Join(" · ", placement)).Append('\n');
        }

        if (item.Tags.Count > 0)
        {
            sb.Append("> Tags: ").Append(EscapeInline(string.Join(", ", item.Tags))).Append('\n');
        }

        if (item.ParentId is { } parent)
        {
            sb.Append("> Parent: #").Append(parent.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        sb.Append("> Pulled from ").Append(item.WebUrl ?? organizationUrl).Append(" on ")
            .Append(pulledAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" UTC.\n\n");

        foreach (var field in item.RichFields)
        {
            var body = ToMarkdown(field.Content, field.IsMarkdown);
            if (body.Length > 0)
            {
                sb.Append("## ").Append(EscapeInline(field.Label)).Append("\n\n").Append(body).Append("\n\n");
            }
        }

        if (item.Links.Count > 0)
        {
            sb.Append("## ").Append(LinkedSection).Append("\n\n");
            foreach (var link in item.Links)
            {
                sb.Append("- ").Append(EscapeInline(link.Relationship)).Append(" #")
                    .Append(link.Id.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            sb.Append('\n');
        }

        if (item.Hyperlinks.Count > 0)
        {
            sb.Append("## ").Append(LinksSection).Append("\n\n");
            foreach (var hyperlink in item.Hyperlinks)
            {
                sb.Append("- ").Append(EscapeInline(hyperlink.Url));
                if (hyperlink.Comment is { } comment)
                {
                    sb.Append(" — ").Append(EscapeInline(comment));
                }

                sb.Append('\n');
            }

            sb.Append('\n');
        }

        if (development is { IsEmpty: false })
        {
            AppendDevelopment(sb, development);
        }

        foreach (var comment in item.Comments)
        {
            var when = comment.Created is { } c
                ? c.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "unknown date";
            sb.Append("## Comment by ").Append(EscapeInline(comment.Author)).Append(", ").Append(when).Append("\n\n");
            var body = ToMarkdown(comment.Text, comment.IsMarkdown);
            sb.Append(body.Length > 0 ? body : "_(no text)_").Append("\n\n");
        }

        return new RenderedAdoDocument(item.Id, title, sb.ToString().TrimEnd(), modifiedAt);
    }

    /// <summary><c>User Story 4512: Default role for new tenants</c> — the type leads so the breadcrumb says what the thing is.</summary>
    public static string TitleOf(AdoWorkItem item)
        => string.IsNullOrWhiteSpace(item.WorkItemType)
            ? $"#{item.Id.ToString(CultureInfo.InvariantCulture)}: {item.Title}"
            : $"{item.WorkItemType} {item.Id.ToString(CultureInfo.InvariantCulture)}: {item.Title}";

    private static void AppendDevelopment(StringBuilder sb, AdoDevelopment development)
    {
        sb.Append("## ").Append(DevelopmentSection).Append("\n\n");

        if (development.PullRequests.Count > 0 || development.OmittedPullRequests > 0)
        {
            sb.Append("### Pull requests\n\n");
            foreach (var pr in development.PullRequests)
            {
                sb.Append("#### PR ").Append(pr.Id.ToString(CultureInfo.InvariantCulture)).Append(": ")
                    .Append(EscapeInline(pr.Title)).Append("\n\n");

                var status = pr.Status;
                if (pr.IsDraft)
                {
                    status = status is null ? "draft" : $"{status}, draft";
                }

                AppendField(sb, "Status", status);
                if (pr.SourceBranch is { } source)
                {
                    sb.Append("- Branch: `").Append(EscapeInline(source)).Append('`');
                    if (pr.TargetBranch is { } target)
                    {
                        sb.Append(" → `").Append(EscapeInline(target)).Append('`');
                    }

                    sb.Append('\n');
                }

                AppendField(sb, "Repository", pr.Repository);
                AppendField(sb, "Author", pr.Author);

                var reviewers = pr.Reviewers.Select(r => VoteLabel(r.Vote) is { } vote ? $"{r.Name} ({vote})" : r.Name).ToList();
                if (reviewers.Count > 0)
                {
                    AppendField(sb, "Reviewers", string.Join(", ", reviewers));
                }

                if (pr.Created is { } created)
                {
                    AppendField(sb, "Created", created.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC");
                }

                if (pr.Closed is { } closed)
                {
                    AppendField(sb, "Closed", closed.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC");
                }

                AppendField(sb, "URL", pr.Url);
                sb.Append('\n');

                // The description is the reason this section exists: prose, never
                // fenced, so the semantic tier can reach the rationale in it.
                var description = PlainBlock(pr.Description);
                if (description.Length > 0)
                {
                    sb.Append(description).Append("\n\n");
                }
            }

            if (development.OmittedPullRequests > 0)
            {
                sb.Append("_… ").Append(development.OmittedPullRequests.ToString(CultureInfo.InvariantCulture))
                    .Append(" more linked pull request(s), not shown._\n\n");
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

        if (development.Commits.Count > 0 || development.OmittedCommits > 0)
        {
            sb.Append("### Commits\n\n");
            foreach (var commit in development.Commits)
            {
                sb.Append("**`").Append(commit.Id[..Math.Min(8, commit.Id.Length)]).Append("`**");

                var meta = new List<string>();
                if (!string.IsNullOrWhiteSpace(commit.Author))
                {
                    meta.Add(EscapeInline(commit.Author));
                }

                if (commit.AuthoredAt is { } authoredAt)
                {
                    meta.Add(authoredAt.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
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

                var message = PlainBlock(commit.Message);
                if (message.Length > 0)
                {
                    sb.Append(message).Append("\n\n");
                }
            }

            if (development.OmittedCommits > 0)
            {
                sb.Append("_… ").Append(development.OmittedCommits.ToString(CultureInfo.InvariantCulture))
                    .Append(" more linked commit(s), not shown._\n\n");
            }
        }

        if (development.Unreadable > 0)
        {
            sb.Append("_").Append(development.Unreadable.ToString(CultureInfo.InvariantCulture))
                .Append(" linked development item(s) could not be read (deleted, or not visible to this account)._\n\n");
        }
    }

    private static string? VoteLabel(int vote) => vote switch
    {
        >= 10 => "approved",
        >= 5 => "approved with suggestions",
        <= -10 => "rejected",
        <= -5 => "waiting for author",
        _ => null,
    };

    private static void AppendField(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            sb.Append("- ").Append(label).Append(": ").Append(EscapeInline(value)).Append('\n');
        }
    }

    /// <summary>
    /// Plain or Markdown multi-line text (a commit message, a PR description) made
    /// safe to embed: newlines normalized and any leading <c>#</c> escaped so a line
    /// cannot be read as a heading and shatter the section structure.
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

    private string ToMarkdown(string? content, bool isMarkdown)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        return isMarkdown ? PlainBlock(content) : PlainBlock(_html.Convert(content).Markdown);
    }

    private static void Add(List<string> parts, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{label}: {EscapeInline(value)}");
        }
    }

    // Keep metadata lines and headings from being broken by stray line breaks in
    // user-entered text (titles, tags, names).
    private static string EscapeInline(string value) => value.Replace("\r", " ").Replace("\n", " ").Trim();

    [GeneratedRegex(@"^(#{1,6}\s)", RegexOptions.Multiline)]
    private static partial Regex LeadingHeading();
}
