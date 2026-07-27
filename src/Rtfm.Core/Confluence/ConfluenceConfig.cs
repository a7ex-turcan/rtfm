using Rtfm.Core.Configuration;

namespace Rtfm.Core.Confluence;

/// <summary>
/// The per-project Confluence workspace descriptor (§2.17), the Confluence twin
/// of <see cref="Jira.JiraConfig"/>. Holds the workspace URL and account email
/// (not secret) plus a <c>${ENV}</c> reference to the API token, expanded lazily
/// by <see cref="ResolveToken"/>. The traversal knobs leash the step-2 crawl;
/// <see cref="PollSeconds"/> paces the step-3 watch loop.
/// </summary>
public sealed record ConfluenceConfig(
    string BaseUrl,
    string Email,
    string Token,
    int MaxDepth = ConfluenceConfig.DefaultMaxDepth,
    int MaxPages = ConfluenceConfig.DefaultMaxPages,
    int PollSeconds = ConfluenceConfig.DefaultPollSeconds)
{
    /// <summary>Default link-follow depth (children + in-body links). The seed is depth 0.</summary>
    public const int DefaultMaxDepth = 2;

    /// <summary>Default hard ceiling on pages pulled per <c>index</c> command (§2.17 leash).</summary>
    public const int DefaultMaxPages = 200;

    /// <summary>Absolute ceiling on <see cref="MaxPages"/> — a runaway-crawl backstop.</summary>
    public const int MaxPagesCeiling = 5000;

    /// <summary>Default poll interval for <c>rtfm confluence watch</c>, in seconds.</summary>
    public const int DefaultPollSeconds = 300;

    /// <summary>The API token, env-expanded (§2.17). A missing var fails loudly at call time, not parse time.</summary>
    public string ResolveToken() => EnvironmentExpansion.Expand(Token, "confluence config");

    /// <summary>Normalizes a workspace URL to <c>https://host</c> (scheme added if absent, path dropped).</summary>
    public static string NormalizeBaseUrl(string url)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Confluence workspace URL is required.", nameof(url));
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }

        var uri = new Uri(trimmed, UriKind.Absolute);
        return $"{uri.Scheme}://{uri.Authority}";
    }
}
