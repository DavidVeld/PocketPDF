namespace PurpleStreamPDF.Core.Tests;

public class OverlapFinderTests
{
    [Fact]
    public void FindsTextALineRunsThrough_NotClearOrInvisibleText()
    {
        // A line through the middle of "Hello world" (baseline 250, 12 pt), and invisible text (3 Tr, like a scan's
        // searchable layer) at y 100 with a line through it too. "Second line", "Gap" and "Here" are left clear.
        using var pdf = TestPdf.CreateWithText(extraContent:
            "0 0 0 RG 1 w 40 254 m 300 254 l S\nBT 3 Tr /F1 12 Tf 50 100 Td (Hidden) Tj ET\n40 104 m 300 104 l S\n");
        var renderer = new PdfRenderer(File.ReadAllBytes(pdf.FilePath));
        const double scale = 150 / 72.0;
        var (withText, withoutText) = renderer.RenderWithAndWithoutText(0, scale);

        var area = Assert.Single(OverlapFinder.FindOverlaps(renderer.GetText(0), withText, withoutText, scale));

        // "Hello world" is at view y 300 - 259 to 300 - 250, from x 50.
        Assert.InRange(area.Left, 40, 50);
        Assert.InRange(area.Top, 30, 45);
        Assert.InRange(area.Bottom, 46, 60); // not down to "Second line" at view y 70
        Assert.InRange(area.Right, 100, 130);
    }
}
