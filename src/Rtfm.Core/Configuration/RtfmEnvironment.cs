namespace Rtfm.Core.Configuration;

/// <summary>
/// Resolves RTFM configuration from the environment. Keeping this in one place
/// means the CLI and the MCP server agree on where OpenSearch lives.
/// </summary>
public static class RtfmEnvironment
{
    /// <summary>Environment variable holding the OpenSearch endpoint (see .mcp.json).</summary>
    public const string OpenSearchUrlVariable = "RTFM_OPENSEARCH_URL";

    /// <summary>Default endpoint for the local single-node OpenSearch from docker-compose.</summary>
    public const string DefaultOpenSearchUrl = "http://localhost:9200";

    /// <summary>Environment variable naming the project the MCP server is scoped to (§2.14).</summary>
    public const string ProjectVariable = "RTFM_PROJECT";

    /// <summary>Environment variable overriding where embedding model files are cached (Tier 2, §2.10).</summary>
    public const string ModelDirectoryVariable = "RTFM_MODEL_DIR";

    /// <summary>
    /// Returns the configured OpenSearch endpoint, falling back to the local default.
    /// </summary>
    public static Uri ResolveOpenSearchUrl()
    {
        var value = Environment.GetEnvironmentVariable(OpenSearchUrlVariable);
        var url = string.IsNullOrWhiteSpace(value) ? DefaultOpenSearchUrl : value.Trim();
        return new Uri(url, UriKind.Absolute);
    }

    /// <summary>
    /// Resolves the project filter for a search (§2.14). A non-empty
    /// <paramref name="requested"/> (per-call override) wins over the
    /// <c>RTFM_PROJECT</c> env default. Returns null — meaning "all projects" —
    /// when nothing is set or the value is the "all" sentinel (<c>*</c>/<c>all</c>).
    /// </summary>
    public static string? ResolveProjectScope(string? requested = null)
    {
        var value = !string.IsNullOrWhiteSpace(requested)
            ? requested
            : Environment.GetEnvironmentVariable(ProjectVariable);

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        return value is "*" or "all" ? null : value;
    }

    /// <summary>
    /// Directory the embedding model files are cached in. Defaults to
    /// <c>LocalApplicationData/rtfm/models</c>; <c>RTFM_MODEL_DIR</c> overrides
    /// (e.g. an offline pre-provisioned copy).
    /// </summary>
    public static string ResolveModelDirectory()
    {
        var value = Environment.GetEnvironmentVariable(ModelDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Path.GetTempPath();
        }

        return Path.Combine(baseDir, "rtfm", "models");
    }
}
