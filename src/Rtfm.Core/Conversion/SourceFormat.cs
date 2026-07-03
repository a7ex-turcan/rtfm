namespace Rtfm.Core.Conversion;

/// <summary>
/// Input formats RTFM can convert to markdown. Detected by sniffing content,
/// not just the file extension — Confluence "Export to Word" files carry a
/// <c>.doc</c> extension but are actually MHTML (see <see cref="FormatDetector"/>).
/// </summary>
public enum SourceFormat
{
    Unknown = 0,

    /// <summary>MIME multipart/related HTML — Confluence's "Export to Word" output (.doc).</summary>
    Mhtml,

    /// <summary>Genuine Word / Open XML (.docx). Phase 1b.</summary>
    Docx,

    /// <summary>Plain Markdown (.md). Phase 1c.</summary>
    Markdown,

    /// <summary>PDF (%PDF magic). Phase 9.</summary>
    Pdf,

    /// <summary>Excel / Open XML workbook (.xlsx — zip with an xl/ folder). Phase 9.</summary>
    Xlsx,

    /// <summary>Comma-separated values (.csv, by extension). Phase 9.</summary>
    Csv,

    /// <summary>draw.io diagram (mxfile XML, .drawio). Phase 15.</summary>
    Drawio,

    /// <summary>Standalone PNG/JPEG image, OCR'd (Phase 17).</summary>
    Image,

    /// <summary>SQL schema file (.sql), structurally parsed (Phase 18).</summary>
    Sql,

    /// <summary>Live database schema via a .rtfmdb connector descriptor (Phase 20).</summary>
    Database,
}
