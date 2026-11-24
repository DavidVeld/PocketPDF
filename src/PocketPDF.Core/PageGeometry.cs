using PdfSharp.Pdf;

namespace PocketPDF.Core;

/// <summary>
/// The visible area of a page (its crop box, in PDF points) and its /Rotate value.
/// Converts between PDF space and view space.
/// </summary>
public sealed record PageGeometry(double Left, double Bottom, double Right, double Top, int Rotation)
{
    double Width => Right - Left;
    double Height => Top - Bottom;
    bool IsSideways => Rotation is 90 or 270;

    public double ViewWidth => IsSideways ? Height : Width;
    public double ViewHeight => IsSideways ? Width : Height;

    public ViewPoint ToView(PdfPoint point)
    {
        // Unrotated position measured from the top-left corner.
        double across = point.X - Left, down = Top - point.Y;
        return Rotation switch
        {
            90 => new(Height - down, across),
            180 => new(Width - across, Height - down),
            270 => new(down, Width - across),
            _ => new(across, down),
        };
    }

    public PdfPoint ToPdf(ViewPoint point)
    {
        var (across, down) = Rotation switch
        {
            90 => (point.Y, Height - point.X),
            180 => (Width - point.X, Height - point.Y),
            270 => (Width - point.Y, point.X),
            _ => (point.X, point.Y),
        };
        return new(Left + across, Top - down);
    }

    public PdfRect ToPdf(ViewRect rect) => PdfRect.Around(rect.Corners().Select(ToPdf));

    public ViewRect ToView(PdfRect rect) => ViewRect.Around(
        [ToView(new PdfPoint(rect.Left, rect.Bottom)), ToView(new PdfPoint(rect.Right, rect.Top)),
         ToView(new PdfPoint(rect.Left, rect.Top)), ToView(new PdfPoint(rect.Right, rect.Bottom))]);

    /// <summary>The matrix (a b c d e f) that maps view space to PDF space, for the PDF "cm" operator.</summary>
    public double[] ViewToPdfMatrix()
    {
        var origin = ToPdf(new ViewPoint(0, 0));
        var unitX = ToPdf(new ViewPoint(1, 0));
        var unitY = ToPdf(new ViewPoint(0, 1));
        return [unitX.X - origin.X, unitX.Y - origin.Y, unitY.X - origin.X, unitY.Y - origin.Y, origin.X, origin.Y];
    }

    public static PageGeometry FromPage(PdfPage page)
    {
        // PDFsharp copies inherited /MediaBox, /CropBox and /Rotate onto each page when it reads the file.
        var box = page.Elements.GetRectangle("/CropBox");
        if (box.IsZero) box = page.Elements.GetRectangle("/MediaBox");
        return new PageGeometry(
            Math.Min(box.X1, box.X2), Math.Min(box.Y1, box.Y2),
            Math.Max(box.X1, box.X2), Math.Max(box.Y1, box.Y2),
            NormalizeRotation(page.Elements.GetInteger("/Rotate")));
    }

    /// <summary>Brings /Rotate into 0, 90, 180 or 270. Invalid values (not a multiple of 90) count as 0, which is how PDFium treats them.</summary>
    public static int NormalizeRotation(int rotation)
    {
        int turned = ((rotation % 360) + 360) % 360;
        return turned % 90 == 0 ? turned : 0;
    }
}
