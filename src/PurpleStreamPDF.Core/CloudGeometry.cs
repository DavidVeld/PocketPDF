namespace PurpleStreamPDF.Core;

/// <summary>One bump of a cloud as a cubic Bezier curve, starting where the previous one ended.</summary>
public readonly record struct BezierArc(ViewPoint Control1, ViewPoint Control2, ViewPoint End);

/// <summary>Shape maths for revision clouds, worked out in view space. Used for both the screen and the PDF appearance.</summary>
public static class CloudGeometry
{
    const double ArcLengthPerPageSize = 0.015; // arc length as a fraction of the page's longer side
    const double RadiusPerChord = 0.55;         // a bit flatter than a semicircle

    public static double ArcLength(PageGeometry page) => Math.Max(page.ViewWidth, page.ViewHeight) * ArcLengthPerPageSize;

    /// <summary>How far the bumps can stick out beyond the polygon. Arcs can be up to 1.5 × arcLength long.</summary>
    public static double BumpMargin(double arcLength) => arcLength * 0.5;

    /// <summary>The bumps of a cloud around a closed polygon, each bulging outward.</summary>
    public static List<BezierArc> Arcs(IReadOnlyList<ViewPoint> vertices, double arcLength)
    {
        bool clockwise = IsClockwiseOnScreen(vertices);
        var arcs = new List<BezierArc>();
        var arcStart = vertices[0];
        foreach (var arcEnd in ArcEndPoints(vertices, arcLength))
        {
            arcs.Add(Arc(arcStart, arcEnd, clockwise));
            arcStart = arcEnd;
        }
        return arcs;
    }

    /// <summary>
    /// Splits every edge of a closed polygon into equal steps close to <paramref name="arcLength"/>.
    /// Returns the end point of each arc in drawing order; the last one is back at the first vertex.
    /// </summary>
    public static List<ViewPoint> ArcEndPoints(IReadOnlyList<ViewPoint> vertices, double arcLength)
    {
        var arcEnds = new List<ViewPoint>();
        for (int i = 0; i < vertices.Count; i++)
        {
            var edgeStart = vertices[i];
            var edgeEnd = vertices[(i + 1) % vertices.Count];
            double edgeLength = Distance(edgeStart, edgeEnd);
            int arcCount = Math.Max(1, (int)Math.Round(edgeLength / arcLength));
            for (int arc = 1; arc <= arcCount; arc++)
            {
                double fraction = arc / (double)arcCount;
                arcEnds.Add(new(
                    edgeStart.X + (edgeEnd.X - edgeStart.X) * fraction,
                    edgeStart.Y + (edgeEnd.Y - edgeStart.Y) * fraction));
            }
        }
        return arcEnds;
    }

    /// <summary>True if the polygon runs clockwise on screen (Y pointing down). Decides which side is outward.</summary>
    public static bool IsClockwiseOnScreen(IReadOnlyList<ViewPoint> vertices)
    {
        double twiceSignedArea = 0;
        for (int i = 0; i < vertices.Count; i++)
        {
            var current = vertices[i];
            var next = vertices[(i + 1) % vertices.Count];
            twiceSignedArea += current.X * next.Y - next.X * current.Y;
        }
        return twiceSignedArea > 0;
    }

    public static double Distance(ViewPoint from, ViewPoint to) => Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));

    /// <summary>
    /// The point on the closed polygon's edges closest to <paramref name="point"/>, and the edge it's on
    /// (edge i runs from vertex i to the next). A new vertex there goes in at index EdgeIndex + 1.
    /// </summary>
    public static (int EdgeIndex, ViewPoint Point) NearestPointOnEdges(IReadOnlyList<ViewPoint> vertices, ViewPoint point)
    {
        (int EdgeIndex, ViewPoint Point) best = (0, vertices[0]);
        double bestDistance = double.MaxValue;
        for (int i = 0; i < vertices.Count; i++)
        {
            var start = vertices[i];
            var end = vertices[(i + 1) % vertices.Count];
            double lengthSquared = Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2);
            double along = lengthSquared < 1e-12 ? 0
                : Math.Clamp(((point.X - start.X) * (end.X - start.X) + (point.Y - start.Y) * (end.Y - start.Y)) / lengthSquared, 0, 1);
            var onEdge = new ViewPoint(start.X + along * (end.X - start.X), start.Y + along * (end.Y - start.Y));
            double distance = Distance(point, onEdge);
            if (distance < bestDistance) (best, bestDistance) = ((i, onEdge), distance);
        }
        return best;
    }

    /// <summary>A circular arc from start to end, bulging outward, approximated by one cubic Bezier.</summary>
    static BezierArc Arc(ViewPoint start, ViewPoint end, bool polygonIsClockwise)
    {
        double chord = Distance(start, end);
        if (chord < 1e-9) return new(start, end, end);

        double directionX = (end.X - start.X) / chord, directionY = (end.Y - start.Y) / chord;
        // Outward is to the left of the direction of travel for a clockwise polygon, to the right otherwise.
        double outwardX = polygonIsClockwise ? directionY : -directionY;
        double outwardY = polygonIsClockwise ? -directionX : directionX;

        double radius = chord * RadiusPerChord, halfChord = chord / 2;
        double centerBehindChord = Math.Sqrt(radius * radius - halfChord * halfChord);
        var center = new ViewPoint(
            (start.X + end.X) / 2 - outwardX * centerBehindChord,
            (start.Y + end.Y) / 2 - outwardY * centerBehindChord);

        double sweepAngle = 2 * Math.Asin(halfChord / radius);
        double handleLength = 4.0 / 3 * Math.Tan(sweepAngle / 4) * radius;
        var (startTangentX, startTangentY) = TangentAlongTravel(start, center, directionX, directionY);
        var (endTangentX, endTangentY) = TangentAlongTravel(end, center, directionX, directionY);
        return new(
            new(start.X + startTangentX * handleLength, start.Y + startTangentY * handleLength),
            new(end.X - endTangentX * handleLength, end.Y - endTangentY * handleLength),
            end);
    }

    /// <summary>Unit tangent of the circle at a point, pointing the same way as the travel direction.</summary>
    static (double X, double Y) TangentAlongTravel(ViewPoint point, ViewPoint center, double directionX, double directionY)
    {
        double radiusX = point.X - center.X, radiusY = point.Y - center.Y;
        double length = Math.Sqrt(radiusX * radiusX + radiusY * radiusY);
        double tangentX = -radiusY / length, tangentY = radiusX / length;
        return tangentX * directionX + tangentY * directionY >= 0 ? (tangentX, tangentY) : (-tangentX, -tangentY);
    }
}
