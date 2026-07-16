using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Npgsql;
using Rtfm.Core.Database;

namespace Rtfm.Core.Conversion;

/// <summary>
/// Pulls a live database schema through a <c>.rtfmdb</c> descriptor (Phase 20)
/// and renders it with the same <see cref="SqlSchemaRenderer"/> as parsed
/// <c>.sql</c> dumps — same per-table chunks, FK annotations, Referenced-by
/// reverse index. Unlike a dump, the result can never be stale: the schema is
/// re-pulled every time the descriptor is ingested (every <c>rtfm index</c>
/// run; watch re-pulls when the descriptor file changes), and
/// <c>source_modified_at</c> is the pull time. Metadata comes from
/// <c>INFORMATION_SCHEMA</c> (portable across both providers); table/column
/// descriptions are fetched provider-specifically and skipped without error
/// when permissions deny them.
/// </summary>
public sealed class DatabaseSchemaConverter
{
    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "pg_catalog", "information_schema", "sys", "guest", "db_owner", "db_accessadmin",
        "db_securityadmin", "db_ddladmin", "db_backupoperator", "db_datareader", "db_datawriter",
        "db_denydatareader", "db_denydatawriter",
    };

    public ConversionResult Convert(Stream input, string sourcePath)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var descriptor = DbDescriptor.Parse(reader.ReadToEnd());

        using var connection = CreateConnection(descriptor);
        connection.Open();

        var schema = LoadSchema(connection, descriptor);
        var pulledAt = DateTimeOffset.UtcNow;
        var title = string.IsNullOrWhiteSpace(descriptor.Name) ? $"{connection.Database} database schema" : descriptor.Name.Trim();

        var sb = new StringBuilder();
        sb.Append("# ").Append(title).Append("\n\n");
        sb.Append("> Live schema pulled from the ").Append(descriptor.Provider.ToLowerInvariant())
            .Append(" database \"").Append(connection.Database).Append("\" on ")
            .Append(pulledAt.ToString("yyyy-MM-dd HH:mm")).Append(" UTC — re-pulled on every index run.\n\n");
        SqlSchemaRenderer.Render(sb, schema);

        return new ConversionResult(
            SourcePath: sourcePath,
            Format: SourceFormat.Database,
            Markdown: sb.ToString().TrimEnd(),
            Title: title,
            SourceModifiedAt: pulledAt);
    }

    private static DbConnection CreateConnection(DbDescriptor descriptor)
    {
        var connectionString = descriptor.ResolveConnectionString();
        return descriptor.Provider.ToLowerInvariant() switch
        {
            "sqlserver" or "mssql" => new SqlConnection(connectionString),
            "postgres" or "postgresql" or "pgsql" => new NpgsqlConnection(connectionString),
            _ => throw new NotSupportedException($".rtfmdb provider '{descriptor.Provider}' is not supported (use \"sqlserver\" or \"postgres\")"),
        };
    }

    private SqlSchema LoadSchema(DbConnection connection, DbDescriptor descriptor)
    {
        var schema = new SqlSchema();
        var tables = new Dictionary<string, SqlTable>(StringComparer.OrdinalIgnoreCase);

        bool Wanted(string schemaName) =>
            !SystemSchemas.Contains(schemaName)
            && (descriptor.Schemas is null || descriptor.Schemas.Length == 0
                || descriptor.Schemas.Contains(schemaName, StringComparer.OrdinalIgnoreCase));

        // Tables + columns.
        foreach (var row in Query(connection,
            """
            SELECT c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.CHARACTER_MAXIMUM_LENGTH, c.IS_NULLABLE, c.COLUMN_DEFAULT
            FROM INFORMATION_SCHEMA.COLUMNS c
            JOIN INFORMATION_SCHEMA.TABLES t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
            WHERE t.TABLE_TYPE = 'BASE TABLE'
            ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION
            """))
        {
            if (!Wanted(row[0]))
            {
                continue;
            }

            var key = $"{row[0]}.{row[1]}";
            if (!tables.TryGetValue(key, out var table))
            {
                table = new SqlTable(key, []);
                tables[key] = table;
                schema.Tables.Add(table);
            }

            var type = row[3];
            if (row[4].Length > 0)
            {
                type += row[4] == "-1" ? "(max)" : $"({row[4]})";
            }

            var flags = new List<string>();
            if (row[5].Equals("NO", StringComparison.OrdinalIgnoreCase))
            {
                flags.Add("NOT NULL");
            }

            if (row[6].Length > 0)
            {
                flags.Add($"DEFAULT {row[6]}");
            }

            table.Columns.Add(new SqlColumn(row[2], type, flags));
        }

        // Primary keys → PK flag, first.
        foreach (var row in Query(connection,
            """
            SELECT kcu.TABLE_SCHEMA, kcu.TABLE_NAME, kcu.COLUMN_NAME
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
              ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME AND kcu.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA
            WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
            """))
        {
            if (tables.TryGetValue($"{row[0]}.{row[1]}", out var table)
                && table.Columns.FirstOrDefault(c => c.Name.Equals(row[2], StringComparison.OrdinalIgnoreCase)) is { } column
                && !column.Flags.Contains("PK"))
            {
                column.Flags.Insert(0, "PK");
            }
        }

        // Foreign keys, grouped per constraint so composite keys stay together.
        var fkParts = new Dictionary<string, (string From, List<string> FromCols, string To, List<string> ToCols)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Query(connection,
            """
            SELECT rc.CONSTRAINT_SCHEMA, rc.CONSTRAINT_NAME,
                   fk.TABLE_SCHEMA, fk.TABLE_NAME, fk.COLUMN_NAME,
                   pk.TABLE_SCHEMA, pk.TABLE_NAME, pk.COLUMN_NAME
            FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE fk
              ON fk.CONSTRAINT_NAME = rc.CONSTRAINT_NAME AND fk.CONSTRAINT_SCHEMA = rc.CONSTRAINT_SCHEMA
            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE pk
              ON pk.CONSTRAINT_NAME = rc.UNIQUE_CONSTRAINT_NAME AND pk.CONSTRAINT_SCHEMA = rc.UNIQUE_CONSTRAINT_SCHEMA
             AND pk.ORDINAL_POSITION = fk.ORDINAL_POSITION
            ORDER BY rc.CONSTRAINT_SCHEMA, rc.CONSTRAINT_NAME, fk.ORDINAL_POSITION
            """))
        {
            if (!Wanted(row[2]))
            {
                continue;
            }

            var key = $"{row[0]}.{row[1]}";
            if (!fkParts.TryGetValue(key, out var fk))
            {
                fk = ($"{row[2]}.{row[3]}", [], $"{row[5]}.{row[6]}", []);
                fkParts[key] = fk;
            }

            fk.FromCols.Add(row[4]);
            fk.ToCols.Add(row[7]);
        }

        foreach (var (_, fk) in fkParts)
        {
            schema.ForeignKeys.Add(new SqlForeignKey(fk.From, string.Join(", ", fk.FromCols), fk.To, string.Join(", ", fk.ToCols)));
        }

        // Views.
        foreach (var row in Query(connection, "SELECT TABLE_SCHEMA, TABLE_NAME, VIEW_DEFINITION FROM INFORMATION_SCHEMA.VIEWS"))
        {
            if (!Wanted(row[0]))
            {
                continue;
            }

            var definition = Regex.Replace(row[2], @"\s+", " ").Trim();
            schema.Others.Add($"VIEW {row[0]}.{row[1]}"
                + (definition.Length > 0 ? $" AS {(definition.Length > 240 ? definition[..240] + "…" : definition)}" : string.Empty));
        }

        LoadComments(connection, descriptor, tables);
        return schema;
    }

    /// <summary>Table/column descriptions — provider-specific catalogs, best-effort (permissions vary).</summary>
    private static void LoadComments(DbConnection connection, DbDescriptor descriptor, Dictionary<string, SqlTable> tables)
    {
        var sql = descriptor.Provider.ToLowerInvariant() switch
        {
            "postgres" or "postgresql" or "pgsql" =>
                """
                SELECT n.nspname, c.relname, COALESCE(a.attname, ''), d.description
                FROM pg_description d
                JOIN pg_class c ON c.oid = d.objoid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                LEFT JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = d.objsubid AND d.objsubid > 0
                WHERE c.relkind IN ('r', 'p')
                """,
            _ =>
                """
                SELECT s.name, t.name, COALESCE(c.name, ''), CAST(ep.value AS nvarchar(max))
                FROM sys.extended_properties ep
                JOIN sys.tables t ON ep.major_id = t.object_id
                JOIN sys.schemas s ON t.schema_id = s.schema_id
                LEFT JOIN sys.columns c ON c.object_id = t.object_id AND c.column_id = ep.minor_id AND ep.minor_id > 0
                WHERE ep.name = 'MS_Description' AND ep.class = 1
                """,
        };

        try
        {
            foreach (var row in Query(connection, sql))
            {
                if (!tables.TryGetValue($"{row[0]}.{row[1]}", out var table))
                {
                    continue;
                }

                if (row[2].Length == 0)
                {
                    table.Comment ??= row[3];
                }
                else if (table.Columns.FirstOrDefault(c => c.Name.Equals(row[2], StringComparison.OrdinalIgnoreCase)) is { } column)
                {
                    column.Comment ??= row[3];
                }
            }
        }
        catch (DbException)
        {
            // Descriptions are a bonus, never a blocker.
        }
    }

    private static IEnumerable<string[]> Query(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new string[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? string.Empty : System.Convert.ToString(reader.GetValue(i)) ?? string.Empty;
            }

            yield return row;
        }
    }
}
