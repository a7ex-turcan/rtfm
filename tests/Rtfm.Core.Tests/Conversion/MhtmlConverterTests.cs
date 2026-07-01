using System.Text;
using Rtfm.Core.Conversion;

namespace Rtfm.Core.Tests.Conversion;

public class MhtmlConverterTests
{
    // A minimal Confluence-style "Export to Word" file: multipart/related with a
    // quoted-printable text/html part (=E2=80=99 is a UTF-8 right single quote),
    // editor attributes, an image, and a page footer — all of which the pipeline
    // must clean up.
    private const string SampleMhtml =
        "MIME-Version: 1.0\r\n" +
        "Content-Type: multipart/related; boundary=\"BOUNDARY\"\r\n" +
        "\r\n" +
        "--BOUNDARY\r\n" +
        "Content-Type: text/html; charset=UTF-8\r\n" +
        "Content-Transfer-Encoding: quoted-printable\r\n" +
        "\r\n" +
        "<html><head><style>.x{color:red}</style></head><body>\r\n" +
        "<h1>Doc Title</h1>\r\n" +
        "<h2 local-id=3D\"abc\" class=3D\"heading\">Section</h2>\r\n" +
        "<p class=3D\"foo\">Hello=E2=80=99s world &amp; more</p>\r\n" +
        "<table class=3D\"confluenceTable\"><tbody>\r\n" +
        "<tr><th>Name</th><th>Value</th></tr>\r\n" +
        "<tr><td>alpha</td><td>1</td></tr>\r\n" +
        "</tbody></table>\r\n" +
        "<img src=3D\"cid:logo\"/>\r\n" +
        "<div id=3D\"footer\">Created by X, last modified by Y</div>\r\n" +
        "</body></html>\r\n" +
        "--BOUNDARY--\r\n";

    private static ConversionResult Convert()
    {
        var bytes = Encoding.ASCII.GetBytes(SampleMhtml);
        using var stream = new MemoryStream(bytes);
        return new DocumentConverter().Convert(stream, "sample.doc");
    }

    [Fact]
    public void Detects_mhtml_by_content_despite_doc_extension()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(SampleMhtml));
        Assert.Equal(DocumentFormat.Mhtml, FormatDetector.Detect("sample.doc", stream));
        // Detection must not consume the stream.
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void Preserves_headings_and_reports_title()
    {
        var result = Convert();

        Assert.Equal(DocumentFormat.Mhtml, result.Format);
        Assert.Equal("Doc Title", result.Title);
        Assert.Contains("# Doc Title", result.Markdown);
        Assert.Contains("## Section", result.Markdown);
    }

    [Fact]
    public void Renders_tables_as_pipe_tables()
    {
        var markdown = Convert().Markdown;

        Assert.Contains("| Name | Value |", markdown);
        Assert.Contains("| alpha | 1 |", markdown);
    }

    [Fact]
    public void Decodes_quoted_printable_and_entities()
    {
        var markdown = Convert().Markdown;

        Assert.Contains("Hello’s world & more", markdown); // QP ’ + &amp; decoded
        Assert.DoesNotContain("&amp;", markdown);
    }

    [Fact]
    public void Strips_editor_attributes_images_and_footer()
    {
        var markdown = Convert().Markdown;

        Assert.DoesNotContain("local-id", markdown);
        Assert.DoesNotContain("class=", markdown);
        Assert.DoesNotContain("<img", markdown);
        Assert.DoesNotContain("Created by", markdown);   // #footer removed
    }
}
