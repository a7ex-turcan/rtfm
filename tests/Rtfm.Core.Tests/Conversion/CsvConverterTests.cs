using System.Text;
using Rtfm.Core.Conversion;

namespace Rtfm.Core.Tests.Conversion;

public class CsvConverterTests
{
    [Fact]
    public void Converts_to_one_pipe_table_with_filename_title()
    {
        var csv = "Name,Port,Notes\nalpha,9001,\"handles auth, sessions\"\nbravo,9002,\"says \"\"hello\"\"\"\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = new CsvConverter().Convert(stream, "servers.csv");

        Assert.Equal(SourceFormat.Csv, result.Format);
        Assert.Equal("servers", result.Title);
        Assert.Contains("# servers", result.Markdown);
        Assert.Contains("| Name | Port | Notes |", result.Markdown);
        Assert.Contains("| --- | --- | --- |", result.Markdown);
        Assert.Contains("| alpha | 9001 | handles auth, sessions |", result.Markdown); // quoted comma survives
        Assert.Contains("says \"hello\"", result.Markdown); // doubled-quote escape
        Assert.Null(result.SourceModifiedAt); // no embedded date → mtime fallback
    }

    [Theory]
    [InlineData("a,b,c\n1,2,3", ',')]
    [InlineData("a;b;c\n1;2;3", ';')]
    [InlineData("a\tb\tc\n1\t2\t3", '\t')]
    [InlineData("\"x;y\",b\n1,2", ',')] // semicolon inside quotes doesn't fool the sniff
    public void Sniffs_the_delimiter_from_the_first_line(string text, char expected)
        => Assert.Equal(expected, CsvConverter.SniffDelimiter(text));

    [Fact]
    public void Parses_newlines_inside_quoted_fields()
    {
        var rows = CsvConverter.Parse("a,\"line one\nline two\"\nnext,row", ',');

        Assert.Equal(2, rows.Count);
        Assert.Equal("line one\nline two", rows[0][1]);
        Assert.Equal(["next", "row"], rows[1]);
    }

    [Fact]
    public void Pads_short_rows_to_the_header_width()
    {
        var csv = "a,b,c\n1,2\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = new CsvConverter().Convert(stream, "ragged.csv");

        Assert.Contains("| 1 | 2 |  |", result.Markdown);
    }
}
