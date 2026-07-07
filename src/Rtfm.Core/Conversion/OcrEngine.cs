using Microsoft.ML.OnnxRuntime;
using RapidOcrNet;
using SkiaSharp;

namespace Rtfm.Core.Conversion;

/// <summary>
/// The one shared OCR engine (Phases 16–17): PaddleOCR PP-OCRv5 via ONNX
/// Runtime, models shipped inside the RapidOcrNet package (no download).
/// Loaded lazily once per process (~13 MB of models) and used by both the PDF
/// route (embedded images) and the standalone image route. Null when the model
/// files aren't next to the binary — callers skip OCR rather than fail.
/// </summary>
internal static class OcrEngine
{
    private static readonly Lazy<RapidOcr?> Instance = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>OCRs a bitmap; null when the engine is unavailable, OCR fails, or nothing was read.</summary>
    public static string? DetectText(SKBitmap bitmap)
    {
        var ocr = Instance.Value;
        if (ocr is null)
        {
            return null;
        }

        // RapidOcrNet's mean-normalize only accepts Bgra8888 or Gray8, but
        // SKBitmap.Decode yields the platform-native color type — Bgra8888 on
        // Windows/Linux, Rgba8888 on macOS. Convert when it isn't acceptable,
        // else macOS throws on every image.
        SKBitmap? converted = null;
        if (bitmap.ColorType is not (SKColorType.Bgra8888 or SKColorType.Gray8))
        {
            converted = bitmap.Copy(SKColorType.Bgra8888);
        }

        try
        {
            var text = ocr.Detect(converted ?? bitmap, RapidOcrOptions.Default).StrRes?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            // OCR must never sink a document (Phases 16–17): a failure here
            // just means this image yields no text. The PDF route already
            // catches per-image; this makes the standalone-image route safe too.
            return null;
        }
        finally
        {
            converted?.Dispose();
        }
    }

    private static RapidOcr? Create()
    {
        try
        {
            // Explicit absolute paths: the package's default resolves relative
            // to the *working directory*, which for rtfm is wherever the user
            // happens to stand.
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
}
