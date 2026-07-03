using System.Text;

namespace Rtfm.Core.Conversion;

/// <summary>
/// The shared relational-schema model + markdown renderer (Phases 18/20). Two
/// producers feed it: <see cref="SqlConverter"/> (parsing DDL text) and
/// <see cref="DatabaseSchemaConverter"/> (pulling live metadata via a
/// <c>.rtfmdb</c> descriptor). One renderer means both routes get identical
/// per-table chunks, FK annotations, and the Referenced-by reverse index.
/// </summary>
internal sealed record SqlColumn(string Name, string Type, List<string> Flags)
{
    public string? Comment { get; set; }
}

internal sealed record SqlTable(string Name, List<SqlColumn> Columns)
{
    public string? Comment { get; set; }
}

internal sealed record SqlForeignKey(string FromTable, string FromColumns, string ToTable, string ToColumns);

internal sealed class SqlSchema
{
    public List<SqlTable> Tables { get; } = [];
    public List<SqlForeignKey> ForeignKeys { get; } = [];
    public List<string> Others { get; } = [];
    public int DataStatements { get; set; }
    public int UnrecognizedStatements { get; set; }
}

internal static class SqlSchemaRenderer
{
    /// <summary>Match ignoring schema qualification: <c>public.accounts</c> == <c>accounts</c>.</summary>
    internal static bool TableNamesEqual(string a, string b)
        => string.Equals(LastSegment(a), LastSegment(b), StringComparison.OrdinalIgnoreCase);

    private static string LastSegment(string name)
        => name[(name.LastIndexOf('.') + 1)..];

    internal static void Render(StringBuilder sb, SqlSchema schema)
    {
        foreach (var table in schema.Tables)
        {
            sb.Append("## Table: ").Append(table.Name).Append("\n\n");
            if (table.Comment is not null)
            {
                sb.Append(table.Comment).Append("\n\n");
            }

            foreach (var column in table.Columns)
            {
                sb.Append("- ").Append(column.Name).Append(' ').Append(column.Type);

                var annotations = new List<string>(column.Flags);
                foreach (var fk in schema.ForeignKeys.Where(f =>
                             TableNamesEqual(f.FromTable, table.Name)
                             && f.FromColumns.Split(',').Select(c => c.Trim()).Contains(column.Name, StringComparer.OrdinalIgnoreCase)))
                {
                    annotations.Add($"FK → {fk.ToTable}{(fk.ToColumns.Length > 0 ? $"({fk.ToColumns})" : string.Empty)}");
                }

                if (annotations.Count > 0)
                {
                    sb.Append(" — ").Append(string.Join(", ", annotations));
                }

                if (column.Comment is not null)
                {
                    sb.Append(" — ").Append(column.Comment);
                }

                sb.Append('\n');
            }

            var referencedBy = schema.ForeignKeys
                .Where(f => TableNamesEqual(f.ToTable, table.Name) && !TableNamesEqual(f.FromTable, table.Name))
                .Select(f => $"{f.FromTable} ({f.FromColumns})")
                .Distinct()
                .ToList();

            if (referencedBy.Count > 0)
            {
                sb.Append("\nReferenced by: ").Append(string.Join(", ", referencedBy)).Append('\n');
            }

            sb.Append('\n');
        }

        if (schema.ForeignKeys.Count > 0)
        {
            sb.Append("## Relationships\n\n");
            foreach (var fk in schema.ForeignKeys)
            {
                sb.Append("- ").Append(fk.FromTable).Append(" (").Append(fk.FromColumns).Append(") → ").Append(fk.ToTable);
                if (fk.ToColumns.Length > 0)
                {
                    sb.Append(" (").Append(fk.ToColumns).Append(')');
                }

                sb.Append('\n');
            }

            sb.Append('\n');
        }

        if (schema.Others.Count > 0)
        {
            sb.Append("## Other objects\n\n");
            foreach (var other in schema.Others)
            {
                sb.Append("- ").Append(other).Append('\n');
            }

            sb.Append('\n');
        }

        if (schema.DataStatements > 0 || schema.UnrecognizedStatements > 0)
        {
            sb.Append("Also contains: ");
            var parts = new List<string>();
            if (schema.DataStatements > 0)
            {
                parts.Add($"{schema.DataStatements} data statement{(schema.DataStatements == 1 ? "" : "s")}");
            }

            if (schema.UnrecognizedStatements > 0)
            {
                parts.Add($"{schema.UnrecognizedStatements} other statement{(schema.UnrecognizedStatements == 1 ? "" : "s")}");
            }

            sb.Append(string.Join(", ", parts)).Append(".\n");
        }
    }
}
