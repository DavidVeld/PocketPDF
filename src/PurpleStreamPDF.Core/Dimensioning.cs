namespace PurpleStreamPDF.Core;

/// <summary>
/// Where a CAD dimension's lines and value go, in view space. The dimension line itself is drawn as two leaders from
/// <see cref="Middle"/> to <see cref="EndA"/> and <see cref="EndB"/>, so its ends follow the line-end setting.
/// The angle is in degrees, clockwise, kept readable.
/// </summary>
public sealed record DimensionShape(List<(ViewPoint A, ViewPoint B)> Lines, ViewPoint Middle, ViewPoint EndA, ViewPoint EndB, ViewPoint TextTopLeft, double TextAngle, ViewPoint[] TextCorners);

/// <summary>
/// What the measuring and CAD tools snap to on the markups, how a CAD dimension is drawn (the screen and the saved
/// file both use <see cref="Shape"/>), and how typing a new length changes the shapes a dimension ends on.
/// </summary>
public static class Dimensioning
{
    // On paper, in points, following common drafting practice: gap before an extension line, overshoot past the dimension line, half a tick.
    const double ExtensionGap = 2, Overshoot = 3.5, TextGap = 2;
    /// <summary>How close a point must be to a shape to count as on it, in points: snapped points sit exactly on it.</summary>
    const double OnTolerance = 0.05;

