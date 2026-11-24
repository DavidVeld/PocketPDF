namespace PocketPDF.Core;

public enum SnapKind { Endpoint, Intersection, Midpoint, Nearest }

public readonly record struct SnapPoint(ViewPoint Point, SnapKind Kind);

/// <summary>
/// Finds the point of the drawing to snap to near the cursor. Segments sit in a grid of cells so a query only looks
/// at the few nearby ones. Works in view points, the canvas's units.
/// </summary>
public sealed class SnapIndex
{
    const double CellSize = 16;
    const int MaxIntersectionCandidates = 150; // hatching can put hundreds of lines near the cursor; pairs grow with the square

    readonly (ViewPoint A, ViewPoint B)[] _segments;
    readonly Dictionary<(int X, int Y), List<int>> _cells = [];

    public SnapIndex(IEnumerable<(ViewPoint A, ViewPoint B)> segments)
    {
        _segments = segments.ToArray();
        for (int i = 0; i < _segments.Length; i++)
        {
            // Visit the cells along the segment in half-cell steps. A cell it only clips at a corner can be missed,
            // which is why queries also look one cell further out.
            var (a, b) = _segments[i];
            int steps = (int)Math.Ceiling(CloudGeometry.Distance(a, b) / (CellSize / 2)) + 1;
            (int, int)? previous = null;
            for (int step = 0; step <= steps; step++)
            {
                double t = (double)step / steps;
                var cell = CellOf(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y));
                if (cell == previous) continue;
                previous = cell;
                if (!_cells.TryGetValue(cell, out var list)) _cells[cell] = list = [];
                if (list.Count == 0 || list[^1] != i) list.Add(i);
            }
        }
    }

    public int Count => _segments.Length;

    /// <summary>The best point to snap to within <paramref name="radius"/>: an end point, then an intersection, a midpoint, or else the nearest point on a line.</summary>
    public SnapPoint? Snap(ViewPoint point, double radius)
    {
        var near = NearbySegments(point, radius).ToList();
        if (near.Count == 0) return null;

        if (Closest(near.SelectMany(i => new[] { _segments[i].A, _segments[i].B }), point, radius) is { } end) return new(end, SnapKind.Endpoint);

        var crossings = new List<ViewPoint>();
        var candidates = near.Count > MaxIntersectionCandidates
            ? near.OrderBy(i => DistanceToSegment(point, _segments[i]).Distance).Take(MaxIntersectionCandidates).ToList()
            : near;
        for (int i = 0; i < candidates.Count; i++)
            for (int j = i + 1; j < candidates.Count; j++)
                if (Intersection(_segments[candidates[i]], _segments[candidates[j]]) is { } crossing) crossings.Add(crossing);
        if (Closest(crossings, point, radius) is { } intersection) return new(intersection, SnapKind.Intersection);

        var middles = near.Select(i => new ViewPoint((_segments[i].A.X + _segments[i].B.X) / 2, (_segments[i].A.Y + _segments[i].B.Y) / 2));
        if (Closest(middles, point, radius) is { } middle) return new(middle, SnapKind.Midpoint);

        var nearest = near.Select(i => DistanceToSegment(point, _segments[i])).MinBy(found => found.Distance);
        return new(nearest.Point, SnapKind.Nearest);
    }

    /// <summary>How far off a line another piece may sit and still count as the same line, in view points.</summary>
    const double InLineTolerance = 0.5;
    /// <summary>The widest gap between dashes still bridged as one line, in view points (about 10 mm on paper).</summary>
    const double MaxDashGap = 30;

    /// <summary>
    /// The line nearest <paramref name="point"/> within <paramref name="radius"/>, run on over every piece in line with
    /// it that follows on across gaps up to <see cref="MaxDashGap"/>, so a dashed or dash-dot gridline comes back whole.
    /// </summary>
    public (ViewPoint A, ViewPoint B)? LineThrough(ViewPoint point, double radius)
    {
        var picked = NearbySegments(point, radius).Where(i => CloudGeometry.Distance(_segments[i].A, _segments[i].B) > InLineTolerance)
            .Select(i => _segments[i]).DefaultIfEmpty().MinBy(segment => DistanceToSegment(point, segment).Distance);
        if (picked == default) return null;

        var (a, b) = picked;
        double length = CloudGeometry.Distance(a, b), ux = (b.X - a.X) / length, uy = (b.Y - a.Y) / length;
        double Along(ViewPoint p) => (p.X - a.X) * ux + (p.Y - a.Y) * uy;
        double Off(ViewPoint p) => Math.Abs((p.X - a.X) * uy - (p.Y - a.Y) * ux);

        // shortcut: scans every segment of the page; fine for a click, index by angle if huge pages lag on pick.
        var pieces = _segments.Where(s => Off(s.A) <= InLineTolerance && Off(s.B) <= InLineTolerance)
            .Select(s => (From: Math.Min(Along(s.A), Along(s.B)), To: Math.Max(Along(s.A), Along(s.B))))
            .OrderBy(piece => piece.From).ToList();
        double from = 0, to = length;
        bool grew = true;
        while (grew) // pieces are sorted by start, but one ending far along can bridge to later ones, so repeat until settled
        {
            grew = false;
            foreach (var piece in pieces)
                if (piece.To >= from - MaxDashGap && piece.From <= to + MaxDashGap && (piece.From < from || piece.To > to))
                    (from, to, grew) = (Math.Min(from, piece.From), Math.Max(to, piece.To), true);
        }
        return (new(a.X + from * ux, a.Y + from * uy), new(a.X + to * ux, a.Y + to * uy));
    }

    IEnumerable<int> NearbySegments(ViewPoint point, double radius)
    {
        var seen = new HashSet<int>();
        var (minX, minY) = CellOf(point.X - radius, point.Y - radius);
        var (maxX, maxY) = CellOf(point.X + radius, point.Y + radius);
        for (int x = minX - 1; x <= maxX + 1; x++)
            for (int y = minY - 1; y <= maxY + 1; y++)
                if (_cells.TryGetValue((x, y), out var list))
                    foreach (int index in list)
                        if (seen.Add(index) && DistanceToSegment(point, _segments[index]).Distance <= radius) yield return index;
    }

    static (int X, int Y) CellOf(double x, double y) => ((int)Math.Floor(x / CellSize), (int)Math.Floor(y / CellSize));

    static ViewPoint? Closest(IEnumerable<ViewPoint> points, ViewPoint target, double radius)
    {
        ViewPoint? best = null;
        double bestDistance = radius;
        foreach (var point in points)
        {
            double distance = CloudGeometry.Distance(point, target);
            if (distance <= bestDistance) (best, bestDistance) = (point, distance);
        }
        return best;
    }

    static (double Distance, ViewPoint Point) DistanceToSegment(ViewPoint point, (ViewPoint A, ViewPoint B) segment)
    {
        var (index, onEdge) = CloudGeometry.NearestPointOnEdges([segment.A, segment.B], point);
        _ = index;
        return (CloudGeometry.Distance(point, onEdge), onEdge);
    }

    /// <summary>Where two segments cross, if they do (touching at an end counts; parallel ones never cross).</summary>
    static ViewPoint? Intersection((ViewPoint A, ViewPoint B) first, (ViewPoint A, ViewPoint B) second)
    {
        double rX = first.B.X - first.A.X, rY = first.B.Y - first.A.Y;
        double sX = second.B.X - second.A.X, sY = second.B.Y - second.A.Y;
        double denominator = rX * sY - rY * sX;
        if (Math.Abs(denominator) < 1e-9) return null;
        double qX = second.A.X - first.A.X, qY = second.A.Y - first.A.Y;
        double t = (qX * sY - qY * sX) / denominator, u = (qX * rY - qY * rX) / denominator;
        const double Slack = 1e-9;
        if (t < -Slack || t > 1 + Slack || u < -Slack || u > 1 + Slack) return null;
        return new ViewPoint(first.A.X + t * rX, first.A.Y + t * rY);
    }
}
