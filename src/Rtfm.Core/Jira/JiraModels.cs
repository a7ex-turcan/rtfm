using System.Globalization;
using System.Text.RegularExpressions;

namespace Rtfm.Core.Jira;

/// <summary>One pulled Jira ticket (§2.16). Body fields are <b>rendered HTML</b>
/// (from <c>expand=renderedFields</c>) so they reuse the shared
/// strip→ReverseMarkdown tail; metadata dates are the machine (ISO) values from
/// the raw fields.</summary>
public sealed record JiraIssue(
    string Key,
    string Summary,
    string? Status,
    string? IssueType,
    string? Reporter,
    string? Assignee,
    string? Priority,
    IReadOnlyList<string> Labels,
    DateTimeOffset? Created,
    DateTimeOffset? Updated,
    string? DescriptionHtml,
    IReadOnlyList<JiraComment> Comments,
    IReadOnlyList<JiraLink> Links,
    string? ParentKey,
    IReadOnlyList<string> Subtasks);

/// <summary>One comment: author + machine date from the raw field, body as rendered HTML.</summary>
public sealed record JiraComment(string Author, DateTimeOffset? Created, string BodyHtml);

/// <summary>
/// One issue link — the relationship phrase (e.g. "blocks", "is blocked by")
/// and the linked ticket's key. <see cref="Outward"/> distinguishes the two
/// ends for display; traversal (step 2) only needs <see cref="Key"/>.
/// </summary>
public sealed record JiraLink(string Relationship, string Key, bool Outward);

/// <summary>Raised for a Jira API failure the CLI should report cleanly (auth, not-found, HTTP error).</summary>
public sealed class JiraException(string message) : Exception(message);

/// <summary>
/// Parses Jira's timestamps. Jira Cloud returns ISO-8601 with a
/// <b>colon-less</b> zone offset (<c>2026-07-15T12:33:20.256+0400</c>), which
/// <see cref="DateTimeOffset.Parse(string)"/> rejects; a colon is inserted
/// before parsing. Kept as a testable seam.
/// </summary>
public static partial class JiraDate
{
    /// <summary>Parses a Jira timestamp, or null when absent/unparseable (never throws).</summary>
    public static DateTimeOffset? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = ColonlessOffset().Replace(value.Trim(), "${h}:${m}");
        return DateTimeOffset.TryParse(
            normalized, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces,
            out var parsed)
            ? parsed
            : null;
    }

    // A trailing +hhmm / -hhmm zone offset with no colon.
    [GeneratedRegex(@"(?<h>[+-]\d{2})(?<m>\d{2})$")]
    private static partial Regex ColonlessOffset();
}
