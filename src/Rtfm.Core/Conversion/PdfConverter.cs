using System.Globalization;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using RapidOcrNet;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Rtfm.Core.Conversion;

/// <summary>
/// Converts a PDF to markdown (§2.5, Phase 9 route). PDFs carry no real heading
/// semantics, so headings are *heuristic*: the dominant letter size is taken as
/// body text, and short lines set noticeably larger (or bold at body size)
/// become headings — larger size ⇒ higher level. Expect flatter breadcrumbs
/// than MHTML/docx; when a document defeats the heuristics entirely the output
/// degrades to plain paragraphs and the chunker falls back to size-window
/// splitting, which is the documented plan. Tables are not reconstructed —
/// their text is extracted in reading order (imperfect but retrievable).
/// </summary>
public sealed class PdfConverter
{
    /// <summary>A line must be at least this much larger than body text to read as a heading.</summary>
    private const double HeadingSizeFactor = 1.15;

    private const int MaxHeadingWords = 15;

    public ConversionResult Convert(Stream input, string sourcePath)
    {
        using var document = PdfDocument.Open(ReadAll(input));

        var pages = document.GetPages()
            .Select(p => (Lines: BuildLines(p), ImageTexts: ExtractImageTexts(p)))
            .ToList();
        var bodySize = MedianLetterSize(pages.SelectMany(p => p.Lines));
        var headingLevels = AssignHeadingLevels(pages.SelectMany(p => p.Lines), bodySize);

        var markdown = RenderMarkdown(pages, bodySize, headingLevels);
        var title = FirstNonEmpty(document.Information?.Title)
            ?? FirstTopHeading(pages.SelectMany(p => p.Lines), headingLevels, bodySize);

        return new ConversionResult(
            SourcePath: sourcePath,
            Format: SourceFormat.Pdf,
            Markdown: markdown,
            Title: title,
            SourceModifiedAt: TryParsePdfDate(document.Information?.ModifiedDate)
                ?? TryParsePdfDate(document.Information?.CreationDate));
    }

    /// <summary>One visual line of text: words joined, with the size/bold facts the heuristics need.</summary>
    internal sealed record PdfLine(string Text, double FontSize, bool Bold, int WordCount, double Baseline);

