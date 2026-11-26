namespace PurpleStreamPDF.Core;

/// <summary>A position in PDF space: points (1/72 inch), origin at the bottom-left, as stored in the file.</summary>
public readonly record struct PdfPoint(double X, double Y);

/// <summary>A position on the page as the user sees it: points, origin at the top-left, page rotation applied.</summary>
public readonly record struct ViewPoint(double X, double Y);

/// <summary>A rectangle in PDF space.</summary>
public readonly record struct PdfRect(double Left, double Bottom, double Right, double Top)
{
    public static PdfRect Around(IEnumerable<PdfPoint> points)
    {
        var list = points.ToList();
        return new(list.Min(p => p.X), list.Min(p => p.Y), list.Max(p => p.X), list.Max(p => p.Y));
    }
}

/// <summary>A rectangle in view space.</summary>
public readonly record struct ViewRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public ViewPoint TopLeft => new(Left, Top);

    public ViewRect Inflate(double margin) => new(Left - margin, Top - margin, Right + margin, Bottom + margin);

    public ViewPoint[] Corners() => [new(Left, Top), new(Right, Top), new(Right, Bottom), new(Left, Bottom)];

    public static ViewRect Around(IEnumerable<ViewPoint> points)
    {
        var list = points.ToList();
        return new(list.Min(p => p.X), list.Min(p => p.Y), list.Max(p => p.X), list.Max(p => p.Y));
    }
}
