using Rtfm.Core.Configuration;

namespace Rtfm.Core.AzureDevOps;

/// <summary>
/// The per-project Azure DevOps descriptor (§2.18), written by
/// <c>rtfm ado config</c> and read by <c>index</c>/<c>watch</c>. Holds the
/// organization URL (not secret) and <b>how to authenticate</b>, never a
/// credential:
/// <list type="bullet">
///   <item><c>az</c> (the default) — a short-lived Entra ID bearer token taken
///   from the developer's existing <c>az login</c> at call time
///   (<see cref="AzCliCredential"/>). Nothing secret is stored at all.</item>
///   <item><c>pat</c> — a <b><c>${ENV}</c> reference</b> to a Personal Access
///   Token (Work Items Read; Code Read for the Development section), expanded
///   lazily by <see cref="ResolveToken"/> — the <c>.rtfmdb</c> / Jira secret
///   discipline (§2.15, §2.16).</item>
/// </list>
/// Work item ids are unique across an <em>organization</em>, not a project, so
/// the organization is the only scope stored; each item's own
/// <c>System.TeamProject</c> drives the project-scoped calls (comments, web
/// links). The traversal knobs leash the crawl exactly as Jira's do.
/// </summary>
public sealed record AdoConfig(
    string OrganizationUrl,
    string Auth = AdoConfig.AuthAzCli,
    string? Token = null,
    string? Resource = null,
    string? Tenant = null,
    int MaxDepth = AdoConfig.DefaultMaxDepth,
    int MaxItems = AdoConfig.DefaultMaxItems,
    bool FollowMentions = false,
    int PollSeconds = AdoConfig.DefaultPollSeconds)
{
    /// <summary>Authenticate with the Azure CLI's logged-in account (<c>az account get-access-token</c>).</summary>
    public const string AuthAzCli = "az";

    /// <summary>Authenticate with a Personal Access Token read from an environment variable.</summary>
    public const string AuthPat = "pat";

    /// <summary>The env var a PAT reference defaults to — the one the <c>azure-devops</c> CLI extension itself reads.</summary>
    public const string DefaultTokenVar = "AZURE_DEVOPS_EXT_PAT";

    /// <summary>Default link-follow depth. The seed is depth 0.</summary>
    public const int DefaultMaxDepth = 2;

    /// <summary>Default hard ceiling on work items pulled per <c>index</c> command.</summary>
    public const int DefaultMaxItems = 150;

    /// <summary>Absolute ceiling on <see cref="MaxItems"/> — a runaway-graph backstop.</summary>
    public const int MaxItemsCeiling = 2000;

    /// <summary>Default poll interval for <c>rtfm ado watch</c>, in seconds.</summary>
    public const int DefaultPollSeconds = 300;

    /// <summary>True when this descriptor authenticates with a PAT rather than the Azure CLI.</summary>
    public bool UsesPat => string.Equals(Auth, AuthPat, StringComparison.OrdinalIgnoreCase);

    /// <summary>The organization name (e.g. <c>contoso</c>), as parsed from <see cref="OrganizationUrl"/>.</summary>
    public string Organization => AdoOrganization.Parse(OrganizationUrl).Name;

    /// <summary>The PAT, env-expanded. A missing var fails loudly at call time, not parse time.</summary>
    public string ResolveToken() => EnvironmentExpansion.Expand(Token ?? string.Empty, "azure devops config");
}

/// <summary>A normalized Azure DevOps Services organization: its REST base URL and its name.</summary>
public sealed record AdoOrganization(string Url, string Name)
{
    /// <summary>
    /// Accepts what a developer is likely to paste — an organization URL, a
    /// project URL, a work item URL, the legacy <c>{org}.visualstudio.com</c>
    /// form, or a bare organization name — and reduces it to the organization:
    /// <c>https://dev.azure.com/{org}</c> or <c>https://{org}.visualstudio.com</c>.
    /// Azure DevOps <em>Server</em> (on-prem collections) is out of scope: it
    /// authenticates differently and is rejected with a message, not guessed at.
    /// </summary>
    /// <exception cref="ArgumentException">The input does not name an Azure DevOps Services organization.</exception>
    public static AdoOrganization Parse(string input)
    {
        var trimmed = (input ?? string.Empty).Trim().TrimEnd('/');
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Azure DevOps organization URL is required.", nameof(input));
        }

        // A bare name ("contoso") — the common shorthand.
        if (!trimmed.Contains('/') && !trimmed.Contains('.') && !trimmed.Contains(':'))
        {
            return new AdoOrganization($"https://dev.azure.com/{trimmed}", trimmed);
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }

        var uri = new Uri(trimmed, UriKind.Absolute);
        var host = uri.Host.ToLowerInvariant();

        if (host == "dev.azure.com")
        {
            var org = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(org))
            {
                throw new ArgumentException("the URL has no organization — expected https://dev.azure.com/<organization>.", nameof(input));
            }

            org = Uri.UnescapeDataString(org);
            return new AdoOrganization($"https://dev.azure.com/{Uri.EscapeDataString(org)}", org);
        }

        if (host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
        {
            var org = host[..^".visualstudio.com".Length];
            if (org.Length > 0 && !org.Contains('.'))
            {
                return new AdoOrganization($"https://{org}.visualstudio.com", org);
            }
        }

        throw new ArgumentException(
            $"'{uri.Host}' is not an Azure DevOps Services host — expected https://dev.azure.com/<organization> "
            + "(Azure DevOps Server / on-prem collections are not supported).",
            nameof(input));
    }
}
