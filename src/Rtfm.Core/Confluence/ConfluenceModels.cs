using System.Globalization;

namespace Rtfm.Core.Confluence;

/// <summary>
/// One pulled Confluence page (§2.17). <see cref="BodyHtml"/> is the rendered
/// <c>body.view</c> — real HTML with real headings — so it reuses the shared
/// strip→ReverseMarkdown tail and the heading-aware chunker with no synthetic
/// structure. <see cref="VersionNumber"/> is the monotonic edit counter used for
/// change detection; <see cref="VersionWhen"/> is the recency signal.
/// </summary>
public sealed record ConfluencePage(
    string Id,
    string Title,
    string Type,
    string? SpaceKey,
    IReadOnlyList<string> Ancestors,
    // Ancestor *ids*, same order as Ancestors (root first). Titles alone can't
    // join a page to its parent reliably — they're only unique within a space —
    // so the id is what a hierarchy view keys on.
    IReadOnlyList<string> AncestorIds,
    int VersionNumber,
    DateTimeOffset? VersionWhen,
    string? VersionBy,
    string BodyHtml,
    IReadOnlyList<string> ChildPageIds,
    IReadOnlyList<string> LinkedPageIds)
{
    /// <summary>True for real page content (not a folder/whiteboard container, which has no body worth indexing).</summary>
    public bool IsPage => string.Equals(Type, "page", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What the user pointed at (§2.17 step 2): one page (+ its subtree), a folder (its subtree), or a whole space.</summary>
public enum ConfluenceSeedKind
{
    Page,
    Folder,
    Space,
}

/// <summary>A resolved indexing seed: a kind plus the content id (page/folder) or space key it names.</summary>
public sealed record ConfluenceSeed(ConfluenceSeedKind Kind, string Value);

/// <summary>
/// One comment on a page (§ comment-indexing enhancement). <see cref="Location"/>
/// is <c>inline</c> (anchored to highlighted page text — <see cref="AnchorText"/>
/// holds that highlighted selection) or <c>footer</c> (a general page comment).
/// <see cref="BodyHtml"/> is the rendered comment body, reused through the shared
/// tail like a page body.
/// </summary>
public sealed record ConfluenceComment(
    string Id,
    string Author,
    DateTimeOffset? Created,
    string Location,
    string? AnchorText,
    string? Resolution,
    string BodyHtml)
{
    /// <summary>True for a text-anchored inline comment (vs a footer/general comment).</summary>
    public bool IsInline => string.Equals(Location, "inline", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Raised for a Confluence API failure the CLI should report cleanly (auth, not-found, HTTP error).</summary>
public sealed class ConfluenceException(string message) : Exception(message);

/// <summary>Parses Confluence's ISO-8601 timestamps (<c>2026-07-01T12:18:00.611Z</c>). Never throws.</summary>
public static class ConfluenceDate
{
    public static DateTimeOffset? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces,
            out var parsed)
            ? parsed
            : null;
    }
}
