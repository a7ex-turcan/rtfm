using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Rtfm.Core.Database;

/// <summary>
/// Runs SQL against a live database reached through a queryable <c>.rtfmdb</c>
/// descriptor (Phase 23). This is RTFM's only <b>live data</b> path — every other
/// surface reads the derived OpenSearch index; here the MCP server talks straight
/// to the database (§2.15).
/// <para>
/// Reads are the default; a descriptor opts into writes with
/// <c>"allowWrites": true</c>. Read mode is enforced <i>at the database</i>, and
/// the mechanism differs by provider because their capabilities do:
/// </para>
/// <list type="bullet">
/// <item><b>Postgres</b> — <c>SET TRANSACTION READ ONLY</c>. A write raises
/// <c>25006</c> in the engine, so the agent gets a clear, immediate error.</item>
/// <item><b>SQL Server</b> — has no read-only transaction mode, but both its DML
/// <i>and its DDL</i> are transactional, so the statement runs inside a
/// transaction that is <b>always rolled back</b>. Nothing persists. This is
/// deliberately not a login-permission check: a local dev box connects as
/// <c>sa</c>/<c>db_owner</c>, and refusing those would make the provider unusable
/// for the tool's actual audience.</item>
/// </list>
/// <para>
/// In read mode the rollback is only half the guard: the outcome must also be
/// <i>reported</i> as an error, never as a silent success — an agent told "ok, 0
/// rows" after its write was undone would believe it wrote. On SQL Server that
/// judgement is <see cref="EvaluateSqlServerReadOutcome"/>, which keys off whether
/// a result set came back rather than off rows-affected (DDL reports -1 there just
/// like a SELECT, which is how a rolled-back CREATE TABLE once reported as OK).
/// Postgres needs none of this: the engine refuses the write outright.
/// </para>
/// String-level filtering of the SQL is deliberately <i>not</i> attempted — CTEs,
/// <c>SELECT INTO</c>, side-effecting functions, and stacked statements all walk
/// past it, so it would be false assurance. Caveat worth knowing: a rollback
/// cannot undo effects that escape the transaction (a procedure doing its own
/// COMMIT, identity/sequence consumption, <c>xp_cmdshell</c>). This is a guard
/// against an agent's stray write, not a security boundary — RTFM is a per-dev
/// local tool and the descriptor points wherever you point it.
/// </summary>
public sealed class DatabaseQueryService
{
    /// <summary>Absolute ceiling on returned rows regardless of descriptor/override (context-size guard).</summary>
    public const int HardRowCap = 5000;

