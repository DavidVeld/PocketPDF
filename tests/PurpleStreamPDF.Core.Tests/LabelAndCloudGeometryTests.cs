namespace PurpleStreamPDF.Core.Tests;

public class LabelAndCloudGeometryTests
{
    [Fact]
    public void Text_is_measured_with_helvetica_widths()
    {
        // A and B are both 667/1000 em wide in Helvetica.
        Assert.Equal(13.34, TextLayout.LineWidth("AB", 10), 6);
        var (width, height) = TextLayout.Measure("AB\nA", 10);
        Assert.Equal(13.34, width, 6);
        Assert.Equal(24, height, 6);
    }

    [Fact]
    public void Callout_box_adds_padding_and_border_around_the_text()
    {
        var box = LabelGeometry.Box(new ViewPoint(10, 20), "AB", 10, hasBorder: true, lineWidth: 1);
        Assert.Equal(new ViewRect(10, 20, 10 + 13.34 + 8, 20 + 12 + 8), box);
    }

    [Theory]
    [InlineData(50, 0, 50, 100)]     // tip straight above the box: leader starts on the top edge
    [InlineData(500, 500, 110, 140)] // tip below-right: leader starts at the bottom-right corner
    public void Leader_starts_at_the_nearest_point_of_the_box(double tipX, double tipY, double expectedX, double expectedY)
    {
        var box = new ViewRect(10, 100, 110, 140);
        Assert.Equal(new ViewPoint(expectedX, expectedY), LabelGeometry.LeaderStart(box, new ViewPoint(tipX, tipY)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void View_to_pdf_matrix_matches_point_conversion(int rotation)
    {
        var page = new PageGeometry(100, 50, 500, 350, rotation);
        var m = page.ViewToPdfMatrix();
        var viewPoint = new ViewPoint(37, 81);
        var expected = page.ToPdf(viewPoint);
        Assert.Equal(expected.X, m[0] * viewPoint.X + m[2] * viewPoint.Y + m[4], 9);
        Assert.Equal(expected.Y, m[1] * viewPoint.X + m[3] * viewPoint.Y + m[5], 9);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cloud_bumps_bulge_outward_whichever_way_it_was_drawn(bool reversed)
    {
        List<ViewPoint> rectangle = [new(0, 0), new(100, 0), new(100, 50), new(0, 50)];
        if (reversed) rectangle.Reverse();

        var arcs = CloudGeometry.Arcs(rectangle, arcLength: 10);

        Assert.Equal(CloudGeometry.ArcEndPoints(rectangle, 10), arcs.Select(arc => arc.End).ToList());
        foreach (var arc in arcs)
        {
            // The middle of a bump lies outside the rectangle.
            var middle = new ViewPoint((arc.Control1.X + arc.Control2.X) / 2, (arc.Control1.Y + arc.Control2.Y) / 2);
            bool inside = middle.X > 0 && middle.X < 100 && middle.Y > 0 && middle.Y < 50;
            Assert.False(inside, $"Bump ending at {arc.End} bulges inward");
        }
    }

    [Fact]
    public void Nearest_point_on_edges_finds_the_edge_including_the_closing_one()
    {
        ViewPoint[] square = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)];
        Assert.Equal((0, new ViewPoint(4, 0)), CloudGeometry.NearestPointOnEdges(square, new(4, -3)));
        Assert.Equal((1, new ViewPoint(10, 7)), CloudGeometry.NearestPointOnEdges(square, new(12, 7)));
        Assert.Equal((3, new ViewPoint(0, 2)), CloudGeometry.NearestPointOnEdges(square, new(1, 2))); // closing edge, from the inside
        Assert.Equal((1, new ViewPoint(10, 10)), CloudGeometry.NearestPointOnEdges(square, new(15, 15))); // past a corner: clamped to it
    }
}
