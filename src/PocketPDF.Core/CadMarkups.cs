namespace PocketPDF.Core;

/// <summary>
/// A drawing entity of the experimental CAD tools. Saved on the file's "CAD" layer (an optional content group), so
/// any viewer can switch them off together.
/// </summary>
public abstract class CadMarkup : Markup;

/// <summary>A line (two points) or polyline, open or closed.</summary>
public sealed class CadLineMarkup : CadMarkup, IFillable
{
    public List<PdfPoint> Points { get; set; } = [];
    public bool Closed { get; set; }
    public string? FillColor { get; set; }
    public HatchPattern FillPattern { get; set; } = HatchPattern.Solid;
    public bool CanFill => Closed;
    public List<PdfPoint> FillOutline(PageGeometry page) => Points;

    protected override void CopyLists() => Points = [.. Points];

    public override void Map(Func<PdfPoint, PdfPoint> map)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] = map(Points[i]);
    }
}

public sealed class CadCircleMarkup : CadMarkup, IFillable
{
    public PdfPoint Center { get; set; }
    /// <summary>In PDF points.</summary>
    public double Radius { get; set; }
    public string? FillColor { get; set; }
    public HatchPattern FillPattern { get; set; } = HatchPattern.Solid;
    public List<PdfPoint> FillOutline(PageGeometry page) => CadGeometry.PathOf(this)!.Value.Points;

    public override void Map(Func<PdfPoint, PdfPoint> map)
    {
        var edge = map(new PdfPoint(Center.X + Radius, Center.Y)); // maps only move and turn, so the radius survives
        Center = map(Center);
        Radius = Math.Sqrt(Math.Pow(edge.X - Center.X, 2) + Math.Pow(edge.Y - Center.Y, 2));
    }
}

/// <summary>
/// An aligned dimension between two points, drawn the usual drafting way: extension lines, a dimension line with oblique ticks
/// (or another <see cref="Ending"/>) and the length above it. It keeps the scale it was made at, as a measurement does. Typing a new length changes the
/// shapes its ends sit on (see <see cref="Dimensioning.Resize"/>).
/// </summary>
public sealed class CadDimensionMarkup : CadMarkup
{
    /// <summary>Text height on paper, in points (about 2.8 mm).</summary>
    public const double TextSize = 8;

    public PdfPoint Start { get; set; }
    public PdfPoint End { get; set; }
    /// <summary>How far the dimension line sits from the measured points, in PDF points: left of Start→End, or right when negative.</summary>
    public double Offset { get; set; }
    /// <summary>Real millimetres per PDF point, see <see cref="DrawingScale"/>.</summary>
    public double MmPerPoint { get; set; } = DrawingScale.PaperMmPerPoint;
    /// <summary>How both ends of the dimension line end.</summary>
    public LineEnding Ending { get; set; } = LineEnding.Tick;

    public double LengthMm => CadGeometry.Distance(Start, End) * MmPerPoint;
    public string ValueText => DrawingScale.FormatLength(LengthMm);

    /// <summary>How far <paramref name="point"/> is from the measured line, as <see cref="Offset"/> counts it.</summary>
    public double OffsetTo(PdfPoint point)
    {
        double length = CadGeometry.Distance(Start, End);
        return length < 1e-9 ? 0 : ((End.X - Start.X) * (point.Y - Start.Y) - (End.Y - Start.Y) * (point.X - Start.X)) / length;
    }

    /// <summary>The middle of the dimension line.</summary>
    public PdfPoint LineMiddle()
    {
        double length = CadGeometry.Distance(Start, End);
        double nx = length < 1e-9 ? 0 : -(End.Y - Start.Y) / length, ny = length < 1e-9 ? 0 : (End.X - Start.X) / length;
        return new((Start.X + End.X) / 2 + nx * Offset, (Start.Y + End.Y) / 2 + ny * Offset);
    }

    public override void Map(Func<PdfPoint, PdfPoint> map)
    {
        Start = map(Start);
        End = map(End);
    }
}

/// <summary>How a hatch fills its region: parallel 45° lines, lines both ways, or solid colour.</summary>
public enum HatchPattern { Lines, Crosshatch, Solid }

/// <summary>A region bounded by <see cref="Boundary"/>, filled in its <see cref="Pattern"/>.</summary>
public sealed class CadHatchMarkup : CadMarkup
{
    /// <summary>3.175 mm on paper, as ANSI31.</summary>
    public const double DefaultSpacing = 9;

    public List<PdfPoint> Boundary { get; set; } = [];
    public HatchPattern Pattern { get; set; }
    /// <summary>Distance between the hatch lines, in PDF points (paper, not real size).</summary>
    public double Spacing { get; set; } = DefaultSpacing;
    public double AngleDegrees { get; set; } = 45;

    protected override void CopyLists() => Boundary = [.. Boundary];

    public override void Map(Func<PdfPoint, PdfPoint> map)
    {
        for (int i = 0; i < Boundary.Count; i++) Boundary[i] = map(Boundary[i]);
    }
}
