using Rtfm.Core.Conversion;
using SkiaSharp;

namespace Rtfm.Core.Tests.Conversion;

public class ImageConverterTests
{
    [Theory]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    public void Ocrs_labeled_images_into_markdown(SKEncodedImageFormat format)
    {
        using var stream = new MemoryStream(DrawLabeledImage("Payment Gateway", format));

        var result = new ImageConverter().Convert(stream, $"arch-shot.{format.ToString().ToLowerInvariant()}");

        Assert.Equal(SourceFormat.Image, result.Format);
        Assert.Equal("arch-shot", result.Title);
        Assert.Contains("# arch-shot", result.Markdown);
        Assert.Contains("[Image text]", result.Markdown);
        Assert.Contains("Payment Gateway", result.Markdown);
        Assert.Null(result.SourceModifiedAt); // mtime fallback
    }

    [Fact]
    public void Textless_image_still_yields_its_title()
    {
        var info = new SKImageInfo(200, 200);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.SteelBlue); // no text at all
        using var snapshot = surface.Snapshot();
        using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(png.ToArray());

        var result = new ImageConverter().Convert(stream, "blank.png");

        Assert.Contains("# blank", result.Markdown);
        Assert.DoesNotContain("[Image text]", result.Markdown);
    }

    [Fact]
    public void Ocr_reads_an_rgba8888_bitmap_the_shape_macos_decodes_to()
    {
        // SKBitmap.Decode yields Rgba8888 on macOS (Bgra8888 on Windows/Linux),
        // which RapidOcrNet rejected — regression guard for the CI macOS break.
        // Force Rgba8888 here so the OCR color-type contract holds on every OS.
        using var decoded = SKBitmap.Decode(DrawLabeledImage("Rgba Channel Order", SKEncodedImageFormat.Png));
        using var rgba = decoded.Copy(SKColorType.Rgba8888);

        Assert.Equal(SKColorType.Rgba8888, rgba.ColorType);
        var text = OcrEngine.DetectText(rgba); // must not throw on the non-Bgra input
        Assert.NotNull(text);
        Assert.Contains("Rgba", text);
    }

    [Fact]
    public void Detector_recognizes_png_and_jpeg_magic()
    {
        using var png = new MemoryStream(DrawLabeledImage("x", SKEncodedImageFormat.Png));
        Assert.Equal(SourceFormat.Image, FormatDetector.Detect("mislabeled.dat", png)); // content wins

        using var jpeg = new MemoryStream(DrawLabeledImage("x", SKEncodedImageFormat.Jpeg));
        Assert.Equal(SourceFormat.Image, FormatDetector.Detect("photo.jpg", jpeg));
    }

    private static byte[] DrawLabeledImage(string label, SKEncodedImageFormat format)
    {
        var info = new SKImageInfo(700, 160);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 34);
        canvas.DrawText(label, 40, 95, SKTextAlign.Left, font, ink);
        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(format, 92);
        return encoded.ToArray();
    }
}
