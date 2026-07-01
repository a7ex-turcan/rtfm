using System.Text;

namespace Rtfm.Core.Conversion;

/// <summary>
/// Determines a document's <see cref="DocumentFormat"/> by peeking at its
/// leading bytes, falling back to the extension. Content wins over extension
/// because Confluence exports lie: a <c>.doc</c> file is really MHTML.
/// </summary>
public static class FormatDetector
{
    private const int PeekBytes = 512;

    /// <summary>
    /// Sniffs <paramref name="stream"/> without consuming it — the stream
    /// position is restored before returning, so the caller can convert next.
    /// </summary>
    public static DocumentFormat Detect(string path, Stream stream)
    {
        var origin = stream.Position;
        var buffer = new byte[PeekBytes];
        var read = stream.Read(buffer, 0, buffer.Length);
        stream.Position = origin;

        var head = buffer.AsSpan(0, read);

        // docx (and any OOXML) is a zip archive: "PK\x03\x04".
        if (read >= 4 && head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04)
        {
            return DocumentFormat.Docx;
        }

        // MHTML: MIME headers appear near the top of the file.
        var text = Encoding.ASCII.GetString(head);
        if (text.Contains("MIME-Version:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("multipart/related", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentFormat.Mhtml;
        }

        var ext = Path.GetExtension(path);
        if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentFormat.Markdown;
        }

        return DocumentFormat.Unknown;
    }
}
