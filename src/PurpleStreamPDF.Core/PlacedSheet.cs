using System.Text.Json.Serialization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PurpleStreamPDF.Core;

/// <summary>
/// Another drawing placed on the page, such as the architect's plan over the structure: a page of a PDF, or an image.
/// See-through, its white paper left out, optionally in one colour, and cut down to a rectangle. It stays upright as placed.
/// </summary>
public sealed class PlacedSheetMarkup : Markup
{
    public const double DefaultOpacity = 0.6;

    /// <summary>The placed drawing as a one-page PDF (an image is put on a page of its own, see <see cref="PlacedSheet"/>).</summary>
    [JsonIgnore] // never changes, and too big to compare on every redraw
    public byte[] Source { get; set; } = [];

    /// <summary>The source page's size as shown upright, in points.</summary>
    public double SourceWidth { get; set; }
    public double SourceHeight { get; set; }

    /// <summary>Where the whole source page's top-left and bottom-right corners sit (as seen), before clipping.</summary>
    public PdfPoint TopLeft { get; set; }
    public PdfPoint BottomRight { get; set; }

    /// <summary>The part shown, as fractions of the source page from its top-left: 0, 0, 1, 1 shows all of it.</summary>
    public double ClipLeft { get; set; }
    public double ClipTop { get; set; }
    public double ClipRight { get; set; } = 1;
    public double ClipBottom { get; set; } = 1;

    public double Opacity { get; set; } = DefaultOpacity;

    /// <summary>Draws every line in <see cref="Markup.Color"/>, to tell it apart from the drawing underneath.</summary>
    public bool Tinted { get; set; }

    public bool IsClipped => ClipLeft > 0 || ClipTop > 0 || ClipRight < 1 || ClipBottom < 1;

    public override void Map(Func<PdfPoint, PdfPoint> map)
    {
        TopLeft = map(TopLeft);
        BottomRight = map(BottomRight);
    }

    // shortcut: positions work in view space, so turning the page after placing a sheet stretches it; store a third corner to support that.
    /// <summary>A point given as fractions of the source page, on the page in view space.</summary>
    public ViewPoint FromSource(double fractionX, double fractionY, PageGeometry page)
    {
        var (topLeft, bottomRight) = (page.ToView(TopLeft), page.ToView(BottomRight));
        return new(topLeft.X + (bottomRight.X - topLeft.X) * fractionX, topLeft.Y + (bottomRight.Y - topLeft.Y) * fractionY);
    }

    /// <summary>The whole source page as placed, in view space.</summary>
    public ViewRect Placed(PageGeometry page) => ViewRect.Around([FromSource(0, 0, page), FromSource(1, 1, page)]);

    /// <summary>The part that shows, in view space.</summary>
    public ViewRect Visible(PageGeometry page) => ViewRect.Around([FromSource(ClipLeft, ClipTop, page), FromSource(ClipRight, ClipBottom, page)]);

    /// <summary>Shows only what is inside <paramref name="area"/> (view space) of what shows now. False, changing nothing, if that is nothing.</summary>
    public bool ClipTo(ViewRect area, PageGeometry page)
    {
        var (topLeft, bottomRight) = (page.ToView(TopLeft), page.ToView(BottomRight));
        double width = bottomRight.X - topLeft.X, height = bottomRight.Y - topLeft.Y;
        if (Math.Abs(width) < 1e-9 || Math.Abs(height) < 1e-9) return false;
        double x1 = (area.Left - topLeft.X) / width, x2 = (area.Right - topLeft.X) / width;
        double y1 = (area.Top - topLeft.Y) / height, y2 = (area.Bottom - topLeft.Y) / height;
        double left = Math.Max(ClipLeft, Math.Min(x1, x2)), right = Math.Min(ClipRight, Math.Max(x1, x2));
        double top = Math.Max(ClipTop, Math.Min(y1, y2)), bottom = Math.Min(ClipBottom, Math.Max(y1, y2));
        if (right - left < 1e-4 || bottom - top < 1e-4) return false;
        (ClipLeft, ClipTop, ClipRight, ClipBottom) = (left, top, right, bottom);
        return true;
    }
}

