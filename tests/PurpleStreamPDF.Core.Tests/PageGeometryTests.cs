namespace PurpleStreamPDF.Core.Tests;

public class PageGeometryTests
{
    // A landscape crop box that doesn't start at 0,0, as CAD exports often have.
    static PageGeometry Page(int rotation) => new(Left: 100, Bottom: 50, Right: 500, Top: 350, Rotation: rotation);

    [Theory]
    [InlineData(0, 400, 300)]
    [InlineData(90, 300, 400)]
    [InlineData(180, 400, 300)]
    [InlineData(270, 300, 400)]
    public void View_size_swaps_for_sideways_pages(int rotation, double expectedWidth, double expectedHeight)
    {
        var page = Page(rotation);
        Assert.Equal(expectedWidth, page.ViewWidth);
        Assert.Equal(expectedHeight, page.ViewHeight);
    }

    [Theory]
    [InlineData(0, 0, 0)]       // unrotated: top-left of crop box stays top-left
    [InlineData(90, 300, 0)]    // turned clockwise: it moves to the top-right
    [InlineData(180, 400, 300)] // upside down: bottom-right
    [InlineData(270, 0, 400)]   // turned anticlockwise: bottom-left
    public void Top_left_of_crop_box_lands_where_the_rotation_puts_it(int rotation, double expectedX, double expectedY)
    {
        var topLeftInPdf = new PdfPoint(100, 350);
        Assert.Equal(new ViewPoint(expectedX, expectedY), Page(rotation).ToView(topLeftInPdf));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Converting_to_view_and_back_returns_the_same_point(int rotation)
    {
        var page = Page(rotation);
        var original = new PdfPoint(123.5, 278.25);
        var roundTrip = page.ToPdf(page.ToView(original));
        Assert.Equal(original.X, roundTrip.X, 9);
        Assert.Equal(original.Y, roundTrip.Y, 9);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-90, 270)]
    [InlineData(450, 90)]
    [InlineData(45, 0)]
    public void Rotation_is_normalized(int rotation, int expected) =>
        Assert.Equal(expected, PageGeometry.NormalizeRotation(rotation));

    [Fact]
    public void Reads_crop_box_and_rotation_from_a_file()
    {
        using var testPdf = TestPdf.Create();

        var pages = MarkupFile.Load(testPdf.FilePath).Pages;

        Assert.Equal(new PageGeometry(100, 50, 500, 350, 90), pages[0]);
        Assert.Equal(new PageGeometry(0, 0, 200, 100, 0), pages[1]);
    }
}
