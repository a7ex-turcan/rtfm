using System.Text.RegularExpressions;

namespace Rtfm.Core.Confluence;

/// <summary>
/// Builds the canonical <c>source_path</c> key for an indexed Confluence page
/// (§2.17), and parses a page id out of what a user pastes. Like
/// <see cref="Jira.JiraSource"/> this is the analogue of
/// <see cref="Indexing.PathNormalizer"/> — the single builder used identically
/// on index and delete — and must <b>not</b> flow through <c>PathNormalizer</c>
/// (its <c>Path.GetFullPath</c> would mangle the URI). Page ids are numeric, so
/// the key needs no case folding.
/// </summary>
public static partial class ConfluenceSource
{
    /// <summary>The synthetic URI scheme for a Confluence page's source key.</summary>
    public const string Scheme = "confluence://";

    /// <summary>The canonical <c>source_path</c> for a page, e.g. <c>confluence://6598099635</c>.</summary>
    public static string Key(string pageId) => Scheme + pageId.Trim();

    /// <summary>True when a stored <c>source_path</c> belongs to a Confluence page.</summary>
    public static bool IsConfluenceKey(string sourcePath) => sourcePath.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts a page id from a page URL (<c>.../pages/{id}/…</c>) or a bare
    /// numeric id. Returns null when neither shape matches (e.g. a Confluence
    /// short link, which carries no id — the user must paste the full URL).
    /// </summary>
    public static string? ParsePageId(string input)
    {
        var s = (input ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            return null;
        }

        if (s.All(char.IsDigit))
        {
            return s;
        }

        var match = PagesUrl().Match(s);
        return match.Success ? match.Groups["id"].Value : null;
    }

    /// <summary>
    /// Resolves what a user pointed at (§2.17 step 2) into a <see cref="ConfluenceSeed"/>:
    /// a page URL (<c>/pages/{id}</c>), a folder URL (<c>/folder/{id}</c>), a space
    /// URL (<c>/spaces/{KEY}/…</c> with no page/folder segment), or a bare id
    /// (treated as a page — a folder id still works, since scope resolution keys on
    /// descendants and a folder self is skipped as non-page). <paramref name="spaceOverride"/>
    /// (the <c>--space</c> flag) forces a whole-space seed. Null when nothing matches.
    /// </summary>
    public static ConfluenceSeed? ParseSeed(string input, string? spaceOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(spaceOverride))
        {
            return new ConfluenceSeed(ConfluenceSeedKind.Space, spaceOverride.Trim());
        }

        var s = (input ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            return null;
        }

        if (s.All(char.IsDigit))
        {
            return new ConfluenceSeed(ConfluenceSeedKind.Page, s);
        }

        if (PagesUrl().Match(s) is { Success: true } page)
        {
            return new ConfluenceSeed(ConfluenceSeedKind.Page, page.Groups["id"].Value);
        }

        if (FolderUrl().Match(s) is { Success: true } folder)
        {
            return new ConfluenceSeed(ConfluenceSeedKind.Folder, folder.Groups["id"].Value);
        }

        // A space URL: /spaces/{KEY}/... with no /pages/ or /folder/ segment.
        if (SpaceUrl().Match(s) is { Success: true } space)
        {
            return new ConfluenceSeed(ConfluenceSeedKind.Space, space.Groups["key"].Value);
        }

        return null;
    }

    [GeneratedRegex(@"/pages/(?<id>\d+)")]
    private static partial Regex PagesUrl();

    [GeneratedRegex(@"/folder/(?<id>\d+)")]
    private static partial Regex FolderUrl();

    [GeneratedRegex(@"/spaces/(?<key>[^/?#]+)")]
    private static partial Regex SpaceUrl();
}
