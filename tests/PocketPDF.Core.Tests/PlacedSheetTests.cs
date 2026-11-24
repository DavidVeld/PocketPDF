namespace PocketPDF.Core.Tests;

public class PlacedSheetTests
{
    static byte[] LineWorkSheet()
    {
        using var drawing = TestPdf.CreateWithLineWork();
        return PlacedSheet.FromPdfPage(File.ReadAllBytes(drawing.FilePath), 0);
    }

    [Fact]
    public void White_paper_is_see_through_and_lines_keep_or_take_the_tint()
    {
        var source = LineWorkSheet();
        var plain = PlacedSheet.Render(source, 1, tint: null);
        var tinted = PlacedSheet.Render(source, 1, tint: "#1E88E5");
        int OnLine(PageBitmap bitmap) => (int)((300 - 20) * bitmap.Width + 60) * 4; // the line (10,20)-(110,20): view y = 300 - 20
        Assert.Equal(0, plain.BgraPixels[3]); // empty paper at the top-left corner
        Assert.True(plain.BgraPixels[OnLine(plain) + 3] > 100, "the line shows");
        Assert.True(plain.BgraPixels[OnLine(plain) + 2] < 60, "and stays black");
        Assert.Equal((byte)0xE5, tinted.BgraPixels[OnLine(tinted)]); // blue channel of the tint
        Assert.Equal((byte)0x1E, tinted.BgraPixels[OnLine(tinted) + 2]);
    }

    [Fact]
    public void A_placed_sheet_keeps_its_place_clip_opacity_and_tint_and_draws_in_the_file()
    {
        using var target = TestPdf.Create();
        using var saved = new TestPdf();
        var loaded = MarkupFile.Load(target.FilePath);
        var page = loaded.Pages[0];
        var sheet = PlacedSheet.Create(LineWorkSheet(), new ViewPoint(20, 30), page, scale: 0.5);
        Assert.True(sheet.ClipTo(new ViewRect(20, 30, 120, 180), page));
        sheet.Opacity = 0.8;
        sheet.Tinted = true;
        sheet.Color = "#1E88E5";
        MarkupFile.Save(loaded, new Dictionary<int, List<Markup>> { [0] = [sheet] }, saved.FilePath);

        var reread = Assert.IsType<PlacedSheetMarkup>(Assert.Single(MarkupFile.Load(saved.FilePath).MarkupsByPage[0]));
        Assert.Equal(sheet.Source, reread.Source);
        Assert.Equal((sheet.SourceWidth, sheet.SourceHeight, sheet.TopLeft, sheet.BottomRight), (reread.SourceWidth, reread.SourceHeight, reread.TopLeft, reread.BottomRight));
        Assert.Equal((0.0, 0.0, 0.5, 1.0), (reread.ClipLeft, reread.ClipTop, reread.ClipRight, reread.ClipBottom));
        Assert.Equal((0.8, true, "#1E88E5"), (reread.Opacity, reread.Tinted, reread.Color));

        // Other viewers see it: the page drawn with its annotations has blue where the sheet's line lands.
        var (pixels, width, _) = new PdfRenderer(File.ReadAllBytes(saved.FilePath)).Render(0, 1);
        var onLine = sheet.FromSource(60 / 400.0, (300 - 20) / 300.0, page);
        int at = ((int)onLine.Y * width + (int)onLine.X) * 4;
        Assert.True(pixels[at] > pixels[at + 2] + 60, $"blue at the sheet's line, got BGR {pixels[at]} {pixels[at + 1]} {pixels[at + 2]}");
    }
}