    /// <summary>
    /// Executes <paramref name="sql"/> against the database named by
    /// <paramref name="database"/> (as resolved by <see cref="DatabaseRegistry"/>),
    /// under the read-only boundary above. Reads and parses the descriptor file
    /// itself so hosts never touch connection strings. Never throws for an expected
    /// outcome (not queryable, missing secret, writable login, unreadable descriptor,
    /// SQL error) — those come back on <see cref="DbQueryResult.Error"/> so callers
    /// render a message instead of a crash.
    /// </summary>
    public async Task<DbQueryResult> ExecuteAsync(DatabaseInfo database, string sql, int? maxRowsOverride = null, CancellationToken cancellationToken = default)
    {
        DbDescriptor descriptor;
        try
        {
            descriptor = DbDescriptor.Parse(await File.ReadAllTextAsync(database.DescriptorPath, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            return DbQueryResult.Refused($"Could not read the descriptor for '{database.Handle}': {ex.Message}");
        }

        return await ExecuteAsync(descriptor, sql, maxRowsOverride, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<DbQueryResult> ExecuteAsync(DbDescriptor descriptor, string sql, int? maxRowsOverride = null, CancellationToken cancellationToken = default)
    {
        if (!descriptor.IsQueryable)
        {
            return DbQueryResult.Refused(
                "This database is registered for schema indexing only. Add a \"query\" block to its .rtfmdb "
                + "descriptor (a read-only connection string) to enable data queries.");
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            return DbQueryResult.Refused("No SQL was provided.");
        }

        ResolvedQuery query;
        try
        {
            query = descriptor.ResolveQuery();
        }
        catch (InvalidDataException ex)
        {
            // A ${VAR} in the query connection string is not set in this process.
            return DbQueryResult.Refused(ex.Message);
        }

        var maxRows = Math.Clamp(maxRowsOverride is > 0 ? maxRowsOverride.Value : query.MaxRows, 1, HardRowCap);

        try
        {
            return query.Provider.ToLowerInvariant() switch
            {
                "postgres" or "postgresql" or "pgsql" => await ExecutePostgresAsync(query, sql, maxRows, cancellationToken).ConfigureAwait(false),
                "sqlserver" or "mssql" => await ExecuteSqlServerAsync(query, sql, maxRows, cancellationToken).ConfigureAwait(false),
                _ => DbQueryResult.Refused($"provider '{query.Provider}' is not supported (use \"postgres\" or \"sqlserver\")"),
            };
        }
        catch (DbException ex)
        {
            // Includes the Postgres read-only violation (25006) and any SQL error —
            // both are the agent's to fix (narrow the query / it's actually a write).
            return DbQueryResult.Failed(ex.Message);
        }
    }

    private static async Task<DbQueryResult> ExecutePostgresAsync(ResolvedQuery query, string sql, int maxRows, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(query.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (query.AllowWrites)
        {
            // Autocommit: no transaction to roll back, so writes persist.
            await using var writable = connection.CreateCommand();
            writable.CommandText = sql;
            writable.CommandTimeout = query.TimeoutSeconds;
            var (written, writtenAffected) = await ReadAndCloseAsync(
                await writable.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false), maxRows, cancellationToken).ConfigureAwait(false);
            return written with { RowsAffected = writtenAffected };
        }

        // Engine-level read guard: a write inside this transaction errors (25006).
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var readOnly = connection.CreateCommand())
        {
            readOnly.Transaction = transaction;
            readOnly.CommandText = "SET TRANSACTION READ ONLY";
            await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = query.TimeoutSeconds;

        // The reader must be closed before the rollback: Npgsql allows only one
        // active command per connection, and a truncated read leaves the reader
        // mid-stream — rolling back with it open fails with "A command is already
        // in progress" and would turn every successful SELECT into an error.
        var (result, _) = await ReadAndCloseAsync(
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false), maxRows, cancellationToken).ConfigureAwait(false);

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task<DbQueryResult> ExecuteSqlServerAsync(ResolvedQuery query, string sql, int maxRows, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(query.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (query.AllowWrites)
        {
            await using var writable = connection.CreateCommand();
            writable.CommandText = sql;
            writable.CommandTimeout = query.TimeoutSeconds;
            var (written, writtenAffected) = await ReadAndCloseAsync(
                await writable.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false), maxRows, cancellationToken).ConfigureAwait(false);
            return written with { RowsAffected = writtenAffected };
        }

        // No read-only transaction mode exists here — but DML *and* DDL are both
        // transactional, so running inside a transaction we always roll back gives
        // the same practical guarantee without refusing a db_owner/sa login.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqlTransaction)transaction;
        command.CommandText = sql;
        command.CommandTimeout = query.TimeoutSeconds;

        var (result, affected) = await ReadAndCloseAsync(
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false), maxRows, cancellationToken).ConfigureAwait(false);

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

        return EvaluateSqlServerReadOutcome(result, affected);
    }

    /// <summary>
    /// Decides what a read-mode SQL Server caller is told once the rollback has run.
    /// The statement already executed and was undone; the only question left is
    /// whether it was a read (report the rows) or a write (report the undo).
    /// <para>
    /// A statement counts as a confirmed read only if it came back <b>with a result
    /// set</b>. <see cref="DbDataReader.RecordsAffected"/> alone is not enough:
    /// DDL (CREATE/DROP/ALTER) reports <c>-1</c> there exactly like a SELECT does,
    /// so keying off rows-affected let a rolled-back <c>CREATE TABLE</c> report as
    /// "OK — no rows returned" while nothing had persisted.
    /// </para>
    /// <para>
    /// So anything with no result set is reported as rolled back rather than assumed
    /// harmless. That over-reports the rare read-ish statement that returns nothing
    /// (a bare <c>PRINT</c>/<c>SET</c>), which costs an agent one retry — while the
    /// opposite error leaves it believing a write landed. Note this never inspects
    /// the SQL string (§2.15): it reads only what the engine reported about the
    /// statement it actually ran. <b>Known limit:</b> a batch whose first statement
    /// selects and whose later statement writes (<c>SELECT 1; CREATE TABLE …</c>)
    /// still reports the read — closing that needs either SQL parsing (defeated by
    /// CTEs and stacked statements) or a DMV requiring VIEW SERVER STATE (refuses
    /// the non-sa logins this tool exists to serve).
    /// </para>
    /// </summary>
    internal static DbQueryResult EvaluateSqlServerReadOutcome(DbQueryResult result, int affected)
    {
        if (affected > 0)
        {
            return DbQueryResult.Failed(
                $"This database is read-only: the statement modified {affected} row(s) and was rolled back — nothing persisted. "
                + "Add \"allowWrites\": true to the descriptor's query block to permit writes.");
        }

        if (result.Columns.Count == 0)
        {
            return DbQueryResult.Failed(
                "This database is read-only: the statement returned no result set, so it could not be confirmed as a read, "
                + "and it was rolled back — nothing persisted. Statements such as CREATE, DROP and ALTER report no rows even "
                + "when they change the database. Add \"allowWrites\": true to the descriptor's query block to permit writes.");
        }

        return result;
    }

    /// <summary>
    /// Reads the result set, then closes the reader — both because the caller may
    /// need the connection back (rollback), and because
    /// <see cref="DbDataReader.RecordsAffected"/> is only final once closed.
    /// </summary>
    private static async Task<(DbQueryResult Result, int Affected)> ReadAndCloseAsync(DbDataReader reader, int maxRows, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ReadAsync(reader, maxRows, cancellationToken).ConfigureAwait(false);
            await reader.CloseAsync().ConfigureAwait(false);
            return (result, reader.RecordsAffected);
        }
        finally
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Reads up to <paramref name="maxRows"/> rows; one extra read past the cap flags truncation.</summary>
    private static async Task<DbQueryResult> ReadAsync(DbDataReader reader, int maxRows, CancellationToken cancellationToken)
    {
        var columns = new string[reader.FieldCount];
        for (var i = 0; i < columns.Length; i++)
        {
            columns[i] = reader.GetName(i);
        }

        var rows = new List<IReadOnlyList<string?>>();
        var truncated = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count >= maxRows)
            {
                // We successfully read one row beyond the cap — there was more.
                truncated = true;
                break;
            }

            var row = new string?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
            }

            rows.Add(row);
        }

        return DbQueryResult.Ok(columns, rows, truncated);
    }
}

