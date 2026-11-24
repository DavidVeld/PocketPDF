namespace PocketPDF.Core.Tests;

public class MarkupFileTests
{
    const int RotatedPage = 0, PlainPage = 1;

    static Dictionary<int, List<Markup>> SampleMarkups() => new()
    {
        [RotatedPage] =
        [
            new TextMarkup { TextPosition = new(200, 300), Text = "Ø 20 bar – check\nsecond line", FontSize = 10, Color = "#1E88E5" },
            new CalloutMarkup { ArrowTip = new(300, 200), TextPosition = new(400, 300), Text = "Check beam", FontSize = 14, LineWidth = 2, Font = "Consolas" },
            new CloudMarkup { Vertices = [new(150, 100), new(250, 100), new(250, 180), new(150, 180)], Color = "#43A047", LineWidth = 0.5 },
            new HighlightMarkup { Points = [new(120, 320), new(200, 330), new(280, 310)], Color = "#FFFF00", Thickness = 10 },
        ],
        [PlainPage] = [new CloudMarkup { Vertices = [new(20, 20), new(80, 20), new(50, 80)] }],
    };

    [Fact]
    public void Saved_markups_load_back_the_same()
    {
        using var source = TestPdf.Create();
        using var saved = new TestPdf();
        var original = SampleMarkups();

        MarkupFile.Save(MarkupFile.Load(source.FilePath), original, saved.FilePath);
        var reloaded = MarkupFile.Load(saved.FilePath).MarkupsByPage;

        foreach (var pageIndex in original.Keys)
        {
            Assert.Equal(original[pageIndex].Count, reloaded[pageIndex].Count);
            for (int i = 0; i < original[pageIndex].Count; i++) AssertSameMarkup(original[pageIndex][i], reloaded[pageIndex][i]);
        }
    }

    [Fact]
    public void A_turned_page_is_saved_turned_with_its_markups_still_on_the_drawing()
    {
        using var source = TestPdf.Create();
        using var saved = new TestPdf();
        var loaded = MarkupFile.Load(source.FilePath);
        var markups = loaded.MarkupsByPage[PlainPage];
        markups.Add(new LineMarkup { Start = new(20, 20), End = new(80, 40) });

        loaded.RotatePage(PlainPage, 90);
        loaded.RotatePage(RotatedPage, -90);
        Assert.True(loaded.HasTurnedPages);
        Assert.NotSame(loaded.OriginalBytes, MarkupFile.RenderBytes(loaded, new HashSet<string>())); // the page is drawn turned
        MarkupFile.Save(loaded, loaded.MarkupsByPage, saved.FilePath);

        var reloaded = MarkupFile.Load(saved.FilePath);
        Assert.Equal(90, reloaded.Pages[PlainPage].Rotation);
        Assert.Equal(0, reloaded.Pages[RotatedPage].Rotation); // was 90
        var line = Assert.IsType<LineMarkup>(reloaded.MarkupsByPage[PlainPage].Last());
        AssertClose(new(20, 20), line.Start);
        AssertClose(new(80, 40), line.End);

        loaded.RotatePage(PlainPage, -90);
        loaded.RotatePage(RotatedPage, 90);
        Assert.False(loaded.HasTurnedPages); // turned back
    }

    [Fact]
    public void Other_annotations_survive_and_saving_twice_does_not_duplicate()
    {
        using var source = TestPdf.Create();
        using var first = new TestPdf();
        using var second = new TestPdf();

        MarkupFile.Save(MarkupFile.Load(source.FilePath), SampleMarkups(), first.FilePath);
        var loadedFirst = MarkupFile.Load(first.FilePath);
        MarkupFile.Save(loadedFirst, loadedFirst.MarkupsByPage, second.FilePath);

        var subtypes = TestPdf.AnnotationSubtypes(File.ReadAllBytes(second.FilePath), RotatedPage);
        Assert.Equal(["/Square", "/Ink", "/FreeText", "/FreeText", "/Polygon", "/Ink"], subtypes);
    }

