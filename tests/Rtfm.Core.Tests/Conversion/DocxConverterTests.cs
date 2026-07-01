using System.IO.Compression;
using System.Text;
using Rtfm.Core.Conversion;

namespace Rtfm.Core.Tests.Conversion;

public class DocxConverterTests
{
    [Fact]
    public void Converts_docx_headings_and_body_to_markdown()
    {
        using var docx = BuildMinimalDocx();
        var result = new DocumentConverter().Convert(docx, "sample.docx");

        Assert.Equal(DocumentFormat.Docx, result.Format);
        Assert.Contains("# Doc Title", result.Markdown);   // Heading 1 style → h1 → #
        Assert.Contains("Hello world.", result.Markdown);
        Assert.Equal("Doc Title", result.Title);
    }

    /// <summary>
    /// Builds the smallest .docx (an OOXML zip) that Mammoth will convert: a
    /// styles part naming "heading 1", and a body with one styled heading and
    /// one plain paragraph.
    /// </summary>
    private static MemoryStream BuildMinimalDocx()
    {
        const string ns = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        var contentTypes =
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
              <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
            </Types>
            """;

        var rootRels =
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
            </Relationships>
            """;

        var docRels =
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
            </Relationships>
            """;

        var styles =
            $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:styles xmlns:w="{ns}">
              <w:style w:type="paragraph" w:styleId="Heading1">
                <w:name w:val="heading 1"/>
              </w:style>
            </w:styles>
            """;

        var document =
            $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="{ns}">
              <w:body>
                <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Doc Title</w:t></w:r></w:p>
                <w:p><w:r><w:t>Hello world.</w:t></w:r></w:p>
              </w:body>
            </w:document>
            """;

        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "[Content_Types].xml", contentTypes);
            WriteEntry(zip, "_rels/.rels", rootRels);
            WriteEntry(zip, "word/_rels/document.xml.rels", docRels);
            WriteEntry(zip, "word/styles.xml", styles);
            WriteEntry(zip, "word/document.xml", document);
        }

        stream.Position = 0;
        return stream;
    }

    private static void WriteEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
