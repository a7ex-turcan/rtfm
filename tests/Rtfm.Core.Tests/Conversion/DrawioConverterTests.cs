using System.IO.Compression;
using System.Text;
using Rtfm.Core.Conversion;

namespace Rtfm.Core.Tests.Conversion;

public class DrawioConverterTests
{
    /// <summary>A two-table ER diagram with an FK edge — the target use case.</summary>
    private const string ErGraphModel =
        """
        <mxGraphModel><root>
          <mxCell id="0" /><mxCell id="1" parent="0" />
          <mxCell id="t1" value="accounts" style="shape=table;" vertex="1" parent="1" />
          <mxCell id="t1r1" style="shape=tableRow;" vertex="1" parent="t1" />
          <mxCell id="t1r1a" value="PK" style="shape=partialRectangle;" vertex="1" parent="t1r1" />
          <mxCell id="t1r1b" value="id" style="shape=partialRectangle;" vertex="1" parent="t1r1" />
          <mxCell id="t1r2" style="shape=tableRow;" vertex="1" parent="t1" />
          <mxCell id="t1r2b" value="name" style="shape=partialRectangle;" vertex="1" parent="t1r2" />
          <mxCell id="t2" value="&lt;b&gt;users&lt;/b&gt;" style="shape=table;" vertex="1" parent="1" />
          <mxCell id="t2r1" style="shape=tableRow;" vertex="1" parent="t2" />
          <mxCell id="t2r1b" value="account_id FK" style="shape=partialRectangle;" vertex="1" parent="t2r1" />
          <mxCell id="e1" value="1:N" style="edgeStyle=entityRelation;" edge="1" parent="1" source="t2r1b" target="t1r1b" />
          <mxCell id="note" value="Accounts own users." vertex="1" parent="1" />
        </root></mxGraphModel>
        """;

    [Fact]
    public void Uncompressed_page_renders_tables_and_relations()
    {
        var xml = $"""<mxfile modified="2026-06-15T10:00:00Z"><diagram name="Schema">{ErGraphModel}</diagram></mxfile>""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var result = new DrawioConverter().Convert(stream, "platform-erd.drawio");

        Assert.Equal(SourceFormat.Drawio, result.Format);
        Assert.Equal("platform-erd", result.Title);
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero), result.SourceModifiedAt);
        Assert.Contains("## Schema", result.Markdown);
        Assert.Contains("**accounts** — PK id; name", result.Markdown);
        Assert.Contains("**users** — account_id FK", result.Markdown); // HTML label stripped
        // Edge endpoints resolve through the row cells up to the labeled column,
        // and the edge label rides along.
        Assert.Contains("account_id FK → id: 1:N", result.Markdown);
        Assert.Contains("Accounts own users.", result.Markdown);
    }

    [Fact]
    public void Compressed_page_is_inflated_and_rendered()
    {
        // Encode exactly the way draw.io does: URI-encode → raw DEFLATE → base64.
        var encoded = Uri.EscapeDataString(ErGraphModel);
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(Encoding.UTF8.GetBytes(encoded));
        }

        var xml = $"""<mxfile><diagram name="Compressed Schema">{Convert.ToBase64String(buffer.ToArray())}</diagram></mxfile>""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var result = new DrawioConverter().Convert(stream, "legacy.drawio");

        Assert.Contains("## Compressed Schema", result.Markdown);
        Assert.Contains("**accounts** — PK id; name", result.Markdown);
        Assert.Contains("account_id FK → id: 1:N", result.Markdown);
        Assert.Null(result.SourceModifiedAt); // no modified attribute → mtime fallback
    }

    [Fact]
    public void Multiple_pages_become_separate_sections()
    {
        var xml = """
            <mxfile>
              <diagram name="Overview"><mxGraphModel><root>
                <mxCell id="0" /><mxCell id="1" parent="0" />
                <mxCell id="a" value="Gateway" vertex="1" parent="1" />
              </root></mxGraphModel></diagram>
              <diagram><mxGraphModel><root>
                <mxCell id="0" /><mxCell id="1" parent="0" />
                <mxCell id="b" value="Worker" vertex="1" parent="1" />
              </root></mxGraphModel></diagram>
            </mxfile>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var result = new DrawioConverter().Convert(stream, "arch.drawio");

        Assert.Contains("## Overview", result.Markdown);
        Assert.Contains("## Page 2", result.Markdown); // unnamed page gets a fallback name
        Assert.Contains("Gateway", result.Markdown);
        Assert.Contains("Worker", result.Markdown);
    }

    [Fact]
    public void Detector_recognizes_mxfile_content_and_drawio_extension()
    {
        using var byContent = new MemoryStream(Encoding.UTF8.GetBytes("<mxfile host=\"app.diagrams.net\"><diagram/></mxfile>"));
        Assert.Equal(SourceFormat.Drawio, FormatDetector.Detect("diagram.xml", byContent));

        using var byExt = new MemoryStream(Encoding.UTF8.GetBytes("  \n<?xml version=\"1.0\"?><something/>"));
        Assert.Equal(SourceFormat.Drawio, FormatDetector.Detect("diagram.drawio", byExt));
    }

    [Theory]
    [InlineData("<b>users</b>", "users")]
    [InlineData("orders&lt;br&gt;table", "orders table")] // escaped in XML attr, unescaped by XLinq
    [InlineData("plain", "plain")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData(null, "")]
    public void Labels_strip_html_and_collapse_whitespace(string? input, string expected)
        => Assert.Equal(expected, DrawioConverter.CleanLabel(input?.Replace("&lt;", "<").Replace("&gt;", ">")));
}
