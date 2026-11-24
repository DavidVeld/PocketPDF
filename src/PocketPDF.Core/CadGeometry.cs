namespace PocketPDF.Core;

/// <summary>
/// Offset, trim, region finding and hatching for the CAD tools, in PDF space (the page's own units, so lengths are
/// true whatever the page rotation). Lines and circles are edges; hatches are not.
/// </summary>
public static class CadGeometry
{
    /// <summary>A circle is handled as this many-sided polygon: under 0.1% off its radius.</summary>
    public const int CircleSides = 96;

    const double Epsilon = 1e-6;
    const int MaxHatchLines = 20_000;

    /// <summary>The entity as a path of points, or null for a hatch (not an edge).</summary>
    public static (List<PdfPoint> Points, bool Closed)? PathOf(CadMarkup markup) => markup switch
    {
        CadLineMarkup line => (line.Points, line.Closed),
        CadCircleMarkup circle => (Enumerable.Range(0, CircleSides)
            .Select(i => 2 * Math.PI * i / CircleSides)
            .Select(angle => new PdfPoint(circle.Center.X + circle.Radius * Math.Cos(angle), circle.Center.Y + circle.Radius * Math.Sin(angle)))
            .ToList(), true),
        _ => null,
    };

    /// <summary>How far a point is from the entity's line work; infinite for a hatch.</summary>
    public static double DistanceTo(CadMarkup markup, PdfPoint point)
    {
        if (markup is CadCircleMarkup circle) return Math.Abs(Distance(circle.Center, point) - circle.Radius);
        if (PathOf(markup) is not var (points, closed)) return double.PositiveInfinity;
        return Segments(points, closed).Select(segment => Distance(point, Nearest(segment.A, segment.B, point))).DefaultIfEmpty(double.PositiveInfinity).Min();
    }

    // ---- Offset -------------------------------------------------------------------------------

    /// <summary>
    /// A copy <paramref name="distance"/> away on the side of <paramref name="side"/>: a parallel line or polyline
    /// (corners mitred), or a bigger or smaller circle. Null if there's no such copy (a circle shrunk to nothing).
    /// </summary>
    public static CadMarkup? Offset(CadMarkup markup, double distance, PdfPoint side)
    {
        if (markup is CadCircleMarkup circle)
        {
            double radius = Distance(circle.Center, side) >= circle.Radius ? circle.Radius + distance : circle.Radius - distance;
            if (radius <= Epsilon) return null;
            var bigger = (CadCircleMarkup)circle.CopyMapped(point => point);
            bigger.Radius = radius;
            return bigger;
        }
        if (markup is not CadLineMarkup line) return null;
        var points = WithoutRepeats(line.Points, line.Closed);
        if (points.Count < 2) return null;
        var segments = Segments(points, line.Closed).ToList();

        // Every segment runs the same way along the path, so "left of the nearest segment" is the side for all of them.
        var nearest = segments.MinBy(segment => Distance(side, Nearest(segment.A, segment.B, side)));
        double sign = Math.Sign(Cross(nearest.A, nearest.B, side));
        if (sign == 0) return null;
        var shifted = segments.Select(segment =>
        {
            double length = Distance(segment.A, segment.B);
            double nx = -(segment.B.Y - segment.A.Y) / length * sign * distance, ny = (segment.B.X - segment.A.X) / length * sign * distance;
            return (A: new PdfPoint(segment.A.X + nx, segment.A.Y + ny), B: new PdfPoint(segment.B.X + nx, segment.B.Y + ny));
        }).ToList();

        var corners = new List<PdfPoint>();
        int count = shifted.Count;
        for (int i = 0; i < count; i++)
        {
            if (i == 0 && !line.Closed)
            {
                corners.Add(shifted[0].A);
                continue;
            }
            var (before, after) = (shifted[(i - 1 + count) % count], shifted[i]);
            corners.Add(LineCrossing(before.A, before.B, after.A, after.B) ?? after.A); // in line: the corner is just the start
        }
        if (!line.Closed) corners.Add(shifted[^1].B);
        var copy = (CadLineMarkup)line.CopyMapped(point => point);
        copy.Points = corners;
        return copy;
    }

    // ---- Trim ---------------------------------------------------------------------------------

