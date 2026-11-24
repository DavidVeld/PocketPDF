using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace PocketPDF.Core;

/// <summary>One page of an overlay.</summary>
/// <param name="Png">The overlay as a PNG (encoded by the app, so Core stays free of WPF).</param>
/// <param name="Width">The drawing's width as shown, in points.</param>
/// <param name="Height">The drawing's height as shown, in points.</param>
/// <param name="PageNumber">The drawing's page number, for the legend.</param>
public sealed record OverlayPage(byte[] Png, double Width, double Height, int PageNumber);

/// <summary>
/// Writes an overlay as a PDF to print, send or mark up: each page is the overlay image at the drawing's size
/// (so view positions on the drawing are the same on the overlay), with a legend strip below it.
/// </summary>
public static class OverlayExport
{
    public static void Save(string targetPath, IReadOnlyList<OverlayPage> pages, string oldRevisionName, string newRevisionName)
    {
        var bytes = Create(pages, oldRevisionName, newRevisionName);
        // Write next to the target first, so a failed save never leaves half a file behind.
        string temporaryPath = targetPath + ".saving";
        File.WriteAllBytes(temporaryPath, bytes);
        File.Move(temporaryPath, targetPath, overwrite: true);
    }

    /// <summary>The overlay PDF in memory, for opening it without saving it first.</summary>
    public static byte[] Create(IReadOnlyList<OverlayPage> pages, string oldRevisionName, string newRevisionName)
    {
        // PDFsharp's core build needs to be told where fonts come from; Windows fonts are always there for this app.
        if (GlobalFontSettings.FontResolver == null) GlobalFontSettings.UseWindowsFontsUnderWindows = true;

        using var document = new PdfDocument();
        document.Info.Title = $"Overlay: {oldRevisionName} vs {newRevisionName}{(pages.Count == 1 ? $", page {pages[0].PageNumber}" : "")}";
        foreach (var overlay in pages) AddPage(document, overlay, oldRevisionName, newRevisionName);
        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }

    static void AddPage(PdfDocument document, OverlayPage overlay, string oldRevisionName, string newRevisionName)
    {
        var (overlayPng, pageWidth, pageHeight, pageNumber) = overlay;
        // Scale the legend with the sheet, so it is still readable when an A1 or A0 sheet is printed on A3.
        double fontSize = Math.Max(9, Math.Max(pageWidth, pageHeight) / 110);
        double legendHeight = fontSize * 2.5;

        var page = document.AddPage();
        page.Width = XUnit.FromPoint(pageWidth);
        page.Height = XUnit.FromPoint(pageHeight + legendHeight);
        using (var graphics = XGraphics.FromPdfPage(page))
        {
            using var image = XImage.FromStream(new MemoryStream(overlayPng));
            graphics.DrawImage(image, 0, 0, pageWidth, pageHeight);

            var font = new XFont("Arial", fontSize);
            double x = fontSize, baseline = pageHeight + legendHeight / 2 + fontSize * 0.35;
            void Entry(XColor colour, string text)
            {
                graphics.DrawRectangle(new XSolidBrush(colour), x, baseline - fontSize * 0.8, fontSize, fontSize);
                x += fontSize * 1.4;
                graphics.DrawString(text, font, XBrushes.Black, x, baseline);
                x += graphics.MeasureString(text, font).Width + fontSize * 2;
            }
            Entry(XColor.FromArgb(255, 0, 0), $"Old: {oldRevisionName}");
            Entry(XColor.FromArgb(0, 0, 255), $"New: {newRevisionName}");
            Entry(XColors.Black, "Unchanged");
            string pageAndDate = $"Page {pageNumber}   Compared {DateTime.Now:yyyy-MM-dd}";
            graphics.DrawString(pageAndDate, font, XBrushes.Black, pageWidth - fontSize - graphics.MeasureString(pageAndDate, font).Width, baseline);
        }
    }
}
