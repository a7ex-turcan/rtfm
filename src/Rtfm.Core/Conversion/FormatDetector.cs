using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Rtfm.Core.Conversion;

/// <summary>
/// Determines a document's <see cref="SourceFormat"/> by peeking at its
/// leading bytes, falling back to the extension. Content wins over extension
/// because Confluence exports lie: a <c>.doc</c> file is really MHTML.
/// Since Phase 9 the zip magic is ambiguous — both docx and xlsx are OOXML
/// zips — so zips are disambiguated by their container folder
/// (<c>word/</c> vs <c>xl/</c>).
/// </summary>
public static partial class FormatDetector
{
    private const int PeekBytes = 512;

    /// <summary>
    /// Email needs a longer look than the other formats: a real message's
    /// <c>Received:</c> chain routinely runs past 512 bytes before
    /// <c>Subject:</c> appears. Only the email rule reads this far — widening
    /// the window for every rule would let a stray <c>&lt;html</c> deep inside
    /// a CSV win over its extension.
    /// </summary>
    private const int HeaderPeekBytes = 8192;

    /// <summary>
    /// Sniffs <paramref name="stream"/> without consuming it — the stream
    /// position is restored before returning, so the caller can convert next.
    /// </summary>
    public static SourceFormat Detect(string path, Stream stream)
    {
        var origin = stream.Position;
        var buffer = new byte[HeaderPeekBytes];
        // ReadAtLeast, not Read: a single Read may return short even with more
        // to come, which would truncate the header window arbitrarily.
        var total = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        stream.Position = origin;

        var read = Math.Min(total, PeekBytes);
        var head = buffer.AsSpan(0, read);

        // PDF: "%PDF".
        if (read >= 4 && head[0] == 0x25 && head[1] == 0x50 && head[2] == 0x44 && head[3] == 0x46)
        {
            return SourceFormat.Pdf;
        }

        // PNG: \x89PNG.  JPEG: FF D8 FF.
        if (read >= 4 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
        {
            return SourceFormat.Image;
        }

        if (read >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            return SourceFormat.Image;
        }

        // OOXML (docx *and* xlsx) is a zip archive: "PK\x03\x04".
        if (read >= 4 && head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04)
        {
            return DetectOoxml(stream, origin);
        }

        var text = Encoding.ASCII.GetString(head);

        // Email — MUST be tested before MHTML, which it would otherwise match:
        // MHTML *is* a MIME email container, so a .eml would silently route
        // through the Confluence converter and convert as a malformed page.
        // What separates them is conversational headers — a Confluence export
        // has no recipients — so require From: and To: together. Anything
        // ambiguous falls through to MHTML, i.e. the pre-Phase-24 behavior.
        var headerText = Encoding.ASCII.GetString(buffer, 0, total);
        var ext = Path.GetExtension(path);

        if (ext.Equals(".eml", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mbox", StringComparison.OrdinalIgnoreCase)
            || headerText.StartsWith("From ", StringComparison.Ordinal)
            || (FromHeader().IsMatch(headerText)
                && ToHeader().IsMatch(headerText)
                && SubjectOrDateHeader().IsMatch(headerText)))
        {
            return SourceFormat.Email;
        }

        // MHTML: MIME headers appear near the top of the file.
        if (text.Contains("MIME-Version:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("multipart/related", StringComparison.OrdinalIgnoreCase))
        {
            return SourceFormat.Mhtml;
        }

        // draw.io: an mxfile XML root (content wins — covers .xml files too).
        if (text.Contains("<mxfile", StringComparison.OrdinalIgnoreCase))
        {
            return SourceFormat.Drawio;
        }

        // Bare HTML: Jira's "Export to Word" .doc (and .html/.htm) — not MIME-
        // wrapped like Confluence's MHTML, so it's caught only after the MHTML
        // check above. The doctype/root tag sits at the very top of the file.
        if (text.Contains("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<html", StringComparison.OrdinalIgnoreCase))
        {
            return SourceFormat.Html;
        }

        if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
        {
            return SourceFormat.Markdown;
        }

        if (ext.Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return SourceFormat.Csv;
        }

        if (ext.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".htm", StringComparison.OrdinalIgnoreCase))
        {
            return SourceFormat.Html;
        }

        if (ext.Equals(".drawio", StringComparison.OrdinalIgnoreCase))
        {
            return SourceFormat.Drawio;
        }

        if (ext.Equals(".sql", StringComparison.OrdinalIgnoreCase))
        {
            return SourceFormat.Sql;
        }

        if (ext.Equals(".rtfmdb", StringComparison.OrdinalIgnoreCase))
        {
            return SourceFormat.Database;
        }

        return SourceFormat.Unknown;
    }

    /// <summary>
    /// Zip magic alone can't tell docx from xlsx — look at the container
    /// content: <c>xl/</c> means a workbook, <c>word/</c> a document. An
    /// unreadable or markerless zip defaults to docx (the pre-Phase-9
    /// behavior; Mammoth then produces the real error if it isn't one).
    /// </summary>
    private static SourceFormat DetectOoxml(Stream stream, long origin)
    {
        if (!stream.CanSeek)
        {
            return SourceFormat.Docx;
        }

        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                {
                    return SourceFormat.Xlsx;
                }

                if (entry.FullName.StartsWith("word/", StringComparison.OrdinalIgnoreCase))
                {
                    return SourceFormat.Docx;
                }
            }

            return SourceFormat.Docx;
        }
        catch (InvalidDataException)
        {
            return SourceFormat.Docx;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    [GeneratedRegex(@"^From:\s*\S", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex FromHeader();

    [GeneratedRegex(@"^(To|Cc):\s*\S", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ToHeader();

    [GeneratedRegex(@"^(Subject|Date|Message-ID):\s*\S", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SubjectOrDateHeader();
}
