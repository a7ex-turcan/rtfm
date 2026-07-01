using System.Text;
using System.Text.RegularExpressions;

namespace Rtfm.Core.Conversion;

/// <summary>
/// "Converts" a plain Markdown (.md) file — it is already markdown, so this is a
/// passthrough with only light normalization (line endings, runaway blank lines)
/// and a best-effort title from the first heading.
/// </summary>
public sealed class MarkdownConverter
{
    private static readonly Regex BlankLines = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex AtxHeading = new(@"^#{1,6}\s+(.+?)\s*#*\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    public ConversionResult Convert(Stream input, string sourcePath)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();

        text = text.Replace("\r\n", "\n").Replace("\r", "\n");
        text = BlankLines.Replace(text, "\n\n").Trim();

        var title = ExtractTitle(text);
        return new ConversionResult(sourcePath, DocumentFormat.Markdown, text, title);
    }

    private static string? ExtractTitle(string markdown)
    {
        var match = AtxHeading.Match(markdown);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}
