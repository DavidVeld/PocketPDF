namespace PocketPDF.Core.Tests;

public class MarkupTests
{
    [Fact]
    public void Moving_a_callout_moves_arrow_and_label_together()
    {
        var callout = new CalloutMarkup { ArrowTip = new(10, 20), TextPosition = new(50, 60) };
        callout.MoveBy(5, -5);
        Assert.Equal(new PdfPoint(15, 15), callout.ArrowTip);
        Assert.Equal(new PdfPoint(55, 55), callout.TextPosition);
    }

    [Fact]
    public void Moving_a_cloud_moves_every_vertex()
    {
        var cloud = new CloudMarkup { Vertices = [new(0, 0), new(10, 0), new(10, 10)] };
        cloud.MoveBy(1, 2);
        Assert.Equal([new PdfPoint(1, 2), new PdfPoint(11, 2), new PdfPoint(11, 12)], cloud.Vertices);
    }
}
