namespace PurpleStreamPDF.Core;

/// <summary>Where a markup came from: the annotation's place in the original file, the markup's values when read, and the annotation's /Rect.</summary>
internal sealed record AnnotationOrigin(string AnnotationKey, string Fingerprint, PdfRect Bounds);

/// <summary>A markup on a page. All positions are stored in PDF space so they don't depend on zoom or rotation.</summary>
public abstract class Markup
{
    /// <summary>Colour as "#RRGGBB".</summary>
    public string Color { get; set; } = "#E53935";

    /// <summary>Line weight in points.</summary>
    public double LineWidth { get; set; } = 1;

    /// <summary>Font family of the text of text, callouts, tags and measurements; null for the default (Arial on screen, Helvetica in the file).</summary>
    public string? Font { get; set; }

    /// <summary>What the markup means (for example "Delete"), from the tool template it was drawn with; the annotation's /Subj. Null for none.</summary>
    public string? Subject { get; set; }

    /// <summary>Moves every point of the markup through <paramref name="map"/>.</summary>
    public abstract void Map(Func<PdfPoint, PdfPoint> map);

    public void MoveBy(double deltaX, double deltaY) => Map(point => new(point.X + deltaX, point.Y + deltaY));

    /// <summary>A copy with every point mapped (for example onto another page), as a new markup: not linked to any annotation in a file.</summary>
    public Markup CopyMapped(Func<PdfPoint, PdfPoint> map)
    {
        var copy = Clone();
        copy.Origin = null;
        copy.Map(map);
        return copy;
    }

    /// <summary>The annotation this markup was read from, so saving can leave it untouched if nothing changed. Null for new markups.</summary>
    internal AnnotationOrigin? Origin { get; set; }

    /// <summary>
    /// True while the markup is exactly as read from the file. The page then shows it with the file's own appearance
    /// (as the viewer that wrote it drew it), and the markup layer only needs <see cref="FileBounds"/> to let the user select it.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsUnchangedFromFile => Origin != null && Fingerprint() == Origin.Fingerprint;

    /// <summary>The area the annotation covers in the file, or null for new markups.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public PdfRect? FileBounds => Origin?.Bounds;

    /// <summary>All public values of the markup as text. If it still matches <see cref="AnnotationOrigin.Fingerprint"/>, nothing was changed.</summary>
    internal string Fingerprint() => System.Text.Json.JsonSerializer.Serialize(this, GetType());

    /// <summary>An independent copy, still linked to the same file annotation, so an undone change counts as unchanged again.</summary>
    public Markup Clone()
    {
        var copy = (Markup)MemberwiseClone();
        copy.CopyLists();
        return copy;
    }

    /// <summary>Gives a fresh copy its own point lists, so changing one markup never changes the other.</summary>
    protected virtual void CopyLists() { }
}

public sealed class TextMarkup : Markup
{
    /// <summary>Top-left corner of the text as the user sees it.</summary>
    public PdfPoint TextPosition { get; set; }
    public string Text { get; set; } = "";
    public double FontSize { get; set; } = 12;

    public override void Map(Func<PdfPoint, PdfPoint> map) => TextPosition = map(TextPosition);
}

/// <summary>How a leader or line ends: a filled arrow head, a filled dot, plainly, or an oblique drafting tick.</summary>
public enum LineEnding { Arrow, Dot, None, Tick }

/// <summary>A label with a leader line, ending in an arrow, a dot or nothing. A callout has a box round the text; a tag has none.</summary>
public sealed class CalloutMarkup : Markup
{
    public PdfPoint ArrowTip { get; set; }
    /// <summary>Top-left corner of the label box as the user sees it.</summary>
    public PdfPoint TextPosition { get; set; }
    public string Text { get; set; } = "";
    public double FontSize { get; set; } = 12;
    public bool HasBorder { get; set; } = true;
    public LineEnding Ending { get; set; } = LineEnding.Arrow;

    public override void Map(Func<PdfPoint, PdfPoint> map)
    {
        ArrowTip = map(ArrowTip);
        TextPosition = map(TextPosition);
    }
}

/// <summary>A freehand highlighter stroke, see-through so the drawing underneath stays readable.</summary>
public sealed class HighlightMarkup : Markup
{
    public const string DefaultColor = "#FFFF00";
    public const double DefaultThickness = 12;
    /// <summary>How opaque the stroke is. Anything below 1 marks an ink annotation as a highlight when reading a file.</summary>
    public const double Opacity = 0.4;

