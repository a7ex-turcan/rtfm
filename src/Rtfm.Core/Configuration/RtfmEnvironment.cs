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

    /// <summary>
    /// Returns the configured OpenSearch endpoint, falling back to the local default.
    /// </summary>
    public static Uri ResolveOpenSearchUrl()
    {
        var value = Environment.GetEnvironmentVariable(OpenSearchUrlVariable);
        var url = string.IsNullOrWhiteSpace(value) ? DefaultOpenSearchUrl : value.Trim();
        return new Uri(url, UriKind.Absolute);
    }
}
