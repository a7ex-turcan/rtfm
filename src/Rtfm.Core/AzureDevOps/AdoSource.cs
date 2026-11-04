using System.Globalization;
using System.Text.RegularExpressions;

namespace Rtfm.Core.AzureDevOps;

/// <summary>
/// Builds the canonical <c>source_path</c> key for an indexed work item (§2.18)
/// — the Azure DevOps analogue of <see cref="Jira.JiraSource"/> and, like it,
/// deliberately <b>not</b> routed through <see cref="Indexing.PathNormalizer"/>
/// (whose <c>Path.GetFullPath</c> would mangle the URI).
///
/// <para><b>The organization is part of the key.</b> A Jira key carries its
/// project prefix (<c>AEXP-19</c>), so a bare key is effectively unique; a work
/// item id is a bare integer, unique only within one organization. Two RTFM
/// projects pointed at two organizations can both hold a <c>#123</c>, and since
/// re-indexing deletes by <c>source_path</c> alone (§2.9), an org-less key would
/// let one silently replace the other. Organization names are case-insensitive,
/// so the key lower-cases it.</para>
/// </summary>
public static partial class AdoSource
{
    /// <summary>The synthetic URI scheme for a work item's source key.</summary>
    public const string Scheme = "ado://";

    /// <summary>The canonical <c>source_path</c>, e.g. <c>ado://contoso/4512</c>.</summary>
    public static string Key(string organization, int id)
        => $"{Scheme}{organization.Trim().ToLowerInvariant()}/{id.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>True when a stored <c>source_path</c> belongs to an Azure DevOps work item.</summary>
    public static bool IsAdoKey(string sourcePath) => sourcePath.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The work item id named by a seed argument: a bare id (<c>4512</c>), a
    /// mention (<c>#4512</c>), or any URL the browser shows for one
    /// (<c>…/_workitems/edit/4512</c>, a board's <c>?workitem=4512</c>, or a
    /// REST <c>…/workItems/4512</c>). Null when it names none.
    /// </summary>
    public static int? ParseWorkItemId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var trimmed = input.Trim().TrimStart('#');
        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var bare))
        {
            return bare > 0 ? bare : null;
        }

        var match = WorkItemUrl().Match(input);
        return match.Success && int.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : null;
    }

    [GeneratedRegex(@"(?:/_workitems/edit/|[?&]workitem=|/workitems/)(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex WorkItemUrl();
}
