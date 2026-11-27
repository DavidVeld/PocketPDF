namespace PurpleStreamPDF.Core.Tests;

public class LineWorkTests
{
    static IReadOnlyList<LineSegment> Read(TestPdf pdf) => new PdfRenderer(File.ReadAllBytes(pdf.FilePath)).GetLineWork(0);

    static bool Has(IReadOnlyList<LineSegment> segments, double x1, double y1, double x2, double y2) =>
        segments.Any(segment => (Near(segment.A, x1, y1) && Near(segment.B, x2, y2)) || (Near(segment.A, x2, y2) && Near(segment.B, x1, y1)));

    static bool Near(PdfPoint point, double x, double y) => Math.Abs(point.X - x) < 0.01 && Math.Abs(point.Y - y) < 0.01;

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void Hairlines_draw_the_same_page_with_thinner_lines(int rotation)
    {
        using var pdf = TestPdf.CreateWithLineWork(rotation);
        var renderer = new PdfRenderer(File.ReadAllBytes(pdf.FilePath));
        var normal = renderer.Render(0, scale: 4);
        var hairline = renderer.Render(0, scale: 4, hairlines: true);

        Assert.Equal((normal.Width, normal.Height), (hairline.Width, hairline.Height));
        Assert.Equal(normal.BgraPixels[..4], hairline.BgraPixels[..4]); // same background
        static int Inked(byte[] pixels) => Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4 + 3] > 128);
        int normalInk = Inked(normal.BgraPixels), hairlineInk = Inked(hairline.BgraPixels);
        Assert.True(hairlineInk > 0 && hairlineInk < normalInk / 2, $"inked pixels: normal {normalInk}, hairline {hairlineInk}");
    }

    [Theory]
    [InlineData(0, false, 100, 600)] // round the rectangle and the bottom line
    [InlineData(90, false, 30, 100)] // the same, turned: the line runs down the left
    [InlineData(0, true, 100, 600)]
    public void An_area_draws_the_same_pixels_as_that_part_of_the_whole_page(int rotation, bool hairlines, int left, int top)
    {
        using var pdf = TestPdf.CreateWithLineWork(rotation);
        var renderer = new PdfRenderer(File.ReadAllBytes(pdf.FilePath));
        var whole = renderer.Render(0, scale: 3, hairlines: hairlines);
        var area = new PixelArea(left, top, Width: 300, Height: 250);

        var part = renderer.Render(0, scale: 3, hairlines: hairlines, area: area);

        Assert.Equal((area.Width, area.Height), (part.Width, part.Height));
        for (int row = 0; row < area.Height; row++)
            Assert.Equal(whole.BgraPixels.AsSpan(((area.Top + row) * whole.Width + area.Left) * 4, area.Width * 4).ToArray(),
                part.BgraPixels.AsSpan(row * area.Width * 4, area.Width * 4).ToArray());
        Assert.Contains(part.BgraPixels, value => value != 0); // the area has some of the drawing in it, not only background
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void Lines_rectangles_curves_transforms_and_forms_come_out_in_pdf_space(int rotation)
    {
        using var pdf = TestPdf.CreateWithLineWork(rotation);
        var segments = Read(pdf);

        Assert.True(Has(segments, 10, 20, 110, 20), "plain line");
        Assert.True(Has(segments, 50, 50, 70, 50) && Has(segments, 70, 50, 70, 80) && Has(segments, 70, 80, 50, 80) && Has(segments, 50, 80, 50, 50), "rectangle");
        Assert.True(Has(segments, 300, 0, 300, 50), "line moved by cm");
        Assert.True(Has(segments, 100, 100, 120, 100), "line in a scaled, moved form");

        // The curve: 8 pieces from (200,200) to (250,200), bulging up to y 237.5 halfway.
        var curve = segments.Where(segment => segment.A.X >= 199 && segment.B.X >= 199 && segment.A.Y >= 199).ToList();
        Assert.Equal(8, curve.Count);
        Assert.Contains(curve, segment => Near(segment.A, 200, 200) || Near(segment.B, 200, 200));
        Assert.Contains(curve, segment => Near(segment.A, 250, 200) || Near(segment.B, 250, 200));
        Assert.Contains(curve, segment => Near(segment.B, 225, 237.5) || Near(segment.A, 225, 237.5));

        Assert.Equal(15, segments.Count);
    }

    [Fact]
    public void Circles_give_their_centre_and_other_curves_none()
    {
        // A circle of radius 20 round (330, 230) as four quarter curves, and a squashed one (an ellipse) round (60, 230).
        const string circles = "350 230 m 350 241.046 341.046 250 330 250 c 318.954 250 310 241.046 310 230 c "
            + "310 218.954 318.954 210 330 210 c 341.046 210 350 218.954 350 230 c h S\n"
            + "q 2 0 0 1 0 0 cm 40 230 m 40 241.046 31.046 250 20 250 c 8.954 250 0 241.046 0 230 c "
            + "0 218.954 8.954 210 20 210 c 31.046 210 40 218.954 40 230 c h S Q\n";
        using var pdf = TestPdf.CreateWithLineWork(extra: circles);
        var center = Assert.Single(new PdfRenderer(File.ReadAllBytes(pdf.FilePath)).GetCircleCenters(0)); // the open curve and the ellipse give none
        Assert.True(Near(center, 330, 230), $"{center}");
    }

    [Fact]
    public void A_page_without_line_work_has_none()
    {
        using var pdf = TestPdf.CreateWithText();
        Assert.Empty(Read(pdf));
    }
}
