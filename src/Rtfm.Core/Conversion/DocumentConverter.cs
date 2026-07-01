namespace Rtfm.Core.Conversion;

/// <summary>
/// Front door for conversion: detect the format (§2.5) and dispatch to the
/// matching converter. All three routes are live: MHTML (1a), docx (1b), and
/// markdown passthrough (1c).
/// </summary>
public sealed class DocumentConverter
{
    private readonly MhtmlConverter _mhtml = new();
    private readonly DocxConverter _docx = new();
    private readonly MarkdownConverter _markdown = new();

    /// <summary>Converts the file at <paramref name="path"/> to markdown.</summary>
    public ConversionResult Convert(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert(stream, path);
    }

    /// <summary>
    /// Converts an already-open <paramref name="stream"/>, using
    /// <paramref name="path"/> only for format sniffing and diagnostics.
    /// </summary>
    public ConversionResult Convert(Stream stream, string path)
    {
        var format = FormatDetector.Detect(path, stream);

        return format switch
        {
            DocumentFormat.Mhtml => _mhtml.Convert(stream, path),
            DocumentFormat.Docx => _docx.Convert(stream, path),
            DocumentFormat.Markdown => _markdown.Convert(stream, path),
            _ => throw new NotSupportedException(
                $"Unrecognized document format: {path}"),
        };
    }
}
