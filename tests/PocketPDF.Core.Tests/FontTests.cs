using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PocketPDF.Core;

namespace PocketPDF.Core.Tests;

// TextLayout.Fonts is shared by everything: these tests set it, so they run on their own, not beside the others.
[CollectionDefinition(nameof(FontTests), DisableParallelization = true)]
public class FontTestsCollection;

[Collection(nameof(FontTests))]
public class FontTests
{
    /// <summary>A pretend font: every letter a square triangle one font size wide.</summary>
    sealed class TriangleFont : IFontSource
    {
        public double LineWidth(string family, string line, double fontSize) => line.Length * fontSize;
        public double BaselinePerFontSize(string family) => 1;
        public IReadOnlyList<IReadOnlyList<ViewPoint>> Outlines(string family, string line, double fontSize, ViewPoint baseline) =>
            [.. line.Select((_, i) => (IReadOnlyList<ViewPoint>)
                [new(baseline.X + i * fontSize, baseline.Y), new(baseline.X + (i + 1) * fontSize, baseline.Y), new(baseline.X + (i + 1) * fontSize, baseline.Y - fontSize)])];
    }

    [Fact]
    public void Text_in_another_font_is_measured_in_it_and_saved_as_its_letter_outlines()
    {
        TextLayout.Fonts = new TriangleFont();
        try
        {
            Assert.Equal(30, TextLayout.LineWidth("abc", 10, "Triangle"));
            Assert.NotEqual(30, TextLayout.LineWidth("abc", 10)); // the default stays Helvetica

            using var source = TestPdf.Create();
            using var saved = new TestPdf();
            var text = new TextMarkup { TextPosition = new(200, 300), Text = "ab", FontSize = 10, Font = "Triangle" };
            MarkupFile.Save(MarkupFile.Load(source.FilePath), new Dictionary<int, List<Markup>> { [1] = [text] }, saved.FilePath);

            string appearance = AppearanceOf(saved.FilePath, 1, "/FreeText");
            Assert.DoesNotContain("Tj", appearance); // no Helvetica text
            Assert.Equal(2, appearance.Split('\n').Count(line => line == "h")); // a closed outline per letter
            Assert.Contains("\nf\n", appearance); // filled
            Assert.Equal("Triangle", Assert.IsType<TextMarkup>(Assert.Single(MarkupFile.Load(saved.FilePath).MarkupsByPage[1])).Font);
        }
        finally
        {
            TextLayout.Fonts = null;
        }
    }

    [Fact]
    public void Curly_quotes_dashes_and_euro_are_saved_as_their_WinAnsi_codes()
    {
        using var source = TestPdf.Create();
        using var saved = new TestPdf();
        var text = new TextMarkup { TextPosition = new(200, 300), Text = "Don’t – 5€ ✓", FontSize = 10 };
        MarkupFile.Save(MarkupFile.Load(source.FilePath), new Dictionary<int, List<Markup>> { [1] = [text] }, saved.FilePath);

        Assert.Contains("(Don\x92t \x96 5\x80 ?) Tj", AppearanceOf(saved.FilePath, 1, "/FreeText")); // ✓ isn't in WinAnsi
    }

    static string AppearanceOf(string filePath, int pageIndex, string subtype)
    {
        using var document = PdfReader.Open(filePath, PdfDocumentOpenMode.Import);
        var annotations = (PdfArray)document.Pages[pageIndex].Elements.GetObject("/Annots")!;
        var annotation = annotations.Elements.Select(item => (item as PdfSharp.Pdf.Advanced.PdfReference)?.Value as PdfDictionary ?? (PdfDictionary)item)
            .Single(annotation => annotation.Elements.GetName("/Subtype") == subtype);
        var form = (PdfDictionary)((PdfDictionary)annotation.Elements.GetObject("/AP")!).Elements.GetObject("/N")!;
        return System.Text.Encoding.Latin1.GetString(form.Stream.UnfilteredValue);
    }
}
