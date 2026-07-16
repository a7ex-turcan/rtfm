using System.ComponentModel;
using ModelContextProtocol.Server;
using Rtfm.Core.Configuration;
using Rtfm.Core.Database;

namespace Rtfm.Mcp;

/// <summary>
/// Phase 23 tool surface: the live-data gateway. Unlike every other RTFM tool
/// (which reads the derived OpenSearch index), these reach the actual database
/// behind a queryable <c>.rtfmdb</c> connector (§2.15). The intended workflow is
/// schema-first: use search_docs / get_document to learn the tables and columns
/// (the schema is already indexed), then query_database to read the data. Queries
/// are read-only and capped.
/// </summary>
[McpServerToolType]
public static class DatabaseTools
{
    [McpServerTool(Name = "list_databases")]
    [Description("""
        List the live databases RTFM can query: name, handle (the identifier to pass to query_database),
        project, provider, whether querying is enabled, and whether the database is `writable`.

        These come from .rtfmdb connector descriptors in the indexed docs. A database is only queryable
        if its descriptor opts in with a "query" block — otherwise it is schema-indexed only (its tables
        are searchable via search_docs, but its data is not readable here).

        `writable: false` (the default) means the database accepts SELECTs only — a write is rejected or
        rolled back. Only send a write to a database listed as writable, and only when the user asked for it.

        Scope follows RTFM_PROJECT by default; pass `project` to override, "*" for all projects.
        """)]
    public static Task<ListDatabasesResult> ListDatabases(
        [Description("Project to list databases for. Omit for the configured default; \"*\" for all projects.")] string? project = null,
        CancellationToken cancellationToken = default)
    {
        var scope = RtfmEnvironment.ResolveProjectScope(project);
        var databases = DatabaseRegistry.List(scope);

        var result = new ListDatabasesResult(
            ProjectScope: scope ?? "(all projects)",
            Count: databases.Count,
            Databases: databases.Select(d => new DatabaseEntry(d.Name, d.Handle, d.Project, d.Provider, d.Queryable, d.Writable)).ToList(),
            Note: databases.Count == 0
                ? "No .rtfmdb connectors found in indexed folders. Add a .rtfmdb descriptor and run 'rtfm index'."
                : databases.All(d => !d.Queryable)
                    ? "These databases are schema-indexed only. Add a \"query\" block to a .rtfmdb descriptor to enable query_database."
                    : null);

        return Task.FromResult(result);
    }

    [McpServerTool(Name = "query_database")]
    [Description("""
        Run a SQL query against a live database and get the rows back as a markdown table.

        WORKFLOW: learn the schema first. The database's tables/columns are already indexed — use
        search_docs / get_document (e.g. "which columns does the accounts table have") before writing
        SQL. Then pass the `database` handle (from list_databases) and a SELECT statement.

        Behavior to rely on:
        - READ-ONLY unless the database opts into writes (list_databases shows `writable`). On a
          read-only database a write is rejected or rolled back and reported as an error — it does NOT
          silently succeed. Only send INSERT/UPDATE/DELETE/DDL to a database marked writable, and only
          when the user asked for that change.
        - Results are capped (default 500 rows). If `truncated` is true you did NOT see all rows — narrow
          the query (add WHERE / LIMIT / aggregation) rather than assuming the table ends there.
        - Only databases whose .rtfmdb descriptor has a "query" block are queryable; others return an error
          telling you it's schema-only.
        - Write self-contained SQL for the target dialect (postgres or sqlserver — see list_databases).
        """)]
    public static async Task<QueryDatabaseResult> QueryDatabase(
        DatabaseQueryService queryService,
        [Description("The database handle (or name) from list_databases.")] string database,
        [Description("A read-only SQL SELECT statement in the database's dialect.")] string sql,
        [Description("Row cap for this call (1-5000). Omit to use the descriptor's default (500).")] int? max_rows = null,
        [Description("Project to resolve the database in. Omit for the configured default; \"*\" for all projects.")] string? project = null,
        CancellationToken cancellationToken = default)
    {
        var scope = RtfmEnvironment.ResolveProjectScope(project);
        var info = DatabaseRegistry.Resolve(database, scope);
        if (info is null)
        {
            return QueryDatabaseResult.Miss(database,
                $"No database '{database}' in scope '{scope ?? "(all projects)"}'. Use list_databases to see what exists.");
        }

        var result = await queryService.ExecuteAsync(info, sql, max_rows, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return QueryDatabaseResult.Miss(info.Handle, result.Error ?? "The query failed.");
        }

        // A write returns no result set; report what it changed instead of an empty table.
        var wrote = info.Writable && result.Columns.Count == 0 && result.RowsAffected > 0;

        return new QueryDatabaseResult(
            Success: true,
            Database: info.Handle,
            Provider: info.Provider,
            RowCount: result.RowCount,
            Truncated: result.Truncated,
            Table: wrote ? null : result.ToMarkdownTable(),
            Error: null,
            RowsAffected: result.RowsAffected > 0 ? result.RowsAffected : null,
            Note: result.Truncated
                ? $"Result truncated at {result.RowCount} rows — there are more. Narrow the query to see the rest."
                : wrote ? $"{result.RowsAffected} row(s) affected."
                : null);
    }
}

/// <summary>Structured tool outputs — serialized to JSON for the LLM by the SDK.</summary>
public sealed record ListDatabasesResult(string ProjectScope, int Count, IReadOnlyList<DatabaseEntry> Databases, string? Note);

public sealed record DatabaseEntry(string Name, string Handle, string Project, string Provider, bool Queryable, bool Writable);

public sealed record QueryDatabaseResult(
    bool Success,
    string Database,
    string? Provider,
    int RowCount,
    bool Truncated,
    string? Table,
    string? Error,
    int? RowsAffected = null,
    string? Note = null)
{
    internal static QueryDatabaseResult Miss(string database, string error)
        => new(false, database, null, 0, false, null, error);
}
