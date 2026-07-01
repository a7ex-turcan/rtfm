using System.Text;
using Rtfm.Core.Conversion;

namespace Rtfm.Core.Tests.Conversion;

public class FormatDetectorTests
{
    [Fact]
    public void Detects_docx_by_zip_magic()
    {
        // "PK\x03\x04" — the ZIP/OOXML signature.
        var bytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00 };
        using var stream = new MemoryStream(bytes);

        Assert.Equal(DocumentFormat.Docx, FormatDetector.Detect("whatever.docx", stream));
    }

    [Fact]
    public void Detects_markdown_by_extension()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("# Just markdown\n"));
        Assert.Equal(DocumentFormat.Markdown, FormatDetector.Detect("notes.md", stream));
    }

    [Fact]
    public void Returns_unknown_for_unrecognized_content()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("plain text, no clues"));
        Assert.Equal(DocumentFormat.Unknown, FormatDetector.Detect("mystery.dat", stream));
    }
}
