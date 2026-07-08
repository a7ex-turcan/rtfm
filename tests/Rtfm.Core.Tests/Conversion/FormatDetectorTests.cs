using System.IO.Compression;
using System.Text;
using Rtfm.Core.Conversion;

namespace Rtfm.Core.Tests.Conversion;

public class FormatDetectorTests
{
    [Fact]
    public void Detects_docx_by_zip_magic()
    {
        // "PK\x03\x04" — the ZIP/OOXML signature. Not a parseable zip, so the
        // container check falls back to the historic default: docx.
        var bytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00 };
        using var stream = new MemoryStream(bytes);

        Assert.Equal(SourceFormat.Docx, FormatDetector.Detect("whatever.docx", stream));
    }

    [Fact]
    public void Disambiguates_xlsx_from_docx_by_container_folder()
    {
        using var xlsx = BuildZipWith("xl/workbook.xml");
        Assert.Equal(SourceFormat.Xlsx, FormatDetector.Detect("lies.docx", xlsx)); // content wins over extension
        Assert.Equal(0, xlsx.Position); // stream restored for the converter

        using var docx = BuildZipWith("word/document.xml");
        Assert.Equal(SourceFormat.Docx, FormatDetector.Detect("lies.xlsx", docx));
    }

    [Fact]
    public void Detects_pdf_by_magic()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7\nrest of file"));
        Assert.Equal(SourceFormat.Pdf, FormatDetector.Detect("whatever.bin", stream));
    }

    [Fact]
    public void Detects_csv_by_extension()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("a,b,c\n1,2,3\n"));
        Assert.Equal(SourceFormat.Csv, FormatDetector.Detect("data.csv", stream));
    }

    private static MemoryStream BuildZipWith(string entryName)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry(entryName).Open());
            writer.Write("<xml/>");
        }

        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void Detects_markdown_by_extension()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("# Just markdown\n"));
        Assert.Equal(SourceFormat.Markdown, FormatDetector.Detect("notes.md", stream));
    }

    [Fact]
    public void Detects_bare_html_by_content_over_doc_extension()
    {
        // Jira's "Export to Word" is a .doc that is really bare HTML — no MIME
        // wrapper, so it must not be mistaken for MHTML or fall through to Unknown.
        var html = "<!DOCTYPE html>\n<html><head><title>[#AEXP-130] Foo</title></head>"
            + "<body><p>hi</p></body></html>";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(html));

        Assert.Equal(SourceFormat.Html, FormatDetector.Detect("AEXP-130.doc", stream));
        Assert.Equal(0, stream.Position); // stream restored for the converter
    }

    [Fact]
    public void Detects_html_by_extension_when_content_lacks_doctype()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("<body>fragment</body>"));
        Assert.Equal(SourceFormat.Html, FormatDetector.Detect("page.htm", stream));
    }

    [Fact]
    public void Returns_unknown_for_unrecognized_content()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("plain text, no clues"));
        Assert.Equal(SourceFormat.Unknown, FormatDetector.Detect("mystery.dat", stream));
    }
}
