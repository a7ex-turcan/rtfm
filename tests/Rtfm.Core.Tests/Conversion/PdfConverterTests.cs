using Rtfm.Core.Conversion;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Rtfm.Core.Tests.Conversion;

public class PdfConverterTests
{
    [Fact]
    public void Converts_headings_by_font_size_and_keeps_body_text()
    {
        // Synthetic PDF: 20pt title, 16pt section heading, 11pt body lines.
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);

        page.AddText("Server Guide", 20, new PdfPoint(50, 800), font);
        page.AddText("The alpha server answers on port 9001 today.", 11, new PdfPoint(50, 770), font);
        page.AddText("It restarts nightly and logs to the audit sink.", 11, new PdfPoint(50, 755), font);
        page.AddText("Configuration", 16, new PdfPoint(50, 720), font);
        page.AddText("Set ALPHA_MODE to standard before deploying.", 11, new PdfPoint(50, 690), font);

        using var stream = new MemoryStream(builder.Build());
        var result = new PdfConverter().Convert(stream, "guide.pdf");

        Assert.Equal(SourceFormat.Pdf, result.Format);
        Assert.Contains("# Server Guide", result.Markdown);
        Assert.Contains("## Configuration", result.Markdown);
        Assert.Contains("port 9001", result.Markdown);
        Assert.Contains("ALPHA_MODE", result.Markdown);
        // No metadata title in this fixture → the top heading becomes the title.
        Assert.Equal("Server Guide", result.Title);
    }

    [Fact]
    public void Uniform_font_degrades_to_plain_paragraphs_not_garbage()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText("Everything here is body text at one size.", 11, new PdfPoint(50, 800), font);
        page.AddText("So nothing should become a heading.", 11, new PdfPoint(50, 785), font);

        using var stream = new MemoryStream(builder.Build());
        var result = new PdfConverter().Convert(stream, "flat.pdf");

        Assert.DoesNotContain("#", result.Markdown);
        Assert.Contains("body text at one size", result.Markdown);
        // No metadata title, no headings → filename stem (Phase 21), not null.
        Assert.Equal("flat", result.Title);
    }

    [Theory]
    [InlineData("Team Handbook", "Team Handbook")]      // honest title survives
    [InlineData("  Padded Title ", "Padded Title")]     // trimmed
    [InlineData("v2.1 Release Notes", "v2.1 Release Notes")] // dot mid-token but spaced text is not a filename
    [InlineData("index.html", null)]                    // web-to-PDF filename stamp
    [InlineData("report_final.docx", null)]             // extension-bearing single token
    [InlineData("C:/exports/cdm.pdf", null)]            // path
    [InlineData("IIDD  EEppiicc", null)]                // doubled-text-run artifact
    [InlineData("Bookkeeping fees", "Bookkeeping fees")] // natural doubles stay
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Metadata_titles_are_sanitized(string? raw, string? expected)
    {
        Assert.Equal(expected, PdfConverter.SanitizeMetadataTitle(raw));
    }

    [Theory]
    [InlineData("D:20260702134500+02'00'", 2026, 7, 2, 13, 45, 120)]
    [InlineData("D:20260702134500Z", 2026, 7, 2, 13, 45, 0)]
    [InlineData("D:20260702", 2026, 7, 2, 0, 0, 0)]
    [InlineData("20260702134500", 2026, 7, 2, 13, 45, 0)]
    public void Parses_pdf_date_strings(string input, int y, int mo, int d, int h, int mi, int offsetMinutes)
    {
        var parsed = PdfConverter.TryParsePdfDate(input);

        Assert.NotNull(parsed);
        Assert.Equal(new DateTime(y, mo, d, h, mi, 0), parsed.Value.DateTime);
        Assert.Equal(TimeSpan.FromMinutes(offsetMinutes), parsed.Value.Offset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("D:junk")]
    [InlineData("D:2026")]
    public void Rejects_unparseable_pdf_dates(string? input)
        => Assert.Null(PdfConverter.TryParsePdfDate(input));
}