    public HighlightMarkup() => Color = DefaultColor;

    public List<PdfPoint> Points { get; set; } = [];

    /// <summary>Stroke width in points. Separate from <see cref="Markup.LineWidth"/>, which is far too thin for a highlighter.</summary>
    public double Thickness { get; set; } = DefaultThickness;

    protected override void CopyLists() => Points = [.. Points];

    public override void Map(Func<PdfPoint, PdfPoint> map)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] = map(Points[i]);
    }
}

public enum MeasureKind { Distance, Length, Area }

/// <summary>
/// A measurement: a distance (two points), a length along several points, or an area. It keeps the scale it was
/// measured at, so changing the page's scale later doesn't change measurements already made.
/// </summary>
public sealed class MeasureMarkup : Markup
{
    public const double LabelFontSize = 10;
    public const double LabelPadding = 2;

    public MeasureKind Kind { get; set; }
    public List<PdfPoint> Points { get; set; } = [];

    /// <summary>Real millimetres per PDF point, see <see cref="DrawingScale"/>.</summary>
    public double MmPerPoint { get; set; } = DrawingScale.PaperMmPerPoint;

    /// <summary>Millimetres for a distance or length, square millimetres for an area.</summary>
    public double Value => Kind == MeasureKind.Area ? Math.Abs(SignedArea(Points)) * MmPerPoint * MmPerPoint : PathLength(Points) * MmPerPoint;

    public string ValueText => Kind == MeasureKind.Area ? DrawingScale.FormatArea(Value) : DrawingScale.FormatLength(Value);

    /// <summary>Where the value's label is centred: the middle of a distance, the middle piece of a length, the middle of an area.</summary>
    public ViewPoint LabelCenter(PageGeometry page)
    {
        var points = Points.Select(page.ToView).ToList();
        if (Kind == MeasureKind.Area) return new(points.Average(point => point.X), points.Average(point => point.Y));
        int middle = (points.Count - 1) / 2;
        return new((points[middle].X + points[middle + 1].X) / 2, (points[middle].Y + points[middle + 1].Y) / 2);
    }

    /// <summary>The white box behind the value, centred on <see cref="LabelCenter"/>. The screen and the saved file both use it.</summary>
    public ViewRect LabelBox(PageGeometry page)
    {
        var center = LabelCenter(page);
        var (width, height) = TextLayout.Measure(ValueText, LabelFontSize, Font);
        return new ViewRect(center.X - width / 2 - LabelPadding, center.Y - height / 2 - 1, center.X + width / 2 + LabelPadding, center.Y + height / 2 + 1);
    }

    public override void Map(Func<PdfPoint, PdfPoint> map)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] = map(Points[i]);
    }

    protected override void CopyLists() => Points = [.. Points];

    public static double PathLength(IReadOnlyList<PdfPoint> points) =>
        points.Zip(points.Skip(1), (from, to) => Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2))).Sum();

    /// <summary>Shoelace formula; the sign depends on the direction the points go round.</summary>
    public static double SignedArea(IReadOnlyList<PdfPoint> points) =>
        points.Select((point, i) => point.X * points[(i + 1) % points.Count].Y - points[(i + 1) % points.Count].X * point.Y).Sum() / 2;
}

public enum AreaShape { Polygon, Box, Circle }

public enum AreaLineStyle { Cloud, Dashed, Solid, None }

/// <summary>
/// An area (polygon, box or circle) outlined as a revision cloud, dashes, a solid line or nothing, optionally with a
/// leader to a text tag. The callout, tag and cloud tools all draw one; they only start with a different outline.
/// </summary>
public sealed class CloudMarkup : Markup, IFillable
{
    /// <summary>Dash and gap lengths of a dashed outline, in line widths (as WPF counts them).</summary>
    public static readonly double[] Dashes = [6, 3];

    /// <summary>A polygon's corners, or two opposite corners of a box or of the box round a circle.</summary>
    public List<PdfPoint> Vertices { get; set; } = [];
    public AreaShape Shape { get; set; }
    public AreaLineStyle Style { get; set; }

