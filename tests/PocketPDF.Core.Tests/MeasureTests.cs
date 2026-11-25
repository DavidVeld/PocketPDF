namespace PocketPDF.Core.Tests;

public class MeasureTests
{
    static readonly double OneToHundred = DrawingScale.FromRatio(100);

    [Fact]
    public void Values_follow_the_scale()
    {
        // 72 pt is an inch of paper: at 1:100 that's 2540 mm.
        var distance = new MeasureMarkup { Kind = MeasureKind.Distance, Points = [new(0, 0), new(72, 0)], MmPerPoint = OneToHundred };
        Assert.Equal(2540, distance.Value, 6);

        var length = new MeasureMarkup { Kind = MeasureKind.Length, Points = [new(0, 0), new(72, 0), new(72, 72)], MmPerPoint = OneToHundred };
        Assert.Equal(5080, length.Value, 6);

        var area = new MeasureMarkup { Kind = MeasureKind.Area, Points = [new(0, 0), new(0, 72), new(72, 72), new(72, 0)], MmPerPoint = OneToHundred };
        Assert.Equal(2540.0 * 2540, area.Value, 3);
        Assert.Equal(6.45, Math.Round(area.Value / 1e6, 2));
    }

    [Fact]
    public void Measurements_save_as_dimension_annotations_and_read_back()
    {
        using var source = TestPdf.CreateWithText();
        using var saved = new TestPdf();
        var loaded = MarkupFile.Load(source.FilePath);
        var markups = loaded.MarkupsByPage[0];
        markups.Add(new MeasureMarkup { Kind = MeasureKind.Distance, Points = [new(50, 50), new(150, 50)], MmPerPoint = OneToHundred, Color = "#1E88E5" });
        markups.Add(new MeasureMarkup { Kind = MeasureKind.Length, Points = [new(50, 100), new(100, 100), new(100, 150)], MmPerPoint = OneToHundred });
        markups.Add(new MeasureMarkup { Kind = MeasureKind.Area, Points = [new(200, 50), new(300, 50), new(300, 120)], MmPerPoint = DrawingScale.FromRatio(50) });

        MarkupFile.Save(loaded, loaded.MarkupsByPage, saved.FilePath);

        Assert.Equal(["/Line", "/PolyLine", "/Polygon"], TestPdf.AnnotationSubtypes(File.ReadAllBytes(saved.FilePath), 0));
        var reloaded = MarkupFile.Load(saved.FilePath).MarkupsByPage[0].Cast<MeasureMarkup>().ToList();
        Assert.Equal([MeasureKind.Distance, MeasureKind.Length, MeasureKind.Area], reloaded.Select(measure => measure.Kind));
        for (int i = 0; i < 3; i++)
        {
            var original = (MeasureMarkup)markups[i];
            Assert.Equal(original.Points, reloaded[i].Points);
            Assert.Equal(original.MmPerPoint, reloaded[i].MmPerPoint, 6);
            Assert.Equal(original.ValueText, reloaded[i].ValueText);
            Assert.True(reloaded[i].IsUnchangedFromFile);
        }
        Assert.Equal("#1E88E5", reloaded[0].Color);
    }

    [Fact]
    public void Page_scale_is_saved_in_a_viewport_and_read_back()
    {
        using var source = TestPdf.CreateWithText();
        using var saved = new TestPdf();
        var loaded = MarkupFile.Load(source.FilePath);
        Assert.Empty(loaded.MmPerPointByPage);

        loaded.MmPerPointByPage[0] = DrawingScale.FromRatio(50);
        MarkupFile.Save(loaded, loaded.MarkupsByPage, saved.FilePath);

        Assert.Equal("1:50", DrawingScale.Describe(MarkupFile.Load(saved.FilePath).MmPerPointByPage[0]));
    }

    [Fact]
    public void A_scale_in_feet_from_another_viewer_is_understood()
    {
        // 1/8" = 1'-0": one inch of paper (72 pt) is 8 feet, so one point is 8/72 ft.
        using var source = TestPdf.CreateWithText();
        AddViewports(source.FilePath, ("ft", 8.0 / 72));

        double mmPerPoint = MarkupFile.Load(source.FilePath).MmPerPointByPage[0];

        Assert.Equal(8 * 304.8 / 72, mmPerPoint, 4); // PDF numbers keep about 6 decimals
        Assert.Equal("1:96", DrawingScale.Describe(mmPerPoint));
    }

    [Fact]
    public void A_sheet_whose_viewports_have_different_scales_gets_no_page_scale()
    {
        // A 1:100 plan and a 1:20 detail on one sheet: no one scale measures both right.
        using var mixed = TestPdf.CreateWithText();
        AddViewports(mixed.FilePath, ("mm", DrawingScale.FromRatio(100)), ("mm", DrawingScale.FromRatio(20)));
        Assert.Empty(MarkupFile.Load(mixed.FilePath).MmPerPointByPage);

        using var same = TestPdf.CreateWithText();
        AddViewports(same.FilePath, ("mm", DrawingScale.FromRatio(50)), ("mm", DrawingScale.FromRatio(50)));
        Assert.Equal("1:50", DrawingScale.Describe(MarkupFile.Load(same.FilePath).MmPerPointByPage[0]));
    }

    /// <summary>Gives the first page viewports with these scales, as another viewer writes them: units per point.</summary>
    static void AddViewports(string filePath, params (string Unit, double PerPoint)[] scales)
    {
        using var document = PdfSharp.Pdf.IO.PdfReader.Open(filePath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        var viewports = scales.Select(scale =>
        {
            var format = new PdfSharp.Pdf.PdfDictionary(document) { Elements = { ["/U"] = new PdfSharp.Pdf.PdfString(scale.Unit), ["/C"] = new PdfSharp.Pdf.PdfReal(scale.PerPoint) } };
            var measure = new PdfSharp.Pdf.PdfDictionary(document) { Elements = { ["/Type"] = new PdfSharp.Pdf.PdfName("/Measure"), ["/Subtype"] = new PdfSharp.Pdf.PdfName("/RL"), ["/X"] = new PdfSharp.Pdf.PdfArray(document, format) } };
            return (PdfSharp.Pdf.PdfItem)new PdfSharp.Pdf.PdfDictionary(document) { Elements = { ["/Type"] = new PdfSharp.Pdf.PdfName("/Viewport"), ["/Measure"] = measure } };
        }).ToArray();
        document.Pages[0].Elements["/VP"] = new PdfSharp.Pdf.PdfArray(document, viewports);
        document.Save(filePath);
    }
}
