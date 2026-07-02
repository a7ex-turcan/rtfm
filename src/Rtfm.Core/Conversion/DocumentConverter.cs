namespace Rtfm.Core.Conversion;

/// <summary>
/// Front door for conversion: detect the format (§2.5) and dispatch to the
/// matching converter. Live routes: MHTML (1a), docx (1b), markdown
/// passthrough (1c), and the Phase 9 additions — PDF, xlsx, CSV.
/// </summary>
public sealed class DocumentConverter
{
    private readonly MhtmlConverter _mhtml = new();
    private readonly DocxConverter _docx = new();
    private readonly MarkdownConverter _markdown = new();
    private readonly PdfConverter _pdf = new();
    private readonly XlsxConverter _xlsx = new();
    private readonly CsvConverter _csv = new();
    private readonly DrawioConverter _drawio = new();

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
            SourceFormat.Mhtml => _mhtml.Convert(stream, path),
            SourceFormat.Docx => _docx.Convert(stream, path),
            SourceFormat.Markdown => _markdown.Convert(stream, path),
            SourceFormat.Pdf => _pdf.Convert(stream, path),
            SourceFormat.Xlsx => _xlsx.Convert(stream, path),
            SourceFormat.Csv => _csv.Convert(stream, path),
            SourceFormat.Drawio => _drawio.Convert(stream, path),
            _ => throw new NotSupportedException(
                $"Unrecognized document format: {path}"),
        };
    }
}