    [Fact]
    public void Page_keeps_unchanged_markups_and_is_drawn_without_changed_or_deleted_ones()
    {
        using var source = TestPdf.Create();
        using var saved = new TestPdf();
        MarkupFile.Save(MarkupFile.Load(source.FilePath), SampleMarkups(), saved.FilePath);
        var loaded = MarkupFile.Load(saved.FilePath);

        // Nothing changed yet: the page is drawn straight from the file, with every markup's own appearance.
        Assert.Empty(MarkupFile.AnnotationsToHide(loaded));
        Assert.All(loaded.MarkupsByPage[RotatedPage], markup => Assert.True(markup.IsUnchangedFromFile));

        // Move the text, delete the cloud: both are now drawn by the markup layer, so the page hides the file's versions.
        var rotatedPageMarkups = loaded.MarkupsByPage[RotatedPage];
        rotatedPageMarkups.OfType<TextMarkup>().Single().MoveBy(5, 5);
        rotatedPageMarkups.Remove(rotatedPageMarkups.OfType<CloudMarkup>().Single());
        var renderBytes = MarkupFile.RenderBytes(loaded, MarkupFile.AnnotationsToHide(loaded));

        // The square and the opaque pen stroke (not editable) stay, and so do the unchanged callout and highlight.
        Assert.Equal(["/Square", "/Ink", "/FreeText", "/Ink"], TestPdf.AnnotationSubtypes(renderBytes, RotatedPage));
        Assert.Single(TestPdf.AnnotationSubtypes(renderBytes, PlainPage));
    }

    [Fact]
    public void Tags_pen_lines_and_rectangles_load_back_the_same_and_are_drawn()
    {
        using var source = TestPdf.Create();
        using var saved = new TestPdf();
        List<Markup> original =
        [
            new CalloutMarkup { ArrowTip = new(300, 200), TextPosition = new(400, 300), Text = "Tag", HasBorder = false, Ending = LineEnding.None },
            new CloudMarkup { Vertices = [new(150, 100), new(250, 100), new(250, 180)], TextPosition = new(300, 120), Text = "Cloud tag", FontSize = 10, Ending = LineEnding.Dot },
            new PenMarkup { Points = [new(120, 320), new(200, 330), new(280, 310)], Color = "#000000", LineWidth = 2 },
            new LineMarkup { Start = new(140, 260), End = new(240, 280), Ending = LineEnding.Arrow, LineWidth = 3 },
            new LineMarkup { Start = new(150, 300), End = new(250, 310), Ending = LineEnding.Dot },
            new RectangleMarkup { Corner1 = new(330, 150), Corner2 = new(380, 250), Color = "#123456", LineWidth = 2 },
            new CloudMarkup { Vertices = [new(400, 60), new(440, 100)], Shape = AreaShape.Box, Style = AreaLineStyle.Dashed, TextPosition = new(450, 115), Text = "Box", Ending = LineEnding.Arrow },
            new CloudMarkup { Vertices = [new(400, 130), new(460, 190)], Shape = AreaShape.Circle, Style = AreaLineStyle.Solid, TextPosition = new(465, 205), Text = "Circle" },
            new CloudMarkup { Vertices = [new(400, 230), new(460, 290)], Shape = AreaShape.Circle, Style = AreaLineStyle.None },
            new RectangleMarkup { Corner1 = new(100, 400), Corner2 = new(160, 440), LineWidth = 2, FillColor = "#FFFF00" },
            new RectangleMarkup { Corner1 = new(200, 400), Corner2 = new(260, 440), FillColor = RectangleMarkup.MaskFill, HasOutline = false },
        ];
        MarkupFile.Save(MarkupFile.Load(source.FilePath), new Dictionary<int, List<Markup>> { [RotatedPage] = original }, saved.FilePath);

        var reloaded = MarkupFile.Load(saved.FilePath);
        Assert.Equal(original.Count, reloaded.MarkupsByPage[RotatedPage].Count);
        for (int i = 0; i < original.Count; i++) AssertSameMarkup(original[i], reloaded.MarkupsByPage[RotatedPage][i]);

        var (pixels, width, _) = new PdfRenderer(File.ReadAllBytes(saved.FilePath)).Render(RotatedPage, scale: 1);
        var page = reloaded.Pages[RotatedPage];
        Assert.True(HasInkNear(pixels, width, page.ToView(new PdfPoint(200, 330)), radius: 2), "No pen stroke drawn");
        Assert.True(HasInkNear(pixels, width, page.ToView(new PdfPoint(190, 270)), radius: 2), "No line drawn");
        Assert.True(HasInkNear(pixels, width, page.ToView(new PdfPoint(330, 200)), radius: 2), "No rectangle side drawn");
        Assert.False(HasInkNear(pixels, width, page.ToView(new PdfPoint(355, 200)), radius: 5), "The rectangle should not be filled");
        var circle = ViewRect.Around([page.ToView(new PdfPoint(400, 130)), page.ToView(new PdfPoint(460, 190))]);
        Assert.True(HasInkNear(pixels, width, new ViewPoint(circle.Left, (circle.Top + circle.Bottom) / 2), radius: 2), "No circle drawn");
        var hidden = ViewRect.Around([page.ToView(new PdfPoint(400, 230)), page.ToView(new PdfPoint(460, 290))]);
        Assert.False(HasInkNear(pixels, width, new ViewPoint(hidden.Left, (hidden.Top + hidden.Bottom) / 2), radius: 2), "An area without an outline should not be drawn");
    }