    /// <summary>The outline as a closed polygon in view space. A circle gets one corner per cloud bump, so the bumps come out even.</summary>
    public List<ViewPoint> Outline(PageGeometry page)
    {
        var points = Vertices.Select(page.ToView).ToList();
        if (Shape == AreaShape.Polygon) return points;
        var box = ViewRect.Around(points);
        if (Shape == AreaShape.Box) return [.. box.Corners()];
        double radiusX = box.Width / 2, radiusY = box.Height / 2;
        double perimeter = Math.PI * (3 * (radiusX + radiusY) - Math.Sqrt((3 * radiusX + radiusY) * (radiusX + 3 * radiusY))); // Ramanujan
        int count = Style == AreaLineStyle.Cloud ? Math.Max(8, (int)Math.Round(perimeter / CloudGeometry.ArcLength(page))) : 96;
        return Enumerable.Range(0, count).Select(i => 2 * Math.PI * i / count)
            .Select(angle => new ViewPoint(box.Left + radiusX + radiusX * Math.Cos(angle), box.Top + radiusY + radiusY * Math.Sin(angle))).ToList();
    }

    /// <summary>Top-left corner of the tag's text, or null for a cloud without a tag.</summary>
    public PdfPoint? TextPosition { get; set; }
    public string Text { get; set; } = "";
    public double FontSize { get; set; } = 12;
    /// <summary>How the tag's leader ends on the area.</summary>
    public LineEnding Ending { get; set; } = LineEnding.None;

    public string? FillColor { get; set; }
    public HatchPattern FillPattern { get; set; } = HatchPattern.Solid;
    public List<PdfPoint> FillOutline(PageGeometry page) => [.. Outline(page).Select(page.ToPdf)];

    protected override void CopyLists() => Vertices = [.. Vertices];

    public override void Map(Func<PdfPoint, PdfPoint> map)
    {
        for (int i = 0; i < Vertices.Count; i++) Vertices[i] = map(Vertices[i]);
        if (TextPosition is { } position) TextPosition = map(position);
    }
}

/// <summary>An opaque freehand pen stroke, as wide as the line weight.</summary>
public sealed class PenMarkup : Markup
{
    public List<PdfPoint> Points { get; set; } = [];

    protected override void CopyLists() => Points = [.. Points];

    public override void Map(Func<PdfPoint, PdfPoint> map)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] = map(Points[i]);
    }
}

/// <summary>A straight line, optionally with an arrow or dot at <see cref="Start"/>.</summary>
public sealed class LineMarkup : Markup
{
    public PdfPoint Start { get; set; }
    public PdfPoint End { get; set; }
    public LineEnding Ending { get; set; } = LineEnding.None;

    public override void Map(Func<PdfPoint, PdfPoint> map)
    {
        Start = map(Start);
        End = map(End);
    }
}

/// <summary>
/// A rectangle between two opposite corners, on the middle of its stroke. The rectangle tool draws an outline without fill;
/// the mask tool a white fill without outline, to blank out part of the drawing.
/// </summary>
public sealed class RectangleMarkup : Markup, IFillable
{
    public const string MaskFill = "#FFFFFF";

    public PdfPoint Corner1 { get; set; }
    public PdfPoint Corner2 { get; set; }
    /// <summary>Fill as "#RRGGBB", or null for none.</summary>
    public string? FillColor { get; set; }
    public HatchPattern FillPattern { get; set; } = HatchPattern.Solid;
    public List<PdfPoint> FillOutline(PageGeometry page) => [Corner1, new(Corner2.X, Corner1.Y), Corner2, new(Corner1.X, Corner2.Y)];
    /// <summary>False for a mask: the corners are then the edge of the fill.</summary>
    public bool HasOutline { get; set; } = true;

    public override void Map(Func<PdfPoint, PdfPoint> map)
    {
        Corner1 = map(Corner1);
        Corner2 = map(Corner2);
    }
}

/// <summary>A closed shape that can be filled: solid, or with hatch lines in the fill colour.</summary>
public interface IFillable
{
    /// <summary>Weight of the hatch lines of a patterned fill, in points.</summary>
    const double PatternLineWidth = 0.5;

    /// <summary>Fill as "#RRGGBB", or null for none.</summary>
    string? FillColor { get; set; }
    HatchPattern FillPattern { get; set; }
    /// <summary>False for a shape that is open just now (an open CAD polyline).</summary>
    bool CanFill => true;
    /// <summary>The edge of the fill, in PDF points.</summary>
    List<PdfPoint> FillOutline(PageGeometry page);
}