    /// <summary>
    /// Cuts out the part of <paramref name="target"/> around <paramref name="pick"/> up to the nearest crossings with
    /// <paramref name="cutters"/> on either side, as AutoCAD's quick trim. Returns what is left of it (empty: an
    /// entity that nothing crosses is deleted), or null when it can't be trimmed there (a closed shape crossed once).
    /// A trimmed circle comes back as an open polyline arc.
    /// </summary>
    public static List<CadMarkup>? Trim(CadMarkup target, IEnumerable<CadMarkup> cutters, PdfPoint pick)
    {
        if (PathOf(target) is not var (rawPoints, closed)) return null;
        var points = WithoutRepeats(rawPoints, closed);
        if (points.Count < 2) return [];
        var path = new PathMeasure(points, closed);

        var cuts = new List<double>();
        var targetSegments = Segments(points, closed).ToList();
        foreach (var cutter in cutters)
        {
            if (cutter == target || PathOf(cutter) is not var (cutterPoints, cutterClosed)) continue;
            foreach (var other in Segments(cutterPoints, cutterClosed))
                for (int i = 0; i < targetSegments.Count; i++)
                    if (SegmentCrossing(targetSegments[i].A, targetSegments[i].B, other.A, other.B) is (double t, _))
                        cuts.Add(path.Start[i] + t * (path.Start[i + 1] - path.Start[i]));
        }
        if (!closed) cuts.RemoveAll(cut => cut < Epsilon || cut > path.Length - Epsilon); // a line ending on another isn't cut there
        else cuts = cuts.Select(cut => cut > path.Length - Epsilon ? 0 : cut).ToList(); // the end of a closed path is its start
        cuts = cuts.Order().Aggregate(new List<double>(), (unique, cut) =>
        {
            if (unique.Count == 0 || cut - unique[^1] > Epsilon) unique.Add(cut);
            return unique;
        });
        if (cuts.Count == 0) return [];
        double at = path.Project(pick);

        var pieces = new List<List<PdfPoint>>();
        if (closed)
        {
            if (cuts.Count < 2) return null;
            double after = cuts.FirstOrDefault(cut => cut > at, cuts[0]);
            double before = cuts.LastOrDefault(cut => cut < at, cuts[^1]);
            pieces.Add(path.Between(after, before));
        }
        else
        {
            if (cuts.LastOrDefault(cut => cut < at, -1) is var before and >= 0) pieces.Add(path.Between(0, before));
            if (cuts.FirstOrDefault(cut => cut > at, -1) is var after and >= 0) pieces.Add(path.Between(after, path.Length));
        }
        return pieces.Where(piece => piece.Count >= 2).Select(piece => (CadMarkup)new CadLineMarkup
        {
            Points = piece, Color = target.Color, LineWidth = target.LineWidth,
        }).ToList();
    }

    // ---- Extend -------------------------------------------------------------------------------

    /// <summary>
    /// Lengthens the end of an open CAD line nearest <paramref name="pick"/>, straight on, to the first of
    /// <paramref name="boundaries"/> it meets, or to the edge of <paramref name="page"/> if it meets none (AutoCAD's extend).
    /// Returns the line's new points; null for a closed shape or circle, or a line already pointing off the page.
    /// </summary>
    public static List<PdfPoint>? Extend(CadMarkup target, IEnumerable<CadMarkup> boundaries, PdfPoint pick, PdfRect page)
    {
        if (target is not CadLineMarkup { Closed: false } line) return null;
        var points = WithoutRepeats(line.Points, closed: false);
        if (points.Count < 2) return null;
        bool atStart = Distance(pick, points[0]) < Distance(pick, points[^1]);
        var (end, before) = atStart ? (points[0], points[1]) : (points[^1], points[^2]);
        double length = Distance(before, end);
        double reach = 4 * (page.Right - page.Left + page.Top - page.Bottom); // past the page from anywhere on it
        var far = new PdfPoint(end.X + (end.X - before.X) / length * reach, end.Y + (end.Y - before.Y) / length * reach);
        var edges = boundaries.Where(boundary => boundary != target).Select(PathOf).OfType<(List<PdfPoint> Points, bool Closed)>()
            .SelectMany(path => Segments(path.Points, path.Closed))
            .Concat(Segments([new(page.Left, page.Bottom), new(page.Right, page.Bottom), new(page.Right, page.Top), new(page.Left, page.Top)], closed: true));
        double nearest = double.PositiveInfinity;
        foreach (var (c, d) in edges)
            if (SegmentCrossing(end, far, c, d) is (double t, _) && t * reach > Epsilon && t < nearest) nearest = t; // not the boundary it ends on
        if (double.IsPositiveInfinity(nearest)) return null;
        var newEnd = new PdfPoint(end.X + (far.X - end.X) * nearest, end.Y + (far.Y - end.Y) * nearest);
        if (atStart) points[0] = newEnd;
        else points[^1] = newEnd;
        return points;
    }