    [Fact]
    public void Can_save_over_the_file_that_was_opened()
    {
        using var source = TestPdf.Create();
        MarkupFile.Save(MarkupFile.Load(source.FilePath), SampleMarkups(), source.FilePath);

        Assert.Equal(4, MarkupFile.Load(source.FilePath).MarkupsByPage[RotatedPage].Count);
        Assert.False(File.Exists(source.FilePath + ".saving"));
    }

    [Theory]
    [InlineData(RotatedPage)]
    [InlineData(PlainPage)]
    public void Other_viewers_can_draw_the_saved_markups(int pageIndex)
    {
        // PDFium (Chrome's engine) only shows an annotation if its appearance stream is valid and in the right place.
        using var source = TestPdf.Create();
        using var saved = new TestPdf();
        var loaded = MarkupFile.Load(source.FilePath);
        var markups = SampleMarkups();
        MarkupFile.Save(loaded, markups, saved.FilePath);

        var renderer = new PdfRenderer(File.ReadAllBytes(saved.FilePath));
        var (pixels, width, _) = renderer.Render(pageIndex, scale: 1);
        var page = loaded.Pages[pageIndex];

        foreach (var cloud in markups[pageIndex].OfType<CloudMarkup>())
        {
            // The first arc starts at the first vertex, so the stroke passes through that pixel.
            var corner = page.ToView(cloud.Vertices[0]);
            Assert.True(HasInkNear(pixels, width, corner, radius: 3), $"No cloud drawn near {corner}");
        }
        foreach (var highlight in markups[pageIndex].OfType<HighlightMarkup>())
        {
            var middlePoint = page.ToView(highlight.Points[1]);
            Assert.True(HasInkNear(pixels, width, middlePoint, radius: 2), $"No highlight drawn near {middlePoint}");
        }
        foreach (var text in markups[pageIndex].OfType<TextMarkup>())
        {
            var box = LabelGeometry.Box(page.ToView(text.TextPosition), text.Text, text.FontSize, hasBorder: false, text.LineWidth);
            var middleOfFirstLine = new ViewPoint(box.Left + box.Width / 4, box.Top + text.FontSize * 0.6);
            Assert.True(HasInkNear(pixels, width, middleOfFirstLine, radius: 6), "No text drawn in the text box");
        }
    }

