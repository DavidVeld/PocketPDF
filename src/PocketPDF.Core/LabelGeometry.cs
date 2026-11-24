namespace PocketPDF.Core;

/// <summary>Layout of text labels and callout leaders, shared by the screen and the PDF writer so both look the same.</summary>
public static class LabelGeometry
{
    /// <summary>Space between a callout's border and its text, in points.</summary>
    public const double CalloutPadding = 3;

    /// <summary>Distance from the outer edge of the box to the text. Plain text has no border or padding.</summary>
    public static double TextInset(bool hasBorder, double lineWidth) => hasBorder ? CalloutPadding + lineWidth : 0;

    /// <summary>Outer box of a label, border included.</summary>
    public static ViewRect Box(ViewPoint topLeft, string text, double fontSize, bool hasBorder, double lineWidth, string? font = null)
    {
        var (textWidth, textHeight) = TextLayout.Measure(text, fontSize, font);
        double inset = TextInset(hasBorder, lineWidth);
        return new(topLeft.X, topLeft.Y, topLeft.X + textWidth + 2 * inset, topLeft.Y + textHeight + 2 * inset);
    }

    /// <summary>The point on the box closest to the arrow tip, where the leader line starts.</summary>
    public static ViewPoint LeaderStart(ViewRect box, ViewPoint arrowTip) =>
        new(Math.Clamp(arrowTip.X, box.Left, box.Right), Math.Clamp(arrowTip.Y, box.Top, box.Bottom));

    /// <summary>A cloud tag's leader: from the text box to the nearest point on the cloud's outline.</summary>
    public static (ViewPoint From, ViewPoint To) CloudLeader(IReadOnlyList<ViewPoint> vertices, ViewRect box)
    {
        var (_, onCloud) = CloudGeometry.NearestPointOnEdges(vertices, new ViewPoint((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2));
        return (LeaderStart(box, onCloud), onCloud);
    }

    /// <summary>Radius of the filled dot a leader can end in: about as big as an arrow head is wide.</summary>
    public static double DotRadius(double lineWidth) => 1.5 + lineWidth;

    /// <summary>The three corners of a filled arrow head at the tip of a leader.</summary>
    static double ArrowLength(double lineWidth) => 4 + 3 * lineWidth;

    /// <summary>
    /// Where a line ending in an arrow at <paramref name="tip"/> stops: inside the head, where it is wider than the line,
    /// so the line's square end can't stick out past the point.
    /// </summary>
    public static ViewPoint ArrowLineEnd(ViewPoint leaderStart, ViewPoint tip, double lineWidth)
    {
        double distance = CloudGeometry.Distance(leaderStart, tip);
        if (distance < 0.001) return tip;
        double back = Math.Min(0.8 * ArrowLength(lineWidth), distance);
        return new(tip.X - (tip.X - leaderStart.X) / distance * back, tip.Y - (tip.Y - leaderStart.Y) / distance * back);
    }

    public static ViewPoint[] ArrowHead(ViewPoint leaderStart, ViewPoint tip, double lineWidth)
    {
        double length = ArrowLength(lineWidth), halfWidth = length / 3;
        double distance = CloudGeometry.Distance(leaderStart, tip);
        if (distance < 0.001) distance = 1;
        double directionX = (tip.X - leaderStart.X) / distance, directionY = (tip.Y - leaderStart.Y) / distance;
        double baseX = tip.X - directionX * length, baseY = tip.Y - directionY * length;
        return
        [
            tip,
            new(baseX - directionY * halfWidth, baseY + directionX * halfWidth),
            new(baseX + directionY * halfWidth, baseY - directionX * halfWidth),
        ];
    }
}