    /// <summary>Where a CAD line or circle crosses the edge of <paramref name="window"/>.</summary>
    public static List<PdfPoint> Crossings(CadMarkup target, PdfRect window)
    {
        if (PathOf(target) is not var (points, closed)) return [];
        var edges = Segments([new(window.Left, window.Bottom), new(window.Right, window.Bottom), new(window.Right, window.Top), new(window.Left, window.Top)], closed: true).ToList();
        var crossings = new List<PdfPoint>();
        foreach (var (a, b) in Segments(points, closed))
            foreach (var (c, d) in edges)
                if (SegmentCrossing(a, b, c, d) is (double t, _)) crossings.Add(new(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y)));
        return crossings;
    }

    /// <summary>Distances along a path, to find points by how far along they are.</summary>
    sealed class PathMeasure
    {
        readonly List<PdfPoint> _points; // closed paths repeat the first point at the end
        readonly bool _closed;
        public readonly List<double> Start = [0];
        public double Length => Start[^1];

        public PathMeasure(List<PdfPoint> points, bool closed)
        {
            _points = closed ? [.. points, points[0]] : points;
            _closed = closed;
            for (int i = 1; i < _points.Count; i++) Start.Add(Start[^1] + Distance(_points[i - 1], _points[i]));
        }

        public PdfPoint PointAt(double along)
        {
            if (_closed) along = ((along % Length) + Length) % Length;
            int i = Math.Clamp(Start.FindLastIndex(start => start <= along), 0, _points.Count - 2);
            double t = Start[i + 1] > Start[i] ? (along - Start[i]) / (Start[i + 1] - Start[i]) : 0;
            return new(_points[i].X + t * (_points[i + 1].X - _points[i].X), _points[i].Y + t * (_points[i + 1].Y - _points[i].Y));
        }

        public double Project(PdfPoint point)
        {
            int best = 0;
            double bestDistance = double.PositiveInfinity, bestAlong = 0;
            for (; best < _points.Count - 1; best++)
            {
                var onSegment = Nearest(_points[best], _points[best + 1], point);
                double distance = Distance(point, onSegment);
                if (distance < bestDistance) (bestDistance, bestAlong) = (distance, Start[best] + Distance(_points[best], onSegment));
            }
            return bestAlong;
        }

        /// <summary>The path from <paramref name="from"/> to <paramref name="to"/>; on a closed path it goes on round past the start if needed.</summary>
        public List<PdfPoint> Between(double from, double to)
        {
            if (_closed && to <= from) to += Length;
            var piece = new List<PdfPoint> { PointAt(from) };
            for (int lap = 0; lap < (_closed ? 2 : 1); lap++)
                for (int i = 0; i < _points.Count - 1; i++)
                    if (Start[i] + lap * Length is var along && along > from + Epsilon && along < to - Epsilon) piece.Add(_points[i]);
            piece.Add(PointAt(to));
            return to - from > Epsilon ? piece : [];
        }
    }

    // ---- Regions and hatching -----------------------------------------------------------------

    /// <summary>
    /// The smallest closed region formed by the edges that holds <paramref name="inside"/>, as a polygon; null if the
    /// point isn't enclosed. Edges are split where they cross, so lines that overshoot still close a region.
    /// </summary>
    // shortcut: islands (a circle inside the region) aren't cut out of it; add them as holes if hatches must skip them.
    public static List<PdfPoint>? RegionAround(IEnumerable<CadMarkup> edges, PdfPoint inside)
    {
        var segments = edges.Select(PathOf).OfType<(List<PdfPoint> Points, bool Closed)>()
            .SelectMany(path => Segments(path.Points, path.Closed)).Where(segment => Distance(segment.A, segment.B) > Epsilon).ToList();

        // Split every segment where others cross it.
        var splits = segments.Select(segment => new List<(double T, PdfPoint Point)> { (0, segment.A), (1, segment.B) }).ToList();
        for (int i = 0; i < segments.Count; i++)
            for (int j = i + 1; j < segments.Count; j++)
                if (SegmentCrossing(segments[i].A, segments[i].B, segments[j].A, segments[j].B) is var (t, u))
                {
                    var point = new PdfPoint(segments[i].A.X + t * (segments[i].B.X - segments[i].A.X), segments[i].A.Y + t * (segments[i].B.Y - segments[i].A.Y));
                    splits[i].Add((t, point));
                    splits[j].Add((u, point));
                }

        // A graph of the pieces, with nearby points merged into one vertex.
        var vertexOf = new Dictionary<(long, long), int>();
        var vertices = new List<PdfPoint>();
        int VertexAt(PdfPoint point)
        {
            var key = ((long)Math.Round(point.X * 1000), (long)Math.Round(point.Y * 1000));
            if (!vertexOf.TryGetValue(key, out int index))
            {
                vertexOf[key] = index = vertices.Count;
                vertices.Add(point);
            }
            return index;
        }
        var neighbours = new Dictionary<int, HashSet<int>>();
        foreach (var split in splits)
        {
            var ordered = split.OrderBy(item => item.T).Select(item => VertexAt(item.Point)).ToList();
            for (int k = 0; k + 1 < ordered.Count; k++)
            {
                if (ordered[k] == ordered[k + 1]) continue;
                (neighbours.TryGetValue(ordered[k], out var a) ? a : neighbours[ordered[k]] = []).Add(ordered[k + 1]);
                (neighbours.TryGetValue(ordered[k + 1], out var b) ? b : neighbours[ordered[k + 1]] = []).Add(ordered[k]);
            }
        }
        var outgoing = neighbours.ToDictionary(pair => pair.Key, pair => pair.Value
            .OrderBy(to => Math.Atan2(vertices[to].Y - vertices[pair.Key].Y, vertices[to].X - vertices[pair.Key].X)).ToList());

        // Walk every face, turning as far right as possible at each vertex: bounded faces come out anticlockwise.
        var visited = new HashSet<(int, int)>();
        List<PdfPoint>? best = null;
        double bestArea = double.PositiveInfinity;
        foreach (var (start, ends) in outgoing)
            foreach (int firstEnd in ends)
            {
                if (visited.Contains((start, firstEnd))) continue;
                var face = new List<PdfPoint>();
                var (from, to) = (start, firstEnd);
                while (visited.Add((from, to)) && face.Count <= vertices.Count * 2)
                {
                    face.Add(vertices[from]);
                    var around = outgoing[to];
                    int back = around.IndexOf(from);
                    (from, to) = (to, around[(back - 1 + around.Count) % around.Count]);
                }
                double area = SignedArea(face);
                if (area > Epsilon && area < bestArea && Contains(face, inside)) (best, bestArea) = (face, area);
            }
        return best;
    }

    /// <summary>The lines a hatch is drawn with: one set for lines, a second set square to it for a crosshatch, none for solid.</summary>
    public static List<(PdfPoint A, PdfPoint B)> HatchStrokes(CadHatchMarkup hatch) => HatchStrokes(hatch.Boundary, hatch.Pattern, hatch.Spacing, hatch.AngleDegrees);

    public static List<(PdfPoint A, PdfPoint B)> HatchStrokes(IReadOnlyList<PdfPoint> boundary, HatchPattern pattern,
        double spacing = CadHatchMarkup.DefaultSpacing, double angleDegrees = 45) => pattern switch
    {
        HatchPattern.Lines => HatchLines(boundary, spacing, angleDegrees),
        HatchPattern.Crosshatch => [.. HatchLines(boundary, spacing, angleDegrees), .. HatchLines(boundary, spacing, angleDegrees + 90)],
        _ => [],
    };

    /// <summary>Parallel lines <paramref name="spacing"/> apart across the polygon, at <paramref name="angleDegrees"/>. Lines sit on a fixed grid, so neighbouring hatches line up.</summary>
    public static List<(PdfPoint A, PdfPoint B)> HatchLines(IReadOnlyList<PdfPoint> boundary, double spacing, double angleDegrees)
    {
        var lines = new List<(PdfPoint, PdfPoint)>();
        if (boundary.Count < 3 || spacing <= 0) return lines;
        double angle = angleDegrees * Math.PI / 180, cos = Math.Cos(angle), sin = Math.Sin(angle);
        // Turned by -angle the hatch lines run along x; each one is cut where it crosses the outline.
        var turned = boundary.Select(point => new PdfPoint(point.X * cos + point.Y * sin, -point.X * sin + point.Y * cos)).ToList();
        double minY = turned.Min(point => point.Y), maxY = turned.Max(point => point.Y);
        for (double y = Math.Ceiling(minY / spacing) * spacing; y <= maxY && lines.Count < MaxHatchLines; y += spacing)
        {
            var crossings = new List<double>();
            for (int i = 0; i < turned.Count; i++)
            {
                var (a, b) = (turned[i], turned[(i + 1) % turned.Count]);
                if ((a.Y <= y) != (b.Y <= y)) crossings.Add(a.X + (y - a.Y) / (b.Y - a.Y) * (b.X - a.X));
            }
            crossings.Sort();
            for (int i = 0; i + 1 < crossings.Count; i += 2)
                lines.Add((Unturn(crossings[i], y), Unturn(crossings[i + 1], y)));
        }
        return lines;

        PdfPoint Unturn(double x, double y) => new(x * cos - y * sin, x * sin + y * cos);
    }

    /// <summary>
    /// Chains open CAD lines end to end into one polyline, as AutoCAD's JOIN. Where two meet, both are cut or stretched
    /// to where they cross, so trimmed ends that overshoot or fall short make a clean corner. The result is closed when
    /// its last end comes back to its first, within <paramref name="tolerance"/> or the widest gap it bridged.
    /// It keeps the first line's colour and weight. Null for fewer than two lines.
    /// </summary>
    public static CadLineMarkup? Join(IReadOnlyList<CadLineMarkup> lines, double tolerance)
    {
        var pieces = lines.Where(line => !line.Closed).Select(line => WithoutRepeats(line.Points.ToList(), false)).Where(points => points.Count >= 2).ToList();
        if (pieces.Count < 2) return null;
        var chain = pieces[0];
        pieces.RemoveAt(0);
        double widestGap = 0;
        while (pieces.Count > 0)
        {
            // The loose end nearest to either end of the chain. Joining at the start is joining at the end of the chain reversed.
            var (piece, atStart, reversed, gap) = pieces.SelectMany(p => new[]
            {
                (p, false, false, Distance(chain[^1], p[0])), (p, false, true, Distance(chain[^1], p[^1])),
                (p, true, false, Distance(chain[0], p[0])), (p, true, true, Distance(chain[0], p[^1])),
            }).MinBy(candidate => candidate.Item4);
            pieces.Remove(piece);
            if (atStart) chain.Reverse();
            if (reversed) piece.Reverse();
            widestGap = Math.Max(widestGap, gap);
            if (Corner(chain[^2], chain[^1], piece[0], piece[1]) is { } corner)
            {
                chain[^1] = corner;
                chain.AddRange(piece.Skip(1));
            }
            else chain.AddRange(piece); // in line: bridged straight
        }
        bool closed = chain.Count >= 3 && Distance(chain[^1], chain[0]) <= Math.Max(tolerance, widestGap) + Epsilon;
        if (closed && Corner(chain[^2], chain[^1], chain[0], chain[1]) is { } closingCorner)
        {
            chain[0] = closingCorner;
            chain.RemoveAt(chain.Count - 1);
        }
        return new CadLineMarkup { Points = WithoutRepeats(chain, closed), Closed = closed, Color = lines[0].Color, LineWidth = lines[0].LineWidth };

        // Where the segment ending at b and the one starting at c would meet; null if they run (nearly) in line,
        // which would put the corner far off.
        static PdfPoint? Corner(PdfPoint a, PdfPoint b, PdfPoint c, PdfPoint d) =>
            LineCrossing(a, b, c, d) is { } point && Distance(point, b) <= Distance(a, b) && Distance(point, c) <= Distance(c, d) ? point : null;
    }

    // ---- Basics -------------------------------------------------------------------------------

    public static IEnumerable<(PdfPoint A, PdfPoint B)> Segments(IReadOnlyList<PdfPoint> points, bool closed)
    {
        for (int i = 0; i + 1 < points.Count; i++) yield return (points[i], points[i + 1]);
        if (closed && points.Count > 2) yield return (points[^1], points[0]);
    }

    static List<PdfPoint> WithoutRepeats(List<PdfPoint> points, bool closed)
    {
        var result = new List<PdfPoint>();
        foreach (var point in points)
            if (result.Count == 0 || Distance(result[^1], point) > Epsilon) result.Add(point);
        if (closed && result.Count > 1 && Distance(result[0], result[^1]) <= Epsilon) result.RemoveAt(result.Count - 1);
        return result;
    }

    public static double Distance(PdfPoint a, PdfPoint b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    /// <summary>Positive when <paramref name="point"/> is left of a→b (PDF space, y up).</summary>
    static double Cross(PdfPoint a, PdfPoint b, PdfPoint point) => (b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X);

    static PdfPoint Nearest(PdfPoint a, PdfPoint b, PdfPoint point)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, lengthSquared = dx * dx + dy * dy;
        double t = lengthSquared == 0 ? 0 : Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared, 0, 1);
        return new(a.X + t * dx, a.Y + t * dy);
    }

    /// <summary>Where segments a→b and c→d cross, as fractions along each; null if they don't, or run parallel.</summary>
    static (double T, double U)? SegmentCrossing(PdfPoint a, PdfPoint b, PdfPoint c, PdfPoint d)
    {
        double rx = b.X - a.X, ry = b.Y - a.Y, sx = d.X - c.X, sy = d.Y - c.Y;
        double denominator = rx * sy - ry * sx;
        if (Math.Abs(denominator) < 1e-12) return null;
        double qx = c.X - a.X, qy = c.Y - a.Y;
        double t = (qx * sy - qy * sx) / denominator, u = (qx * ry - qy * rx) / denominator;
        const double tolerance = 1e-9;
        return t >= -tolerance && t <= 1 + tolerance && u >= -tolerance && u <= 1 + tolerance ? (Math.Clamp(t, 0, 1), Math.Clamp(u, 0, 1)) : null;
    }

    /// <summary>Where the endless lines through a→b and c→d cross; null if parallel.</summary>
    static PdfPoint? LineCrossing(PdfPoint a, PdfPoint b, PdfPoint c, PdfPoint d)
    {
        double rx = b.X - a.X, ry = b.Y - a.Y, sx = d.X - c.X, sy = d.Y - c.Y;
        double denominator = rx * sy - ry * sx;
        if (Math.Abs(denominator) < 1e-9 * Math.Sqrt((rx * rx + ry * ry) * (sx * sx + sy * sy))) return null;
        double t = ((c.X - a.X) * sy - (c.Y - a.Y) * sx) / denominator;
        return new(a.X + t * rx, a.Y + t * ry);
    }

    static double SignedArea(IReadOnlyList<PdfPoint> points) =>
        points.Select((point, i) => point.X * points[(i + 1) % points.Count].Y - points[(i + 1) % points.Count].X * point.Y).Sum() / 2;

    /// <summary>Even-odd test: a dead-end line walked out and back crosses twice, so it doesn't count.</summary>
    static bool Contains(IReadOnlyList<PdfPoint> polygon, PdfPoint point)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            if ((polygon[i].Y > point.Y) != (polygon[j].Y > point.Y)
                && point.X < (polygon[j].X - polygon[i].X) * (point.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X)
                inside = !inside;
        return inside;
    }

    /// <summary>The point at the same distance from <paramref name="origin"/>, turned to the nearest multiple of 30° or 45° (0, 30, 45, 60, 90…).</summary>
    public static ViewPoint RoundedAngle(ViewPoint origin, ViewPoint point)
    {
        double dx = point.X - origin.X, dy = point.Y - origin.Y, length = Math.Sqrt(dx * dx + dy * dy);
        double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
        double by30 = Math.Round(angle / 30) * 30, by45 = Math.Round(angle / 45) * 45;
        double rounded = (Math.Abs(by30 - angle) <= Math.Abs(by45 - angle) ? by30 : by45) * Math.PI / 180;
        return new(origin.X + length * Math.Cos(rounded), origin.Y + length * Math.Sin(rounded));
    }

    /// <summary>The rectangle corner opposite <paramref name="corner"/> with its width and height rounded to whole <paramref name="step"/>s.</summary>
    public static ViewPoint RoundedCorner(ViewPoint corner, ViewPoint point, double step) => new(
        corner.X + Math.Round((point.X - corner.X) / step) * step,
        corner.Y + Math.Round((point.Y - corner.Y) / step) * step);
}
