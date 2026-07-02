using System.Text;
using Rtfm.Core.Conversion;

namespace Rtfm.Core.Tests.Conversion;

public class MarkdownConverterTests
{
    private static ConversionResult Convert(string markdown)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(markdown));
        return new DocumentConverter().Convert(stream, "notes.md");
    }

    [Fact]
    public void Passes_markdown_through_and_extracts_title()
    {
        var result = Convert("# My Notes\n\nSome body text.\n");

        Assert.Equal(SourceFormat.Markdown, result.Format);
        Assert.Equal("My Notes", result.Title);
        Assert.Contains("# My Notes", result.Markdown);
        Assert.Contains("Some body text.", result.Markdown);
    }

    [Fact]
    public void Normalizes_line_endings_and_runaway_blank_lines()
    {
        var result = Convert("# T\r\n\r\n\r\n\r\nbody\r\n");

        Assert.DoesNotContain("\r", result.Markdown);
        Assert.DoesNotContain("\n\n\n", result.Markdown);
    }
}