/// <summary>Makes and draws <see cref="PlacedSheetMarkup"/>s.</summary>
public static class PlacedSheet
{
    /// <summary>A page of a PDF on its own, so the sheet carries only that page.</summary>
    public static byte[] FromPdfPage(byte[] pdf, int pageIndex)
    {
        using var input = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        using var output = new PdfDocument();
        output.AddPage(input.Pages[pageIndex]);
        using var stream = new MemoryStream();
        output.Save(stream);
        return stream.ToArray();
    }

    /// <summary>An image (PNG, JPEG, BMP) on a page its size, at 96 dpi as Windows shows it.</summary>
    public static byte[] FromImage(byte[] image)
    {
        using var picture = XImage.FromStream(new MemoryStream(image));
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(picture.PixelWidth * 0.75);
        page.Height = XUnit.FromPoint(picture.PixelHeight * 0.75);
        using (var graphics = XGraphics.FromPdfPage(page)) graphics.DrawImage(picture, 0, 0, page.Width.Point, page.Height.Point);
        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }

    /// <summary>A sheet of <paramref name="onePagePdf"/> at its own size, its top-left corner at <paramref name="topLeft"/> (view space).</summary>
    public static PlacedSheetMarkup Create(byte[] onePagePdf, ViewPoint topLeft, PageGeometry page, double scale = 1)
    {
        var (width, height) = new PdfRenderer(onePagePdf).PageSize(0);
        return new PlacedSheetMarkup
        {
            Source = onePagePdf, SourceWidth = width, SourceHeight = height,
            TopLeft = page.ToPdf(topLeft), BottomRight = page.ToPdf(new ViewPoint(topLeft.X + width * scale, topLeft.Y + height * scale)),
        };
    }

    /// <summary>The sheet's line work where it sits on the page, in view space, for snapping to it.</summary>
    public static List<(ViewPoint A, ViewPoint B)> Lines(PlacedSheetMarkup sheet, PageGeometry page)
    {
        var sourcePage = MarkupFile.Load(sheet.Source).Pages[0];
        ViewPoint OnPage(PdfPoint point)
        {
            var onSource = sourcePage.ToView(point);
            return sheet.FromSource(onSource.X / sheet.SourceWidth, onSource.Y / sheet.SourceHeight, page);
        }
        return new PdfRenderer(sheet.Source).GetLineWork(0).Select(segment => (OnPage(segment.A), OnPage(segment.B))).ToList();
    }

    /// <summary>
    /// Draws the source page at <paramref name="scale"/> (1 = 72 dpi), its white paper see-through: lighter parts fade out,
    /// so the drawing underneath shows between the lines. With <paramref name="tint"/> ("#RRGGBB"), every line is in that colour.
    /// </summary>
    public static PageBitmap Render(byte[] source, double scale, string? tint)
    {
        var (pixels, width, height) = new PdfRenderer(source).Render(0, scale);
        var (tintRed, tintGreen, tintBlue) = tint is { Length: 7 }
            ? (Convert.ToByte(tint[1..3], 16), Convert.ToByte(tint[3..5], 16), Convert.ToByte(tint[5..7], 16)) : ((byte)0, (byte)0, (byte)0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            // The pixel as it looks on white paper, then as much of the white taken out as leaves that look ("colour to alpha").
            double alpha = pixels[i + 3] / 255.0;
            double blue = 255 - (255 - pixels[i]) * alpha, green = 255 - (255 - pixels[i + 1]) * alpha, red = 255 - (255 - pixels[i + 2]) * alpha;
            double ink;
            if (tint != null)
            {
                ink = 1 - (0.299 * red + 0.587 * green + 0.114 * blue) / 255;
                (red, green, blue) = (tintRed, tintGreen, tintBlue);
            }
            else
            {
                ink = 1 - Math.Min(red, Math.Min(green, blue)) / 255;
                if (ink > 0)
                {
                    red = 255 - (255 - red) / ink;
                    green = 255 - (255 - green) / ink;
                    blue = 255 - (255 - blue) / ink;
                }
            }
            pixels[i] = (byte)Math.Clamp(Math.Round(blue), 0, 255);
            pixels[i + 1] = (byte)Math.Clamp(Math.Round(green), 0, 255);
            pixels[i + 2] = (byte)Math.Clamp(Math.Round(red), 0, 255);
            pixels[i + 3] = (byte)Math.Clamp(Math.Round(ink * 255), 0, 255);
        }
        return new PageBitmap(pixels, width, height);
    }
}
