using System.Text;
using ClosedXML.Excel;

namespace Rtfm.Core.Conversion;

/// <summary>
/// Converts an Excel workbook (.xlsx) to markdown (§2.5, Phase 9 route). Each
/// visible sheet becomes a section — breadcrumb <c>Workbook &gt; Sheet</c> —
/// and its used range a pipe table whose first row is the header. The chunker
/// already splits oversized tables by rows, repeating the header row, so big
/// sheets arrive as self-describing chunks with no extra work here. Formulas
/// contribute their *computed* display values (what a reader sees), not the
/// formula text.
/// </summary>
public sealed class XlsxConverter
{
    public ConversionResult Convert(Stream input, string sourcePath)
    {
        using var workbook = new XLWorkbook(input);

        var title = FirstNonEmpty(workbook.Properties.Title)
            ?? Path.GetFileNameWithoutExtension(sourcePath);

        var sb = new StringBuilder();
        sb.Append("# ").Append(title).Append("\n\n");

        foreach (var sheet in workbook.Worksheets.Where(ws => ws.Visibility == XLWorksheetVisibility.Visible))
        {
            var range = sheet.RangeUsed();
            if (range is null)
            {
                continue; // empty sheet
            }

            sb.Append("## ").Append(sheet.Name).Append("\n\n");
            AppendTable(sb, range);
            sb.Append('\n');
        }

        var modified = workbook.Properties.Modified;

        return new ConversionResult(
            SourcePath: sourcePath,
            Format: SourceFormat.Xlsx,
            Markdown: sb.ToString().TrimEnd(),
            Title: title,
            SourceModifiedAt: modified == default
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(modified, DateTimeKind.Utc)));
    }

    private static void AppendTable(StringBuilder sb, IXLRange range)
    {
        var columns = range.ColumnCount();
        var first = true;

        foreach (var row in range.Rows())
        {
            sb.Append('|');
            for (var c = 1; c <= columns; c++)
            {
                sb.Append(' ').Append(Cell(row.Cell(c))).Append(" |");
            }

            sb.Append('\n');

            if (first)
            {
                sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", columns))).Append('\n');
                first = false;
            }
        }
    }

    /// <summary>Formatted display text, made pipe-table-safe.</summary>
    private static string Cell(IXLCell cell)
        => cell.GetFormattedString()
            .Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ')
            .Replace("|", "\\|")
            .Trim();

    private static string? FirstNonEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
