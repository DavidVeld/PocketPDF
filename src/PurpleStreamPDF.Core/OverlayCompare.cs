namespace PurpleStreamPDF.Core;

/// <summary>A rendered page: BGRA pixels as PDFium returns them (transparent where nothing is drawn).</summary>
public readonly record struct PageBitmap(byte[] BgraPixels, int Width, int Height);

/// <summary>
/// Where a point on the new revision sits on the old one: old = new × Scale + Offset.
/// Lines up an old revision exported at a different offset or scale. No rotation: sheets come out of CAD square.
/// </summary>
public readonly record struct PageAlignment(double Scale, double OffsetX, double OffsetY)
{
    public static readonly PageAlignment None = new(1, 0, 0);

    /// <summary>From two matching points picked on each revision (in the same units, such as view points).</summary>
    public static PageAlignment FromPoints((double X, double Y) old1, (double X, double Y) old2, (double X, double Y) new1, (double X, double Y) new2)
    {
        double newDistance = Math.Sqrt(Math.Pow(new2.X - new1.X, 2) + Math.Pow(new2.Y - new1.Y, 2));
        double oldDistance = Math.Sqrt(Math.Pow(old2.X - old1.X, 2) + Math.Pow(old2.Y - old1.Y, 2));
        if (newDistance < 1e-6 || oldDistance < 1e-6) throw new ArgumentException("The two points on each revision must be apart.");
        double scale = oldDistance / newDistance;
        // Match the midpoints, so a small click error on either point is split between both.
        return new(scale,
            (old1.X + old2.X) / 2 - scale * (new1.X + new2.X) / 2,
            (old1.Y + old2.Y) / 2 - scale * (new1.Y + new2.Y) / 2);
    }

    /// <summary>The same alignment for bitmaps rendered at this many pixels per unit.</summary>
    public PageAlignment InPixels(double pixelsPerUnit) => new(Scale, OffsetX * pixelsPerUnit, OffsetY * pixelsPerUnit);
}

/// <summary>
/// Overlays two revisions of a page: lines only in the old one show red, lines only in the new one blue,
/// lines in both black. Works per pixel on the rendered pages, because CAD exports shuffle their vector data between revisions.
/// </summary>
public static class OverlayCompare
{
    /// <summary>
    /// Mixes the two pages into one opaque BGRA image the size of <paramref name="newPage"/>, both aligned top-left.
    /// A hidden revision counts as blank paper, so the other one shows alone in its colour.
    /// </summary>
    public static byte[] Combine(PageBitmap oldPage, PageBitmap newPage, bool showOld, bool showNew) =>
        Combine(oldPage, newPage, PageAlignment.None, showOld, showNew);

    /// <summary>As above, with the old page moved and scaled by <paramref name="alignmentInPixels"/> first.</summary>
    public static byte[] Combine(PageBitmap oldPage, PageBitmap newPage, PageAlignment alignmentInPixels, bool showOld, bool showNew)
    {
        bool isAligned = alignmentInPixels != PageAlignment.None;
        var result = new byte[newPage.Width * newPage.Height * 4];
        for (int y = 0; y < newPage.Height; y++)
        for (int x = 0; x < newPage.Width; x++)
        {
            // Lightness 0 (ink) to 255 (paper). Using it as colour channels keeps anti-aliased edges smooth:
            // red = new lightness, blue = old lightness, green = the darker of both.
            int oldLightness = !showOld ? 255
                : isAligned ? SmoothLightness(oldPage, alignmentInPixels, x, y)
                : Lightness(oldPage, x, y);
            int newLightness = showNew ? Lightness(newPage, x, y) : 255;
            int offset = (y * newPage.Width + x) * 4;
            result[offset] = (byte)oldLightness;                               // blue
            result[offset + 1] = (byte)Math.Min(oldLightness, newLightness); // green
            result[offset + 2] = (byte)newLightness;                           // red
            result[offset + 3] = 255;
        }
        return result;
    }

    /// <summary>
    /// Lightness of the old page under new pixel (x, y), blended from its four nearest pixels:
    /// an offset of a fraction of a pixel would otherwise show as red and blue fringes along every line.
    /// </summary>
    static int SmoothLightness(PageBitmap page, PageAlignment alignment, int x, int y)
    {
        // Pixel centres sit at +0.5.
        double oldX = (x + 0.5) * alignment.Scale + alignment.OffsetX - 0.5;
        double oldY = (y + 0.5) * alignment.Scale + alignment.OffsetY - 0.5;
        int left = (int)Math.Floor(oldX), top = (int)Math.Floor(oldY);
        double fractionX = oldX - left, fractionY = oldY - top;
        double topRow = Lightness(page, left, top) * (1 - fractionX) + Lightness(page, left + 1, top) * fractionX;
        double bottomRow = Lightness(page, left, top + 1) * (1 - fractionX) + Lightness(page, left + 1, top + 1) * fractionX;
        return (int)Math.Round(topRow * (1 - fractionY) + bottomRow * fractionY);
    }

    /// <summary>Lightness of a pixel as seen on white paper. Outside the page counts as paper.</summary>
    static int Lightness(PageBitmap page, int x, int y)
    {
        if (x < 0 || y < 0 || x >= page.Width || y >= page.Height) return 255;
        int offset = (y * page.Width + x) * 4;
        var pixels = page.BgraPixels;
        int alpha = pixels[offset + 3];
        int colourAverage = (pixels[offset] + pixels[offset + 1] + pixels[offset + 2]) / 3;
        return (colourAverage * alpha + 255 * (255 - alpha)) / 255;
    }
}
