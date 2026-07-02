using System.Text;

namespace Rtfm.Core.Conversion;

/// <summary>
/// Converts a CSV to markdown (§2.5, Phase 9 route): one pipe table, first row
/// as the header, filename as the title. RFC 4180-ish parsing (quoted fields,
/// doubled-quote escapes, embedded delimiters/newlines) with a light delimiter
/// sniff (comma / semicolon / tab) for the European-locale exports that use
/// <c>;</c>. No dependency — the format doesn't warrant one. Oversized tables
/// are the chunker's problem, and it already solves it (row-split with repeated
/// header).
/// </summary>
public sealed class CsvConverter
{
    public ConversionResult Convert(Stream input, string sourcePath)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();

        var delimiter = SniffDelimiter(text);
        var rows = Parse(text, delimiter);
        var title = Path.GetFileNameWithoutExtension(sourcePath);

        var sb = new StringBuilder();
        sb.Append("# ").Append(title).Append("\n\n");

        if (rows.Count > 0)
        {
            var columns = rows[0].Count;
            for (var r = 0; r < rows.Count; r++)
            {
                sb.Append('|');
                for (var c = 0; c < columns; c++)
                {
                    var value = c < rows[r].Count ? rows[r][c] : string.Empty;
                    sb.Append(' ').Append(Escape(value)).Append(" |");
                }

                sb.Append('\n');

                if (r == 0)
                {
                    sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", columns))).Append('\n');
                }
            }
        }

        return new ConversionResult(
            SourcePath: sourcePath,
            Format: SourceFormat.Csv,
            Markdown: sb.ToString().TrimEnd(),
            Title: title,
            SourceModifiedAt: null); // CSV carries no embedded date — mtime fallback applies
    }

    /// <summary>Picks the most frequent of comma/semicolon/tab on the first line. Internal for tests.</summary>
    internal static char SniffDelimiter(string text)
    {
        var firstLine = text.AsSpan();
        var end = firstLine.IndexOfAny('\r', '\n');
        if (end >= 0)
        {
            firstLine = firstLine[..end];
        }

        int commas = 0, semicolons = 0, tabs = 0;
        var inQuotes = false;
        foreach (var ch in firstLine)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes)
            {
                switch (ch)
                {
                    case ',': commas++; break;
                    case ';': semicolons++; break;
                    case '\t': tabs++; break;
                }
            }
        }

        if (tabs > commas && tabs > semicolons)
        {
            return '\t';
        }

        return semicolons > commas ? ';' : ',';
    }

    /// <summary>RFC 4180-ish parse: quoted fields, "" escapes, delimiters/newlines inside quotes. Internal for tests.</summary>
    internal static List<List<string>> Parse(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        void EndField()
        {
            row.Add(field.ToString());
            field.Clear();
        }

        void EndRow()
        {
            EndField();
            // Skip rows that are entirely empty (e.g. trailing newline).
            if (row.Count > 1 || row[0].Length > 0)
            {
                rows.Add(row);
            }

            row = [];
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
            }
            else if (ch == '"' && field.Length == 0)
            {
                inQuotes = true;
            }
            else if (ch == delimiter)
            {
                EndField();
            }
            else if (ch == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                EndRow();
            }
            else if (ch == '\n')
            {
                EndRow();
            }
            else
            {
                field.Append(ch);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            EndRow();
        }

        return rows;
    }

    private static string Escape(string value)
        => value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace("|", "\\|").Trim();
}