    private static IReadOnlyList<PdfLine> BuildLines(Page page)
    {
        var lines = new List<PdfLine>();

        // Group words into lines by baseline (bottom Y), reading top-down.
        foreach (var lineGroup in page.GetWords()
                     .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                     .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 2.0)) // ~2pt baseline tolerance
                     .OrderByDescending(g => g.Key))
        {
            var words = lineGroup.OrderBy(w => w.BoundingBox.Left).ToList();
            var letters = words.SelectMany(w => w.Letters).ToList();
            if (letters.Count == 0)
            {
                continue;
            }

            lines.Add(new PdfLine(
                Text: string.Join(' ', words.Select(w => w.Text)),
                FontSize: Math.Round(letters.Max(l => l.PointSize) * 2) / 2, // ½pt buckets
                Bold: letters.All(l => l.FontDetails.IsBold),
                WordCount: words.Count,
                Baseline: words[0].BoundingBox.Bottom));
        }

        return lines;
    }

    private static double MedianLetterSize(IEnumerable<PdfLine> lines)
    {
        // Weight by word count so one big title doesn't skew the body size.
        var sizes = lines.SelectMany(l => Enumerable.Repeat(l.FontSize, l.WordCount)).OrderBy(s => s).ToList();
        return sizes.Count == 0 ? 0 : sizes[sizes.Count / 2];
    }

    private static bool IsHeading(PdfLine line, double bodySize)
        => line.WordCount <= MaxHeadingWords
           && bodySize > 0
           && (line.FontSize >= bodySize * HeadingSizeFactor
               || (line.Bold && line.FontSize >= bodySize && !line.Text.EndsWith('.')));

    /// <summary>Distinct heading sizes, largest first → #, ##, ### (bold-at-body headings rank below all size tiers).</summary>
    private static Dictionary<double, int> AssignHeadingLevels(IEnumerable<PdfLine> lines, double bodySize)
    {
        var sizes = lines
            .Where(l => IsHeading(l, bodySize) && l.FontSize >= bodySize * HeadingSizeFactor)
            .Select(l => l.FontSize)
            .Distinct()
            .OrderByDescending(s => s)
            .ToList();

        return sizes.Select((size, i) => (size, level: Math.Min(i + 1, 3)))
            .ToDictionary(x => x.size, x => x.level);
    }

    private static string RenderMarkdown(
        IReadOnlyList<(IReadOnlyList<PdfLine> Lines, IReadOnlyList<string> ImageTexts)> pages,
        double bodySize,
        Dictionary<double, int> headingLevels)
    {
        var sb = new StringBuilder();
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                sb.Append(string.Join(' ', paragraph)).Append("\n\n");
                paragraph.Clear();
            }
        }

        foreach (var (page, imageTexts) in pages)
        {
            var gaps = page.Zip(page.Skip(1), (a, b) => a.Baseline - b.Baseline).Where(g => g > 0).OrderBy(g => g).ToList();
            var typicalGap = gaps.Count == 0 ? 0 : gaps[gaps.Count / 2];

            PdfLine? previous = null;
            foreach (var line in page)
            {
                if (IsHeading(line, bodySize))
                {
                    FlushParagraph();
                    var level = headingLevels.TryGetValue(line.FontSize, out var l)
                        ? l
                        : Math.Min(headingLevels.Count + 1, 4); // bold-at-body tier
                    sb.Append(new string('#', level)).Append(' ').Append(line.Text).Append("\n\n");
                }
                else
                {
                    // A gap clearly larger than the page's line spacing separates paragraphs.
                    if (previous is not null && typicalGap > 0 && previous.Baseline - line.Baseline > typicalGap * 1.6)
                    {
                        FlushParagraph();
                    }

                    paragraph.Add(line.Text);
                }

                previous = line;
            }

            FlushParagraph(); // page boundary always ends the paragraph

            // OCR'd text from the page's embedded images (Phase 16) — one
            // paragraph per image so a diagram's labels stay together.
            foreach (var text in imageTexts)
            {
                sb.Append("[Image text] ").Append(text.ReplaceLineEndings("\n").Trim()).Append("\n\n");
            }
        }

        return sb.ToString().TrimEnd();
    }

    // ---- Phase 16: OCR for embedded images (diagrams saved as pictures) ----

    /// <summary>Ignore icons/logos: the shorter side must reach this many pixels.</summary>
    private const int MinImageDimension = 80;

    private const int MaxImagesPerPage = 8;

    /// <summary>
    /// One shared OCR engine per process (models load once, ~15 MB). Ships
    /// inside the RapidOcrNet package — no download, no network. Null when the
    /// model files aren't next to the binary (never expected; OCR is then
    /// skipped rather than failing conversion).
    /// </summary>
    private static readonly Lazy<RapidOcr?> OcrEngine = new(CreateOcrEngine, LazyThreadSafetyMode.ExecutionAndPublication);

    private static RapidOcr? CreateOcrEngine()
    {
        try
        {
            // Explicit absolute paths: the package's own default resolves
            // relative to the *working directory*, which for rtfm is wherever
            // the user happens to stand.
            var dir = Path.Combine(AppContext.BaseDirectory, "models", "v5");
            var ocr = new RapidOcr();
            ocr.InitModels(
                Path.Combine(dir, "ch_PP-OCRv5_mobile_det.onnx"),
                Path.Combine(dir, "ch_ppocr_mobile_v2.0_cls_infer.onnx"),
                Path.Combine(dir, "latin_PP-OCRv5_rec_mobile_infer.onnx"),
                Path.Combine(dir, "ppocrv5_latin_dict.txt"),
                new SessionOptions());
            return ocr;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ExtractImageTexts(Page page)
    {
        var texts = new List<string>();
        var ocr = OcrEngine.Value;
        if (ocr is null)
        {
            return texts;
        }

        var used = 0;
        foreach (var image in page.GetImages())
        {
            if (used >= MaxImagesPerPage)
            {
                break;
            }

            if (Math.Min(image.WidthInSamples, image.HeightInSamples) < MinImageDimension)
            {
                continue;
            }

            try
            {
                using var bitmap = DecodeImage(image);
                if (bitmap is null)
                {
                    continue;
                }

                used++;
                var result = ocr.Detect(bitmap, RapidOcrOptions.Default);
                var text = result.StrRes?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    texts.Add(text);
                }
            }
            catch
            {
                // One unreadable image never sinks the document.
            }
        }

        return texts;
    }

    /// <summary>PNG re-encode covers most PDF filters; raw bytes cover DCT (JPEG), which SkiaSharp decodes directly.</summary>
    private static SKBitmap? DecodeImage(IPdfImage image)
    {
        if (image.TryGetPng(out var png))
        {
            return SKBitmap.Decode(png);
        }

        return SKBitmap.Decode(image.RawBytes.ToArray());
    }

    private static string? FirstTopHeading(IEnumerable<PdfLine> lines, Dictionary<double, int> levels, double bodySize)
        => lines.FirstOrDefault(l => IsHeading(l, bodySize) && levels.TryGetValue(l.FontSize, out var lvl) && lvl == 1)?.Text;

    private static string? FirstNonEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Parses PDF date strings like <c>D:20260702134500+02'00'</c> (tolerant of truncation). Internal for tests.</summary>
    internal static DateTimeOffset? TryParsePdfDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var s = value.Trim();
        if (s.StartsWith("D:", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }

        var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length < 8)
        {
            return null;
        }

        try
        {
            var year = int.Parse(digits[..4], CultureInfo.InvariantCulture);
            var month = digits.Length >= 6 ? int.Parse(digits[4..6], CultureInfo.InvariantCulture) : 1;
            var day = digits.Length >= 8 ? int.Parse(digits[6..8], CultureInfo.InvariantCulture) : 1;
            var hour = digits.Length >= 10 ? int.Parse(digits[8..10], CultureInfo.InvariantCulture) : 0;
            var minute = digits.Length >= 12 ? int.Parse(digits[10..12], CultureInfo.InvariantCulture) : 0;
            var second = digits.Length >= 14 ? int.Parse(digits[12..14], CultureInfo.InvariantCulture) : 0;

            var offset = TimeSpan.Zero;
            var rest = s[digits.Length..];
            if (rest.Length >= 3 && (rest[0] == '+' || rest[0] == '-'))
            {
                var hours = int.Parse(rest[1..3], CultureInfo.InvariantCulture);
                var minutes = rest.Length >= 6 && char.IsDigit(rest[4]) && char.IsDigit(rest[5])
                    ? int.Parse(rest[4..6], CultureInfo.InvariantCulture)
                    : 0;
                offset = new TimeSpan(hours, minutes, 0);
                if (rest[0] == '-')
                {
                    offset = offset.Negate();
                }
            }

            return new DateTimeOffset(year, month, day, hour, minute, second, offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
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
