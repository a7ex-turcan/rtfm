namespace Rtfm.Core.Jira;

/// <summary>
/// Builds the canonical <c>source_path</c> key for an indexed Jira ticket
/// (§2.16). This is the Jira analogue of <see cref="Indexing.PathNormalizer"/>:
/// the single builder used identically on index and delete so exact-match
/// delete-by-query (§2.9) holds. A ticket has no file path, so its key is the
/// synthetic <c>jira://KEY</c> — and it must <b>not</b> flow through
/// <c>PathNormalizer</c>, whose <c>Path.GetFullPath</c> would mangle the URI
/// into a filesystem path. Issue keys are case-insensitive in Jira, so the key
/// is upper-cased for a stable canonical form.
/// </summary>
public static class JiraSource
{
    /// <summary>The synthetic URI scheme for a Jira ticket's source key.</summary>
    public const string Scheme = "jira://";

    /// <summary>The canonical <c>source_path</c> for a ticket, e.g. <c>jira://AEXP-123</c>.</summary>
    public static string Key(string issueKey) => Scheme + issueKey.Trim().ToUpperInvariant();

    /// <summary>True when a stored <c>source_path</c> belongs to a Jira ticket.</summary>
    public static bool IsJiraKey(string sourcePath) => sourcePath.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);
}
