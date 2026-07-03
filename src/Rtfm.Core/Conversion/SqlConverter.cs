using System.Text;
using System.Text.RegularExpressions;

namespace Rtfm.Core.Conversion;

/// <summary>
/// Converts a <c>.sql</c> schema file to structured markdown (Phase 18). SQL is
/// text, but a schema dump is a *graph wearing a text costume* — so instead of
/// a passthrough, a dialect-tolerant scanner extracts tables (columns, types,
/// PK/FK/NOT NULL flags), foreign keys (inline and via <c>ALTER TABLE</c>),
/// <c>COMMENT ON</c> descriptions, and secondary objects (views, indexes,
/// enums, functions). Each table renders as its own <c>##</c> section — its
/// own chunk (the Phase 15 granularity lesson) — carrying both directions of
/// every relation: <c>FK → target</c> on columns and a computed
/// <b>Referenced by</b> line, so "which tables reference X" is answerable from
/// either side. Never throws on weird SQL: unparsed statements are tallied,
/// and if nothing parses at all the raw SQL passes through as a fenced block.
/// </summary>
public sealed class SqlConverter
{
    public ConversionResult Convert(Stream input, string sourcePath)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var sql = reader.ReadToEnd();
        var title = Path.GetFileNameWithoutExtension(sourcePath);

        var schema = Parse(sql);
        var sb = new StringBuilder();
        sb.Append("# ").Append(title).Append("\n\n");

        if (schema.Tables.Count == 0 && schema.Others.Count == 0)
        {
            // Nothing structural recognized — stay searchable as raw SQL.
            sb.Append("```sql\n").Append(sql.Trim()).Append("\n```");
        }
        else
        {
            Render(sb, schema);
        }