    /// <summary>
    /// The lines a markup offers to snap to, in view space: its edges, or a circle's quarter points as points (its
    /// outline is many short pieces, whose corners would snap everywhere). A circle's centre comes from <see cref="SnapCenter"/>.
    /// Text, ink and measurements offer none.
    /// </summary>
    public static IEnumerable<(ViewPoint A, ViewPoint B)> SnapSegments(Markup markup, PageGeometry page)
    {
        (List<PdfPoint> Points, bool Closed)? path = markup switch
        {
            CadLineMarkup line => (line.Points, line.Closed),
            CadHatchMarkup hatch => (hatch.Boundary, true),
            LineMarkup line => ([line.Start, line.End], false),
            RectangleMarkup { HasOutline: true } rectangle => (rectangle.FillOutline(page), true),
            CloudMarkup { Shape: not AreaShape.Circle } cloud => (cloud.FillOutline(page), true),
            _ => null,
        };
        if (path is var (points, closed)) return CadGeometry.Segments(points, closed).Select(segment => (page.ToView(segment.A), page.ToView(segment.B)));
        return markup switch
        {
            CadCircleMarkup circle => CircleSnaps(page.ToView(circle.Center), circle.Radius, circle.Radius),
            CloudMarkup { Shape: AreaShape.Circle } ellipse when ViewRect.Around(ellipse.Vertices.Select(page.ToView)) is var box =>
                CircleSnaps(new((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2), box.Width / 2, box.Height / 2),
            _ => [],
        };
    }

    /// <summary>The centre of a circle markup (or a circular cloud), in view space; null for any other markup.</summary>
    public static ViewPoint? SnapCenter(Markup markup, PageGeometry page) => markup switch
    {
        CadCircleMarkup circle => page.ToView(circle.Center),
        CloudMarkup { Shape: AreaShape.Circle } ellipse when ViewRect.Around(ellipse.Vertices.Select(page.ToView)) is var box =>
            new ViewPoint((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2),
        _ => null,
    };

    static IEnumerable<(ViewPoint, ViewPoint)> CircleSnaps(ViewPoint center, double radiusX, double radiusY) =>
        new ViewPoint[] { new(center.X + radiusX, center.Y), new(center.X - radiusX, center.Y), new(center.X, center.Y + radiusY), new(center.X, center.Y - radiusY) }
            .Select(point => (point, point));

    /// <summary>Whether <paramref name="point"/> sits on the markup: on an edge, a circle's outline or centre, or a snap point.</summary>
    static bool IsOn(Markup markup, PdfPoint point, PageGeometry page)
    {
        if (markup is CadCircleMarkup circle)
            return Math.Abs(CadGeometry.Distance(circle.Center, point) - circle.Radius) <= OnTolerance || CadGeometry.Distance(circle.Center, point) <= OnTolerance;
        var onView = page.ToView(point);
        if (SnapCenter(markup, page) is { } center && CloudGeometry.Distance(center, onView) <= OnTolerance) return true;
        return SnapSegments(markup, page).Any(segment =>
            CloudGeometry.Distance(onView, CloudGeometry.NearestPointOnEdges([segment.A, segment.B], onView).Point) <= OnTolerance);
    }

    /// <summary>
    /// Gives <paramref name="dimension"/> a new length by changing what it measures, as far as where its ends sit tells:
    /// a shape both ends sit on is stretched (its points level with the far end move along the dimension; a circle grows
    /// about its centre), and a shape only the far end sits on moves along. With nothing at the far end, the shapes at
    /// the near end move the other way. Dimensions ending on a changed shape follow it, this one too.
    /// False, changing nothing, if no shape sits on either end.
    /// </summary>
    public static bool Resize(CadDimensionMarkup dimension, double lengthMm, IReadOnlyList<Markup> markups, PageGeometry page)
    {
        double length = CadGeometry.Distance(dimension.Start, dimension.End), newLength = lengthMm / dimension.MmPerPoint;
        if (length < 1e-9 || newLength < 1e-9) return false;
        var shapes = markups.Where(markup => markup is not (CadDimensionMarkup or MeasureMarkup)).ToList();
        var (near, far) = (dimension.Start, dimension.End);
        var moving = shapes.Where(shape => IsOn(shape, far, page)).ToList();
        if (moving.Count == 0)
        {
            (near, far) = (far, near);
            moving = shapes.Where(shape => IsOn(shape, far, page)).ToList();
        }
        if (moving.Count == 0) return false;

        double ux = (far.X - near.X) / length, uy = (far.Y - near.Y) / length, grow = newLength - length;
        PdfPoint Along(PdfPoint point) => new(point.X + ux * grow, point.Y + uy * grow);
        bool LevelWithFar(PdfPoint point) => Math.Abs((point.X - near.X) * ux + (point.Y - near.Y) * uy - length) <= OnTolerance;
        Func<PdfPoint, PdfPoint> MapFor(Markup shape) =>
            !IsOn(shape, near, page) ? Along
            : shape is CadCircleMarkup circle ? point => ScaledAbout(circle.Center, point, newLength / length)
            : point => LevelWithFar(point) ? Along(point) : point;
        var changes = moving.Select(shape => (Shape: shape, Map: MapFor(shape))).ToList();

        // Each dimension end follows the first changed shape it sits on, worked out before anything moves.
        Func<PdfPoint, PdfPoint>? MapAt(PdfPoint point) => changes.FirstOrDefault(change => IsOn(change.Shape, point, page)).Map;
        var followers = markups.OfType<CadDimensionMarkup>().Select(other => (Dimension: other, Start: MapAt(other.Start), End: MapAt(other.End))).ToList();
        foreach (var (shape, map) in changes) shape.Map(map);
        foreach (var (other, start, end) in followers)
        {
            if (start != null) other.Start = start(other.Start);
            if (end != null) other.End = end(other.End);
        }
        return true;
    }

    static PdfPoint ScaledAbout(PdfPoint center, PdfPoint point, double factor) =>
        new(center.X + (point.X - center.X) * factor, center.Y + (point.Y - center.Y) * factor);

    /// <summary>The dimension's extension lines, its dimension line's ends, and where its value goes, upright or reading up.</summary>
    public static DimensionShape Shape(CadDimensionMarkup dimension, PageGeometry page)
    {
        var (start, end) = (dimension.Start, dimension.End);
        double length = CadGeometry.Distance(start, end), nx = -(end.Y - start.Y) / length, ny = (end.X - start.X) / length;
        var (a, b) = (page.ToView(start), page.ToView(end));
        var lineA = page.ToView(new PdfPoint(start.X + nx * dimension.Offset, start.Y + ny * dimension.Offset));
        var lineB = page.ToView(new PdfPoint(end.X + nx * dimension.Offset, end.Y + ny * dimension.Offset));
        var (dx, dy) = Unit(lineA, lineB);

        var lines = new List<(ViewPoint, ViewPoint)>();
        foreach (var (from, to) in new[] { (a, lineA), (b, lineB) })
        {
            if (CloudGeometry.Distance(from, to) <= ExtensionGap) continue; // the dimension line runs through the points: no extension lines
            var (ex, ey) = Unit(from, to);
            lines.Add((new(from.X + ex * ExtensionGap, from.Y + ey * ExtensionGap), new(to.X + ex * Overshoot, to.Y + ey * Overshoot)));
        }
        if (dimension.Ending == LineEnding.Tick) // drafting practice: the dimension line runs on a little past its ticks
        {
            lines.Add((lineA, new(lineA.X - dx * Overshoot, lineA.Y - dy * Overshoot)));
            lines.Add((lineB, new(lineB.X + dx * Overshoot, lineB.Y + dy * Overshoot)));
        }
        var middle = new ViewPoint((lineA.X + lineB.X) / 2, (lineA.Y + lineB.Y) / 2);

        double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
        if (angle >= 90) angle -= 180; // upright or reading up, as CAD text does: a vertical dimension reads from the bottom
        else if (angle < -90) angle += 180;
        double radians = angle * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
        var (upX, upY) = (sin, -cos); // up for the text, in view space (Y down)
        var (width, height) = TextLayout.Measure(dimension.ValueText, CadDimensionMarkup.TextSize, dimension.Font);
        double centerX = (lineA.X + lineB.X) / 2 + upX * (TextGap + height / 2), centerY = (lineA.Y + lineB.Y) / 2 + upY * (TextGap + height / 2);
        var topLeft = new ViewPoint(centerX - cos * width / 2 + upX * height / 2, centerY - sin * width / 2 + upY * height / 2);
        ViewPoint[] corners =
        [
            topLeft, new(topLeft.X + cos * width, topLeft.Y + sin * width),
            new(topLeft.X + cos * width - upX * height, topLeft.Y + sin * width - upY * height), new(topLeft.X - upX * height, topLeft.Y - upY * height),
        ];
        return new(lines, middle, lineA, lineB, topLeft, angle, corners);
    }

    static (double X, double Y) Unit(ViewPoint from, ViewPoint to)
    {
        double length = CloudGeometry.Distance(from, to);
        return length < 1e-9 ? (1, 0) : ((to.X - from.X) / length, (to.Y - from.Y) / length);
    }
}
