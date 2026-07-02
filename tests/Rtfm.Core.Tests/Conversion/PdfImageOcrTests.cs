using Rtfm.Core.Conversion;
using SkiaSharp;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Rtfm.Core.Tests.Conversion;

/// <summary>
/// Phase 16: diagrams embedded as raster images in PDFs. The OCR models ship
/// inside the RapidOcrNet package (no network), so this stays a legal unit
/// test — just a slower one (~1s model load, once per test run).
/// </summary>
public class PdfImageOcrTests
{
    [Fact]
    public void Embedded_raster_diagram_text_is_ocred_into_the_markdown()
    {
        // A diagram-ish image: two labeled boxes.
        var info = new SKImageInfo(800, 240);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        using var stroke = new SKPaint { Color = SKColors.Black, IsStroke = true, StrokeWidth = 2 };
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 30);
        canvas.DrawRect(30, 60, 300, 90, stroke);
        canvas.DrawText("Billing Service", 60, 115, SKTextAlign.Left, font, ink);
        canvas.DrawRect(430, 60, 330, 90, stroke);
        canvas.DrawText("Tenant Database", 460, 115, SKTextAlign.Left, font, ink);

        using var snapshot = surface.Snapshot();
        using var jpeg = snapshot.Encode(SKEncodedImageFormat.Jpeg, 92);

        var builder = new PdfDocumentBuilder();
        var pdfFont = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText("Deployment Overview", 18, new PdfPoint(50, 800), pdfFont);
        page.AddJpeg(jpeg.ToArray(), new PdfRectangle(50, 400, 550, 550));

        using var stream = new MemoryStream(builder.Build());
        var result = new PdfConverter().Convert(stream, "deployment.pdf");

        Assert.Contains("Deployment Overview", result.Markdown); // vector text still extracted
        Assert.Contains("[Image text]", result.Markdown);
        Assert.Contains("Billing Service", result.Markdown);
        Assert.Contains("Tenant Database", result.Markdown);
    }

    [Fact]
    public void Tiny_images_are_skipped_without_failing_conversion()
    {
        // A 40px icon — under the size floor; conversion must not OCR or throw.
        var info = new SKImageInfo(40, 40);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Red);
        using var snapshot = surface.Snapshot();
        using var jpeg = snapshot.Encode(SKEncodedImageFormat.Jpeg, 90);

        var builder = new PdfDocumentBuilder();
        var pdfFont = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText("Just an icon here.", 12, new PdfPoint(50, 800), pdfFont);
        page.AddJpeg(jpeg.ToArray(), new PdfRectangle(50, 700, 90, 740));

        using var stream = new MemoryStream(builder.Build());
        var result = new PdfConverter().Convert(stream, "icon.pdf");

        Assert.Contains("Just an icon here.", result.Markdown);
        Assert.DoesNotContain("[Image text]", result.Markdown);
    }
}