        return new ConversionResult(
            SourcePath: sourcePath,
            Format: SourceFormat.Sql,
            Markdown: sb.ToString().TrimEnd(),
            Title: title,
            SourceModifiedAt: null); // no embedded date — mtime fallback
    }

    // ---- model ----

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

    // ---- parsing ----

    private static readonly Regex CreateTableRx = new(
        @"^\s*CREATE\s+(?:GLOBAL\s+|LOCAL\s+|TEMP(?:ORARY)?\s+|UNLOGGED\s+)*TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>[^\s(]+)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AlterFkRx = new(
        @"^\s*ALTER\s+TABLE\s+(?:ONLY\s+)?(?:IF\s+EXISTS\s+)?(?<table>[^\s]+)[\s\S]*?FOREIGN\s+KEY\s*\((?<cols>[^)]*)\)\s*REFERENCES\s+(?<ref>[^\s(]+)\s*(?:\((?<refcols>[^)]*)\))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CommentOnRx = new(
        @"^\s*COMMENT\s+ON\s+(?<kind>TABLE|COLUMN)\s+(?<target>[^\s]+)\s+IS\s+'(?<text>(?:[^']|'')*)'",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CreateOtherRx = new(
        @"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:UNIQUE\s+|MATERIALIZED\s+)*(?<kind>VIEW|INDEX|FUNCTION|PROCEDURE|TRIGGER|SEQUENCE|TYPE)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:CONCURRENTLY\s+)?(?<name>[^\s(;]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EnumRx = new(
        @"AS\s+ENUM\s*\((?<values>[^)]*)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DataRx = new(
        @"^\s*(INSERT|UPDATE|DELETE|COPY|MERGE)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static SqlSchema Parse(string sql)
    {
        var schema = new SqlSchema();

        foreach (var statement in SplitStatements(StripComments(sql)))
        {
            try
            {
                ParseStatement(statement, schema);
            }
            catch
            {
                schema.UnrecognizedStatements++;
            }
        }

        return schema;
    }

    private static void ParseStatement(string statement, SqlSchema schema)
    {
        if (statement.Length < 4)
        {
            return;
        }

        if (CreateTableRx.Match(statement) is { Success: true } table)
        {
            var body = BalancedParenBody(statement, statement.IndexOf('(', table.Index + table.Length - 1));
            schema.Tables.Add(ParseTable(CleanIdentifier(table.Groups["name"].Value), body, schema));
            return;
        }

        if (AlterFkRx.Match(statement) is { Success: true } fk)
        {
            schema.ForeignKeys.Add(new SqlForeignKey(
                CleanIdentifier(fk.Groups["table"].Value),
                CleanColumns(fk.Groups["cols"].Value),
                CleanIdentifier(fk.Groups["ref"].Value),
                CleanColumns(fk.Groups["refcols"].Value)));
            return;
        }

        if (CommentOnRx.Match(statement) is { Success: true } comment)
        {
            ApplyComment(schema, comment.Groups["kind"].Value, CleanIdentifier(comment.Groups["target"].Value),
                comment.Groups["text"].Value.Replace("''", "'"));
            return;
        }

        if (CreateOtherRx.Match(statement) is { Success: true } other)
        {
            var kind = other.Groups["kind"].Value.ToUpperInvariant();
            var name = CleanIdentifier(other.Groups["name"].Value);
            var line = $"{kind} {name}";

            if (kind == "TYPE" && EnumRx.Match(statement) is { Success: true } e)
            {
                var values = e.Groups["values"].Value.Split(',')
                    .Select(v => v.Trim().Trim('\''))
                    .Where(v => v.Length > 0);
                line = $"TYPE {name} AS ENUM ({string.Join(", ", values)})";
            }
            else if (kind == "INDEX" && Regex.Match(statement, @"\bON\s+(?<t>[^\s(]+)\s*\((?<c>[^)]*)\)", RegexOptions.IgnoreCase) is { Success: true } ix)
            {
                line = $"INDEX {name} on {CleanIdentifier(ix.Groups["t"].Value)} ({CleanColumns(ix.Groups["c"].Value)})";
            }
            else if (kind is "VIEW")
            {
                var asAt = Regex.Match(statement, @"\bAS\b", RegexOptions.IgnoreCase);
                if (asAt.Success)
                {
                    var def = Regex.Replace(statement[(asAt.Index + 2)..].Trim(), @"\s+", " ");
                    line = $"VIEW {name} AS {(def.Length > 240 ? def[..240] + "…" : def)}";
                }
            }

            schema.Others.Add(line);
            return;
        }

        if (DataRx.IsMatch(statement))
        {
            schema.DataStatements++;
            return;
        }

        schema.UnrecognizedStatements++;
    }

    private static SqlTable ParseTable(string name, string body, SqlSchema schema)
    {
        var table = new SqlTable(name, []);
        var primaryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in SplitTopLevel(body, ','))
        {
            var trimmed = item.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var firstWord = FirstWord(trimmed).ToUpperInvariant();
            if (firstWord is "PRIMARY" or "CONSTRAINT" or "FOREIGN" or "UNIQUE" or "CHECK" or "KEY" or "INDEX" or "EXCLUDE" or "LIKE")
            {
                // Table-level constraint.
                if (Regex.Match(trimmed, @"PRIMARY\s+KEY\s*\((?<c>[^)]*)\)", RegexOptions.IgnoreCase) is { Success: true } pk)
                {
                    foreach (var c in pk.Groups["c"].Value.Split(','))
                    {
                        primaryKeys.Add(CleanIdentifier(c.Trim()));
                    }
                }

                if (Regex.Match(trimmed, @"FOREIGN\s+KEY\s*\((?<c>[^)]*)\)\s*REFERENCES\s+(?<r>[^\s(]+)\s*(?:\((?<rc>[^)]*)\))?", RegexOptions.IgnoreCase) is { Success: true } fk)
                {
                    schema.ForeignKeys.Add(new SqlForeignKey(name, CleanColumns(fk.Groups["c"].Value), CleanIdentifier(fk.Groups["r"].Value), CleanColumns(fk.Groups["rc"].Value)));
                }

                continue;
            }

            table.Columns.Add(ParseColumn(trimmed, name, schema));
        }

        foreach (var column in table.Columns.Where(c => primaryKeys.Contains(c.Name)))
        {
            column.Flags.Insert(0, "PK");
        }

        return table;
    }

    private static SqlColumn ParseColumn(string definition, string tableName, SqlSchema schema)
    {
        var columnName = CleanIdentifier(FirstWord(definition));
        var rest = definition[FirstWord(definition).Length..].Trim();

        // Type = leading tokens up to the first recognized flag keyword.
        var typeMatch = Regex.Match(rest,
            @"^(?<type>.+?)(?=\s+(NOT\s+NULL|NULL|DEFAULT|PRIMARY\s+KEY|REFERENCES|UNIQUE|CHECK|CONSTRAINT|GENERATED|COLLATE|AUTO_INCREMENT|IDENTITY)\b|$)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var type = Regex.Replace(typeMatch.Groups["type"].Value.Trim(), @"\s+", " ");

        var flags = new List<string>();
        if (Regex.IsMatch(rest, @"\bPRIMARY\s+KEY\b", RegexOptions.IgnoreCase))
        {
            flags.Add("PK");
        }

        if (Regex.IsMatch(rest, @"\bNOT\s+NULL\b", RegexOptions.IgnoreCase))
        {
            flags.Add("NOT NULL");
        }

        if (Regex.IsMatch(rest, @"\bUNIQUE\b", RegexOptions.IgnoreCase))
        {
            flags.Add("UNIQUE");
        }

        if (Regex.IsMatch(rest, @"\b(AUTO_INCREMENT|IDENTITY|GENERATED)\b", RegexOptions.IgnoreCase))
        {
            flags.Add("auto");
        }

        if (Regex.Match(rest, @"\bDEFAULT\s+(?<d>\S+)", RegexOptions.IgnoreCase) is { Success: true } def)
        {
            flags.Add($"DEFAULT {def.Groups["d"].Value.TrimEnd(',')}");
        }

        if (Regex.Match(rest, @"\bREFERENCES\s+(?<r>[^\s(]+)\s*(?:\((?<rc>[^)]*)\))?", RegexOptions.IgnoreCase) is { Success: true } fk)
        {
            schema.ForeignKeys.Add(new SqlForeignKey(tableName, columnName, CleanIdentifier(fk.Groups["r"].Value), CleanColumns(fk.Groups["rc"].Value)));
        }

        return new SqlColumn(columnName, type, flags);
    }

    private static void ApplyComment(SqlSchema schema, string kind, string target, string text)
    {
        if (kind.Equals("TABLE", StringComparison.OrdinalIgnoreCase))
        {
            FindTable(schema, target)?.Comment ??= text;
            return;
        }

        // COLUMN target: table.column (possibly schema-qualified).
        var lastDot = target.LastIndexOf('.');
        if (lastDot <= 0)
        {
            return;
        }

        var table = FindTable(schema, target[..lastDot]);
        var column = table?.Columns.FirstOrDefault(c => c.Name.Equals(target[(lastDot + 1)..], StringComparison.OrdinalIgnoreCase));
        if (column is not null)
        {
            column.Comment ??= text;
        }
    }

    private static SqlTable? FindTable(SqlSchema schema, string name)
        => schema.Tables.FirstOrDefault(t => TableNamesEqual(t.Name, name));

    /// <summary>Match ignoring schema qualification: <c>public.accounts</c> == <c>accounts</c>.</summary>
    internal static bool TableNamesEqual(string a, string b)
        => string.Equals(LastSegment(a), LastSegment(b), StringComparison.OrdinalIgnoreCase);

    private static string LastSegment(string name)
        => name[(name.LastIndexOf('.') + 1)..];

    // ---- rendering ----

    private static void Render(StringBuilder sb, SqlSchema schema)
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

    // ---- low-level text helpers (internal for tests) ----

    /// <summary>Removes <c>--</c> line comments and <c>/* */</c> block comments; leaves string literals intact.</summary>
    internal static string StripComments(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];

            if (c == '\'')
            {
                var end = FindStringEnd(sql, i);
                sb.Append(sql, i, end - i);
                i = end;
            }
            else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }

    /// <summary>Splits on <c>;</c> outside string literals and Postgres dollar-quoted bodies.</summary>
    internal static List<string> SplitStatements(string sql)
    {
        var statements = new List<string>();
        var start = 0;
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '\'')
            {
                i = FindStringEnd(sql, i);
            }
            else if (c == '$' && Regex.Match(sql[i..Math.Min(sql.Length, i + 64)], @"^\$[A-Za-z_]*\$") is { Success: true } tag)
            {
                var close = sql.IndexOf(tag.Value, i + tag.Length, StringComparison.Ordinal);
                i = close < 0 ? sql.Length : close + tag.Length;
            }
            else if (c == ';')
            {
                statements.Add(sql[start..i]);
                start = i + 1;
                i++;
            }
            else
            {
                i++;
            }
        }

        if (start < sql.Length && sql[start..].Trim().Length > 0)
        {
            statements.Add(sql[start..]);
        }

        return statements;
    }

    private static int FindStringEnd(string sql, int openQuote)
    {
        var i = openQuote + 1;
        while (i < sql.Length)
        {
            if (sql[i] == '\'')
            {
                if (i + 1 < sql.Length && sql[i + 1] == '\'')
                {
                    i += 2; // '' escape
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return sql.Length;
    }

    /// <summary>The text inside the paren pair opening at <paramref name="openIndex"/>.</summary>
    internal static string BalancedParenBody(string text, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '\'')
            {
                i = FindStringEnd(text, i) - 1;
            }
            else if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && --depth == 0)
            {
                return text[(openIndex + 1)..i];
            }
        }

        return text[(openIndex + 1)..];
    }

    /// <summary>Splits on a separator at paren depth zero, outside strings.</summary>
    internal static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'')
            {
                i = FindStringEnd(text, i) - 1;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == separator && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }

    private static string FirstWord(string text)
    {
        var i = 0;
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '(')
        {
            i++;
        }

        return text[..i];
    }

    internal static string CleanIdentifier(string identifier)
        => string.Join('.', identifier.Trim().Split('.').Select(p => p.Trim('"', '`', '[', ']', ' ')));

    private static string CleanColumns(string columns)
        => string.Join(", ", columns.Split(',').Select(c => CleanIdentifier(c.Trim())).Where(c => c.Length > 0));
}
