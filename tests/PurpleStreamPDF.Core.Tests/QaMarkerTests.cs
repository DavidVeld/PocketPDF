namespace PurpleStreamPDF.Core.Tests;

public class QaMarkerTests
{
    [Fact]
    public void Qa_boxes_are_still_known_after_saving_and_other_markups_are_not()
    {
        using var source = TestPdf.Create();
        using var saved = new TestPdf();
        List<Markup> markups =
        [
            new RectangleMarkup { Corner1 = new(10, 10), Corner2 = new(60, 30), Subject = RectangleMarkup.QaSubject },
            new RectangleMarkup { Corner1 = new(10, 50), Corner2 = new(60, 70) },
            new CloudMarkup { Vertices = [new(100, 10), new(150, 10), new(150, 40)], Subject = RectangleMarkup.QaSubject }, // only boxes count
        ];
        MarkupFile.Save(MarkupFile.Load(source.FilePath), new Dictionary<int, List<Markup>> { [0] = markups }, saved.FilePath);

        var reloaded = MarkupFile.Load(saved.FilePath).MarkupsByPage[0];

        Assert.Equal([true, false, false], reloaded.Select(RectangleMarkup.IsQaMarker));
    }
}
