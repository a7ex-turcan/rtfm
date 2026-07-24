using System.Text.RegularExpressions;

namespace Rtfm.Core.Configuration;

/// <summary>
/// Expands <c>${ENV_VAR}</c> placeholders from the process environment so
/// secrets (DB connection strings §2.15, the Jira API token §2.16) live in the
/// environment, never in a stored/committed file. Expansion is done lazily at
/// the moment a secret is needed — the CLI (indexing) and the MCP server
/// (querying) are different processes holding different secrets, so parsing a
/// descriptor must not require a var the current process doesn't hold.
/// A missing variable fails loudly: a half-expanded secret is worse than an
/// error.
/// </summary>
public static partial class EnvironmentExpansion
{
    /// <summary>
    /// Replaces every <c>${VAR}</c> in <paramref name="value"/> with its
    /// environment value. <paramref name="sourceLabel"/> names the descriptor in
    /// the error message when a referenced variable is unset.
    /// </summary>
    public static string Expand(string value, string sourceLabel)
        => PlaceholderPattern().Replace(value, match =>
        {
            var name = match.Groups["name"].Value;
            return Environment.GetEnvironmentVariable(name)
                ?? throw new InvalidDataException(
                    $"environment variable '{name}' referenced by the {sourceLabel} is not set");
        });

    [GeneratedRegex(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex PlaceholderPattern();
}
