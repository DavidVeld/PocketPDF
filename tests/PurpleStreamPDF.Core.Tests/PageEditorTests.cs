using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PurpleStreamPDF.Core.Tests;

public class PageEditorTests
{
    [Theory]
    [InlineData(new[] { 2 }, new[] { 0, 2, 1, 3, 4 })]
    [InlineData(new[] { 1, 2 }, new[] { 1, 2, 0, 3, 4 })] // a block moves together
    [InlineData(new[] { 0, 3 }, new[] { 0, 1, 3, 2, 4 })] // the first page can't go higher; the other still moves
    [InlineData(new[] { 0, 1 }, new[] { 0, 1, 2, 3, 4 })]
    public void Up_moves_each_picked_page_one_place(int[] picked, int[] expected) =>
        Assert.Equal(expected, PageOrder.Up(5, picked.ToHashSet()));

    [Theory]
    [InlineData(new[] { 2 }, new[] { 0, 1, 3, 2, 4 })]
    [InlineData(new[] { 2, 3 }, new[] { 0, 1, 4, 2, 3 })]
    [InlineData(new[] { 1, 4 }, new[] { 0, 2, 1, 3, 4 })]
    public void Down_moves_each_picked_page_one_place(int[] picked, int[] expected) =>
        Assert.Equal(expected, PageOrder.Down(5, picked.ToHashSet()));

    [Fact]
    public void Top_end_and_without_keep_the_picked_pages_in_order()
    {
        var picked = new HashSet<int> { 3, 1 };
        Assert.Equal([1, 3, 0, 2, 4], PageOrder.ToTop(5, picked));
        Assert.Equal([0, 2, 4, 1, 3], PageOrder.ToEnd(5, picked));
        Assert.Equal([0, 2, 4], PageOrder.Without(5, picked));
    }

    [Fact]
    public void Rearrange_reorders_and_drops_pages()
    {
        using var source = TestPdf.Create(); // page 1 has a 400 x 300 crop box and is turned 90; page 2 is 200 x 100
        var bytes = File.ReadAllBytes(source.FilePath);

        var swapped = MarkupFile.Load(PageEditor.Rearrange(bytes, [1, 0]));
        Assert.Equal([(200d, 100d, 0), (400d, 300d, 90)], swapped.Pages.Select(Size));

        var secondOnly = MarkupFile.Load(PageEditor.Rearrange(bytes, [1]));
        Assert.Equal([(200d, 100d, 0)], secondOnly.Pages.Select(Size));

        Assert.Throws<ArgumentException>(() => PageEditor.Rearrange(bytes, []));
        Assert.Throws<ArgumentException>(() => PageEditor.Rearrange(bytes, [0, 0]));
    }

    [Fact]
    public void Markups_go_with_their_page()
    {
        using var source = TestPdf.Create();
        var loaded = MarkupFile.Load(source.FilePath);
        loaded.MarkupsByPage[1].Add(new TextMarkup { TextPosition = new(20, 80), Text = "On page 2" });

        var moved = MarkupFile.Load(PageEditor.Rearrange(MarkupFile.SaveToBytes(loaded), [1, 0]));

        Assert.Equal("On page 2", Assert.IsType<TextMarkup>(Assert.Single(moved.MarkupsByPage[0])).Text);
        Assert.Empty(moved.MarkupsByPage[1]);
    }

    [Fact]
    public void InsertBlank_puts_a_sheet_of_the_size_in()
    {
        using var source = TestPdf.Create();
        var bytes = PageEditor.InsertBlank(File.ReadAllBytes(source.FilePath), 1, 297, 210);

        using var document = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        Assert.Equal(3, document.PageCount);
        Assert.Equal(297, document.Pages[1].Width.Millimeter, 0.1);
        Assert.Equal(210, document.Pages[1].Height.Millimeter, 0.1);
        Assert.Equal(200, document.Pages[2].Width.Point, 0.1); // the old second page follows it
    }

    static (double Width, double Height, int Rotation) Size(PageGeometry page) =>
        (Math.Round(page.Right - page.Left), Math.Round(page.Top - page.Bottom), page.Rotation);
}
