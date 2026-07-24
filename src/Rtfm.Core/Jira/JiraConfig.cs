using Rtfm.Core.Configuration;

namespace Rtfm.Core.Jira;

/// <summary>
/// The per-project Jira workspace descriptor (§2.16), written by
/// <c>rtfm jira config</c> and read by <c>index</c>/<c>watch</c>. Holds the
/// workspace URL and account email (not secret) plus a <b><c>${ENV}</c>
/// reference</b> to the API token — the token itself lives only in the
/// environment and is expanded lazily by <see cref="ResolveToken"/>, mirroring
/// the <c>.rtfmdb</c> secret discipline (§2.15). The traversal knobs
/// (<see cref="MaxDepth"/>, <see cref="MaxTickets"/>, <see cref="FollowMentions"/>)
/// leash the Phase 25 step-2 graph walk; <see cref="PollSeconds"/> paces the
/// step-3 watch loop.
/// </summary>
public sealed record JiraConfig(
    string BaseUrl,
    string Email,
    string Token,
    int MaxDepth = JiraConfig.DefaultMaxDepth,
    int MaxTickets = JiraConfig.DefaultMaxTickets,
    bool FollowMentions = false,
    int PollSeconds = JiraConfig.DefaultPollSeconds)
{
    /// <summary>Default link-follow depth (structured links). The seed is depth 0.</summary>
    public const int DefaultMaxDepth = 2;

    /// <summary>Default hard ceiling on tickets pulled per <c>index</c> command (§2.16 leash).</summary>
    public const int DefaultMaxTickets = 150;

    /// <summary>Absolute ceiling on <see cref="MaxTickets"/> — a runaway-graph backstop.</summary>
    public const int MaxTicketsCeiling = 2000;

    /// <summary>Default poll interval for <c>rtfm jira watch</c>, in seconds.</summary>
    public const int DefaultPollSeconds = 300;

    /// <summary>The API token, env-expanded (§2.16). A missing var fails loudly at call time, not parse time.</summary>
    public string ResolveToken() => EnvironmentExpansion.Expand(Token, "jira config");

    /// <summary>
    /// Normalizes a user-supplied workspace URL to <c>https://host</c> (scheme
    /// added if absent, path/trailing slash dropped) so the REST base is stable.
    /// </summary>
    public static string NormalizeBaseUrl(string url)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Jira workspace URL is required.", nameof(url));
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }

        var uri = new Uri(trimmed, UriKind.Absolute);
        return $"{uri.Scheme}://{uri.Authority}";
    }
}
