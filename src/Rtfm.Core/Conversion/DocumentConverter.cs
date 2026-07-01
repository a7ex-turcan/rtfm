namespace Rtfm.Core.Conversion;

/// <summary>
/// Front door for conversion: detect the format (§2.5) and dispatch to the
/// matching converter. MHTML is live (Phase 1a); docx and markdown arrive in
/// Phase 1b/1c and currently throw a clear "not yet" error.
/// </summary>
public sealed class DocumentConverter
{
    private readonly MhtmlConverter _mhtml = new();

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
            DocumentFormat.Docx => throw new NotSupportedException(
                $"docx conversion is not implemented yet (Phase 1b): {path}"),
            DocumentFormat.Markdown => throw new NotSupportedException(
                $"markdown passthrough is not implemented yet (Phase 1c): {path}"),
            _ => throw new NotSupportedException(
                $"Unrecognized document format: {path}"),
        };
    }
}
