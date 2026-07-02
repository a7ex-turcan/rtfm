using System.Text;
using SkiaSharp;

namespace Rtfm.Core.Conversion;

/// <summary>
/// Converts a standalone image (PNG/JPEG) to markdown via OCR (Phase 17) —
/// architecture screenshots, exported diagrams, whiteboard photos. The
/// filename is the title; the OCR'd text is the body. An image with no
/// readable text still yields its title line, so it remains visible in
/// <c>list_sources</c> even though there is nothing else to find.
/// </summary>
public sealed class ImageConverter
{
    public ConversionResult Convert(Stream input, string sourcePath)
    {
        var title = Path.GetFileNameWithoutExtension(sourcePath);

        var sb = new StringBuilder();
        sb.Append("# ").Append(title).Append("\n\n");

        using var bitmap = SKBitmap.Decode(ReadAll(input));
        if (bitmap is not null && OcrEngine.DetectText(bitmap) is { } text)
        {
            sb.Append("[Image text] ").Append(text.ReplaceLineEndings("\n").Trim()).Append('\n');
        }

        return new ConversionResult(
            SourcePath: sourcePath,
            Format: SourceFormat.Image,
            Markdown: sb.ToString().TrimEnd(),
            Title: title,
            SourceModifiedAt: null); // no reliable embedded date — mtime fallback
    }

    private static byte[] ReadAll(Stream input)
    {
        if (input is MemoryStream ms && ms.TryGetBuffer(out var buffer) && buffer.Offset == 0 && buffer.Count == ms.Length)
        {
            return buffer.Array!;
        }

        using var copy = new MemoryStream();
        input.CopyTo(copy);
        return copy.ToArray();
    }
}
