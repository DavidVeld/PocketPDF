namespace PocketPDF.Core.Tests;

public class CloudGeometryTests
{
    // 100 x 50 rectangle, drawn clockwise on screen (Y points down).
    static readonly List<ViewPoint> ClockwiseRectangle = [new(0, 0), new(100, 0), new(100, 50), new(0, 50)];

    [Fact]
    public void Each_edge_is_split_into_arcs_close_to_the_requested_length()
    {
        var arcEnds = CloudGeometry.ArcEndPoints(ClockwiseRectangle, arcLength: 10);

        // 10 + 5 + 10 + 5 arcs around the 100 x 50 rectangle.
        Assert.Equal(30, arcEnds.Count);
        Assert.Equal(new ViewPoint(10, 0), arcEnds[0]);
        Assert.Equal(ClockwiseRectangle[0], arcEnds[^1]);
    }

    [Fact]
    public void Short_edges_still_get_one_arc()
    {
        var arcEnds = CloudGeometry.ArcEndPoints(ClockwiseRectangle, arcLength: 1000);
        Assert.Equal(4, arcEnds.Count);
    }

    [Fact]
    public void Detects_drawing_direction()
    {
        Assert.True(CloudGeometry.IsClockwiseOnScreen(ClockwiseRectangle));
        Assert.False(CloudGeometry.IsClockwiseOnScreen(Enumerable.Reverse(ClockwiseRectangle).ToList()));
    }
}