    static bool HasInkNear(byte[] bgraPixels, int width, ViewPoint center, int radius)
    {
        int height = bgraPixels.Length / 4 / width;
        for (int y = (int)center.Y - radius; y <= (int)center.Y + radius; y++)
        for (int x = (int)center.X - radius; x <= (int)center.X + radius; x++)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) continue;
            int offset = (y * width + x) * 4;
            bool opaque = bgraPixels[offset + 3] > 0;
            bool notWhite = bgraPixels[offset] < 200 || bgraPixels[offset + 1] < 200 || bgraPixels[offset + 2] < 200;
            if (opaque && notWhite) return true;
        }
        return false;
    }

    static void AssertSameMarkup(Markup expected, Markup actual)
    {
        Assert.IsType(expected.GetType(), actual);
        Assert.Equal(expected.Color, actual.Color);
        Assert.Equal(expected.Font, actual.Font);
        switch (expected)
        {
            case TextMarkup text:
                var actualText = (TextMarkup)actual;
                Assert.Equal(text.Text, actualText.Text);
                Assert.Equal(text.FontSize, actualText.FontSize);
                AssertClose(text.TextPosition, actualText.TextPosition);
                break;
            case CalloutMarkup callout:
                var actualCallout = (CalloutMarkup)actual;
                Assert.Equal(callout.Text, actualCallout.Text);
                Assert.Equal(callout.FontSize, actualCallout.FontSize);
                Assert.Equal(callout.LineWidth, actualCallout.LineWidth);
                Assert.Equal(callout.HasBorder, actualCallout.HasBorder);
                Assert.Equal(callout.Ending, actualCallout.Ending);
                AssertClose(callout.ArrowTip, actualCallout.ArrowTip);
                AssertClose(callout.TextPosition, actualCallout.TextPosition);
                break;
            case HighlightMarkup highlight:
                var actualHighlight = (HighlightMarkup)actual;
                Assert.Equal(highlight.Thickness, actualHighlight.Thickness);
                Assert.Equal(highlight.Points.Count, actualHighlight.Points.Count);
                for (int i = 0; i < highlight.Points.Count; i++) AssertClose(highlight.Points[i], actualHighlight.Points[i]);
                break;
            case CloudMarkup cloud:
                var actualCloud = (CloudMarkup)actual;
                Assert.Equal(cloud.LineWidth, actualCloud.LineWidth);
                Assert.Equal(cloud.Vertices.Count, actualCloud.Vertices.Count);
                for (int i = 0; i < cloud.Vertices.Count; i++) AssertClose(cloud.Vertices[i], actualCloud.Vertices[i]);
                Assert.Equal(cloud.TextPosition.HasValue, actualCloud.TextPosition.HasValue);
                if (cloud.TextPosition is { } tag) AssertClose(tag, actualCloud.TextPosition!.Value);
                Assert.Equal(cloud.Text, actualCloud.Text);
                Assert.Equal(cloud.FontSize, actualCloud.FontSize);
                Assert.Equal(cloud.Ending, actualCloud.Ending);
                Assert.Equal(cloud.Shape, actualCloud.Shape);
                Assert.Equal(cloud.Style, actualCloud.Style);
                break;
            case PenMarkup pen:
                var actualPen = (PenMarkup)actual;
                Assert.Equal(pen.LineWidth, actualPen.LineWidth);
                Assert.Equal(pen.Points.Count, actualPen.Points.Count);
                for (int i = 0; i < pen.Points.Count; i++) AssertClose(pen.Points[i], actualPen.Points[i]);
                break;
            case LineMarkup line:
                var actualLine = (LineMarkup)actual;
                Assert.Equal(line.LineWidth, actualLine.LineWidth);
                Assert.Equal(line.Ending, actualLine.Ending);
                AssertClose(line.Start, actualLine.Start);
                AssertClose(line.End, actualLine.End);
                break;
            case RectangleMarkup rectangle:
                var actualRectangle = (RectangleMarkup)actual;
                Assert.Equal(rectangle.LineWidth, actualRectangle.LineWidth);
                Assert.Equal(rectangle.FillColor, actualRectangle.FillColor);
                Assert.Equal(rectangle.HasOutline, actualRectangle.HasOutline);
                // Read back as bottom-left and top-right, whichever corners were clicked.
                AssertClose(new(Math.Min(rectangle.Corner1.X, rectangle.Corner2.X), Math.Min(rectangle.Corner1.Y, rectangle.Corner2.Y)), actualRectangle.Corner1);
                AssertClose(new(Math.Max(rectangle.Corner1.X, rectangle.Corner2.X), Math.Max(rectangle.Corner1.Y, rectangle.Corner2.Y)), actualRectangle.Corner2);
                break;
        }
    }

    static void AssertClose(PdfPoint expected, PdfPoint actual)
    {
        Assert.Equal(expected.X, actual.X, 0.01);
        Assert.Equal(expected.Y, actual.Y, 0.01);
    }
}