/// <summary>
/// The outcome of a <see cref="DatabaseQueryService"/> call: either a result set
/// (columns + rows) or a message on <see cref="Error"/>. Rows are stringified for
/// transport; <see cref="ToMarkdownTable"/> renders them compactly for an agent.
/// </summary>
public sealed record DbQueryResult(
    bool Success,
    string? Error,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    bool Truncated)
{
    /// <summary>
    /// Rows changed by a write (only meaningful when the descriptor allows writes;
    /// a SELECT reports -1 or 0). Lets a write-mode caller say "3 rows affected"
    /// for a statement that returns no result set.
    /// </summary>
    public int RowsAffected { get; init; }

    /// <summary>Number of rows actually returned (never more than the effective cap).</summary>
    public int RowCount => Rows.Count;

    internal static DbQueryResult Ok(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string?>> rows, bool truncated)
        => new(true, null, columns, rows, truncated);

    /// <summary>A policy/config outcome the caller should not retry as-is (not queryable, writable login, missing secret).</summary>
    internal static DbQueryResult Refused(string reason) => new(false, reason, [], [], false);

    /// <summary>A query-execution failure (bad SQL, read-only violation) — fixable by changing the SQL.</summary>
    internal static DbQueryResult Failed(string reason) => new(false, reason, [], [], false);

    /// <summary>Renders the result set as a GitHub-flavored markdown pipe table (cells pipe-escaped, newlines flattened).</summary>
    public string ToMarkdownTable()
    {
        if (Columns.Count == 0)
        {
            return "(no columns)";
        }

        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", Columns.Select(EscapeCell))).Append(" |\n");
        sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", Columns.Count))).Append('\n');
        foreach (var row in Rows)
        {
            sb.Append("| ").Append(string.Join(" | ", row.Select(c => EscapeCell(c ?? string.Empty)))).Append(" |\n");
        }

        return sb.ToString().TrimEnd();
    }

    private static string EscapeCell(string value)
        => value.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
