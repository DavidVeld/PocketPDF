namespace PocketPDF.Core.Tests;

public class OverlayExportTests
{
    // A 1 x 1 white PNG, standing in for the overlay the app encodes.
    static readonly byte[] WhitePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==");

    [Fact]
    public void Export_has_the_drawing_and_a_legend_strip_below_it_on_each_page()
    {
        string path = Path.Combine(Path.GetTempPath(), $"overlay-{Guid.NewGuid()}.pdf");
        try
        {
            OverlayExport.Save(path, [new(WhitePixelPng, 2384, 1684, 1), new(WhitePixelPng, 1191, 842, 2)], "A101 rev P1.pdf", "A101 rev P2.pdf");

            var renderer = new PdfRenderer(File.ReadAllBytes(path));
            Assert.Equal(2, renderer.PageCount);
            var (width, height) = renderer.PageSize(0);
            Assert.Equal(2384, width, 0);
            Assert.True(height > 1684 && height < 1684 + 100, $"legend strip makes the page {height} pt high");
            Assert.Equal(1191, renderer.PageSize(1).Width, 0);
            Assert.False(File.Exists(path + ".saving"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Markups_carried_onto_the_overlay_keep_their_place_on_the_sheet_and_are_saved()
    {
        // A cloud read from the drawing file (rotated and cropped), so it is linked to an annotation there.
        using var drawing = TestPdf.Create();
        using var drawingWithCloud = new TestPdf();
        var cloud = new CloudMarkup { Vertices = [new(150, 100), new(250, 100), new(250, 180)], TextPosition = new(300, 120), Text = "Changed" };
        MarkupFile.Save(MarkupFile.Load(drawing.FilePath), new Dictionary<int, List<Markup>> { [0] = [cloud] }, drawingWithCloud.FilePath);
        var loaded = MarkupFile.Load(drawingWithCloud.FilePath);
        var sheet = loaded.Pages[0];
        var fromFile = Assert.IsType<CloudMarkup>(Assert.Single(loaded.MarkupsByPage[0]));

        // In memory, as Accept opens it: nothing is written until the user saves.
        var overlay = MarkupFile.Load(OverlayExport.Create([new(WhitePixelPng, sheet.ViewWidth, sheet.ViewHeight, 1)], "old.pdf", "new.pdf"));
        var overlayPage = overlay.Pages[0];
        var carried = (CloudMarkup)fromFile.CopyMapped(point => overlayPage.ToPdf(sheet.ToView(point)));

        Assert.False(carried.IsUnchangedFromFile);
        for (int i = 0; i < cloud.Vertices.Count; i++) AssertSameView(sheet.ToView(fromFile.Vertices[i]), overlayPage.ToView(carried.Vertices[i]));
        AssertSameView(sheet.ToView(fromFile.TextPosition!.Value), overlayPage.ToView(carried.TextPosition!.Value));

        using var savedOverlay = new TestPdf();
        MarkupFile.Save(overlay, new Dictionary<int, List<Markup>> { [0] = [carried] }, savedOverlay.FilePath);
        var reloaded = Assert.IsType<CloudMarkup>(Assert.Single(MarkupFile.Load(savedOverlay.FilePath).MarkupsByPage[0]));
        Assert.Equal("Changed", reloaded.Text);
    }

    static void AssertSameView(ViewPoint expected, ViewPoint actual)
    {
        Assert.Equal(expected.X, actual.X, 0.01);
        Assert.Equal(expected.Y, actual.Y, 0.01);
    }
}
