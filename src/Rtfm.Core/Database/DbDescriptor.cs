using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rtfm.Core.Database;

/// <summary>
/// One <c>.rtfmdb</c> connector descriptor (Phase 20 schema pull + Phase 23
/// live query). Names which database to reach and — optionally — how it may be
/// queried. Connection strings support <c>${ENV_VAR}</c> placeholders so
/// credentials never sit in a scanned, committed file.
/// </summary>
/// <remarks>
/// Environment expansion is <b>lazy</b> (<see cref="ResolveConnectionString"/> /
/// <see cref="ResolveQuery"/>), not done at parse time: schema pull and query
/// can use different credentials in different processes (the CLI indexes with a
/// catalog-read login; the MCP server queries with a separate read-only one), so
/// parsing a descriptor must not require an env var the current process doesn't
/// hold. It is only required at the moment the matching connection is opened.
/// </remarks>
internal sealed record DbDescriptor(
    string Provider,
    string ConnectionString,
    string? Name,
    string[]? Schemas,
    DbQueryBlock? Query)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// True when the descriptor opts in to data queries (Phase 23). A descriptor
    /// without a <c>query</c> block is schema-indexing-only — <c>query_database</c>
    /// refuses it. This keeps pre-Phase-23 descriptors from silently becoming
    /// live query endpoints.
    /// </summary>
    public bool IsQueryable => Query is not null;

    /// <summary>True when the descriptor's query block opts into writes; reads are the default.</summary>
    public bool AllowsWrites => Query?.AllowWrites ?? false;

    internal static DbDescriptor Parse(string json)
    {
        var descriptor = JsonSerializer.Deserialize<DbDescriptor>(json, Options)
            ?? throw new InvalidDataException("empty .rtfmdb descriptor");

        if (string.IsNullOrWhiteSpace(descriptor.Provider) || string.IsNullOrWhiteSpace(descriptor.ConnectionString))
        {
            throw new InvalidDataException(".rtfmdb needs \"provider\" and \"connectionString\"");
        }

        return descriptor;
    }

    /// <summary>The schema-pull connection string (Phase 20), env-expanded. Missing vars fail loudly.</summary>
    public string ResolveConnectionString() => ExpandEnvironment(ConnectionString);

    /// <summary>
    /// The resolved query configuration (Phase 23): the <c>query</c> block's own
    /// connection string when present, else a fall-back to the schema-pull string,
    /// both env-expanded. Row cap and timeout default when unspecified.
    /// </summary>
    /// <exception cref="InvalidOperationException">The descriptor is not queryable (no <c>query</c> block).</exception>
    public ResolvedQuery ResolveQuery()
    {
        if (Query is null)
        {
            throw new InvalidOperationException("descriptor has no query block");
        }

        var raw = string.IsNullOrWhiteSpace(Query.ConnectionString) ? ConnectionString : Query.ConnectionString!;
        return new ResolvedQuery(
            Provider,
            ExpandEnvironment(raw),
            Query.MaxRows is > 0 ? Query.MaxRows.Value : DbQueryBlock.DefaultMaxRows,
            Query.TimeoutSeconds is > 0 ? Query.TimeoutSeconds.Value : DbQueryBlock.DefaultTimeoutSeconds,
            Query.AllowWrites ?? false);
    }

    /// <summary>Replaces <c>${VAR}</c> with the environment value; missing vars fail loudly (a half-expanded secret is worse).</summary>
    internal static string ExpandEnvironment(string value)
        => Regex.Replace(value, @"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}", m =>
            Environment.GetEnvironmentVariable(m.Groups["name"].Value)
            ?? throw new InvalidDataException($"environment variable '{m.Groups["name"].Value}' referenced by the .rtfmdb descriptor is not set"));
}

/// <summary>
/// The optional <c>query</c> block on a <c>.rtfmdb</c> descriptor (Phase 23).
/// Its mere presence is the opt-in that makes the database queryable. A separate
/// <see cref="ConnectionString"/> lets the query path use a read-only login
/// distinct from the schema-pull credential; omit it to reuse the schema-pull
/// string (still subject to the read guard in <see cref="DatabaseQueryService"/>).
/// <see cref="AllowWrites"/> is the second opt-in: reads are the default, and a
/// database only becomes writable when its descriptor says so.
/// </summary>
internal sealed record DbQueryBlock(
    string? ConnectionString = null,
    int? MaxRows = null,
    int? TimeoutSeconds = null,
    bool? AllowWrites = null)
{
    /// <summary>Default row cap when the block does not set <c>maxRows</c>.</summary>
    public const int DefaultMaxRows = 500;

    /// <summary>Default command timeout when the block does not set <c>timeoutSeconds</c>.</summary>
    public const int DefaultTimeoutSeconds = 10;
}

/// <summary>Fully-resolved query configuration: provider, env-expanded connection, caps, write mode.</summary>
internal sealed record ResolvedQuery(string Provider, string ConnectionString, int MaxRows, int TimeoutSeconds, bool AllowWrites);
