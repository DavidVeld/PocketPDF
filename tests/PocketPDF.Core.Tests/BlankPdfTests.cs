namespace PocketPDF.Core.Tests;

public class BlankPdfTests
{
    [Fact]
    public void A3_landscape_is_one_empty_420_by_297_mm_page()
    {
        var a3 = PaperSize.Presets.Single(size => size.Name == "A3").Oriented(landscape: true);
        Assert.Equal((420.0, 297.0), (a3.WidthMm, a3.HeightMm));

        var loaded = MarkupFile.Load(BlankPdf.Create(a3.WidthMm, a3.HeightMm));

        var page = Assert.Single(loaded.Pages);
        Assert.Equal(420 / DrawingScale.PaperMmPerPoint, page.ViewWidth, 1);
        Assert.Equal(297 / DrawingScale.PaperMmPerPoint, page.ViewHeight, 1);
        Assert.Empty(loaded.MarkupsByPage[0]);
        Assert.Null(loaded.ViewOnlyReason); // ready to mark up
    }

    [Fact]
    public void Presets_are_portrait_and_turning_back_and_forth_keeps_the_size()
    {
        Assert.All(PaperSize.Presets, size => Assert.True(size.WidthMm < size.HeightMm));
        var a1 = PaperSize.Presets.Single(size => size.Name == "A1");
        Assert.Equal(a1, a1.Oriented(landscape: false));
        Assert.Equal(a1, a1.Oriented(landscape: true).Oriented(landscape: false));
        var square = new PaperSize("Custom", 500, 500);
        Assert.Equal(square, square.Oriented(landscape: true));
    }

    [Theory]
    [InlineData(0, 297)]
    [InlineData(210, -1)]
    [InlineData(210, 6000)]
    [InlineData(double.NaN, 297)]
    public void Sizes_a_PDF_page_cannot_have_are_refused(double widthMm, double heightMm) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BlankPdf.Create(widthMm, heightMm));
}
