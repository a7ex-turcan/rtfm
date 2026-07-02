using ClosedXML.Excel;
using Rtfm.Core.Conversion;

namespace Rtfm.Core.Tests.Conversion;

public class XlsxConverterTests
{
    [Fact]
    public void Sheets_become_sections_with_pipe_tables()
    {
        using var stream = new MemoryStream();
        using (var workbook = new XLWorkbook())
        {
            var servers = workbook.AddWorksheet("Servers");
            servers.Cell(1, 1).Value = "Name";
            servers.Cell(1, 2).Value = "Port";
            servers.Cell(2, 1).Value = "alpha";
            servers.Cell(2, 2).Value = 9001;
            servers.Cell(3, 1).Value = "bravo | prod"; // pipe must be escaped
            servers.Cell(3, 2).Value = 9002;

            var owners = workbook.AddWorksheet("Owners");
            owners.Cell(1, 1).Value = "Team";
            owners.Cell(2, 1).Value = "Platform";

            workbook.SaveAs(stream);
        }

        stream.Position = 0;
        var result = new XlsxConverter().Convert(stream, "infra.xlsx");

        Assert.Equal(SourceFormat.Xlsx, result.Format);
        Assert.Equal("infra", result.Title); // no workbook title set → filename stem
        Assert.Contains("# infra", result.Markdown);
        Assert.Contains("## Servers", result.Markdown);
        Assert.Contains("## Owners", result.Markdown);
        Assert.Contains("| Name | Port |", result.Markdown);
        Assert.Contains("| alpha | 9001 |", result.Markdown);
        Assert.Contains("bravo \\| prod", result.Markdown);
        Assert.Contains("| --- | --- |", result.Markdown);
    }

    [Fact]
    public void Empty_sheets_are_skipped_and_formulas_use_computed_values()
    {
        using var stream = new MemoryStream();
        using (var workbook = new XLWorkbook())
        {
            var data = workbook.AddWorksheet("Data");
            data.Cell(1, 1).Value = "Total";
            data.Cell(2, 1).FormulaA1 = "=40+2";

            workbook.AddWorksheet("Blank"); // no used range

            workbook.SaveAs(stream);
        }

        stream.Position = 0;
        var result = new XlsxConverter().Convert(stream, "calc.xlsx");

        Assert.Contains("| 42 |", result.Markdown);
        Assert.DoesNotContain("## Blank", result.Markdown);
        Assert.DoesNotContain("=40+2", result.Markdown);
    }
}
