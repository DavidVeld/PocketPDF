namespace PocketPDF.Core.Tests;

public class PageTextTests
{
    static PageText ReadText(TestPdf pdf) => new PdfRenderer(File.ReadAllBytes(pdf.FilePath)).GetText(0);

    [Fact]
    public void Text_comes_in_reading_order_with_line_breaks_and_a_space_between_words_drawn_apart()
    {
        using var pdf = TestPdf.CreateWithText();
        var text = ReadText(pdf);
        Assert.Equal("Hello world\r\nSecond line\r\nGap Here", text.TextOf(0, text.Characters.Count - 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Character_boxes_are_where_the_page_geometry_puts_the_text(int rotation)
    {
        using var pdf = TestPdf.CreateWithText(rotation);
        var page = MarkupFile.Load(pdf.FilePath).Pages[0];
        var text = ReadText(pdf);

        // The middle of the "H" of "Hello": it starts at x 50 on the baseline at y 250, and is 12 pt text.
        var insideH = page.ToView(new PdfPoint(53, 254));
        int index = text.CharacterAt(insideH, tolerance: 0);
        Assert.True(index >= 0, $"no character at {insideH}");
        Assert.Equal('H', text.Characters[index].Char);
        Assert.Equal("Hello", text.TextOf(index, index + 4));
    }

    [Fact]
    public void Nearest_character_and_tolerance()
    {
        using var pdf = TestPdf.CreateWithText();
        var text = ReadText(pdf);
        Assert.Equal(-1, text.CharacterAt(new ViewPoint(300, 280), tolerance: 5)); // empty area
        Assert.Equal('e', text.Characters[text.CharacterAt(new ViewPoint(300, 75))].Char); // end of "Second line", far to the right
    }

    [Fact]
    public void Word_around_a_character()
    {
        using var pdf = TestPdf.CreateWithText();
        var text = ReadText(pdf);
        int r = text.Characters.ToList().FindIndex(character => character.Char == 'r');
        var (start, end) = text.WordAt(r);
        Assert.Equal("world", text.TextOf(start, end));
    }

    [Fact]
    public void One_rectangle_per_line_covering_its_characters()
    {
        using var pdf = TestPdf.CreateWithText();
        var text = ReadText(pdf);
        var rects = text.LineRects(0, text.Characters.Count - 1);
        Assert.Equal(3, rects.Count);
        Assert.InRange(rects[0].Left, 50, 52);
        Assert.InRange(rects[0].Right, 107, 110); // "Hello world" in Helvetica 12 is about 58 pt wide
        Assert.True(rects[0].Bottom <= rects[1].Top + 1, "lines overlap");
        Assert.InRange(rects[2].Right, 144, 147); // "Gap Here" reaches past x 120
    }

    [Fact]
    public void A_page_without_text_is_empty()
    {
        using var pdf = TestPdf.Create();
        Assert.True(ReadText(pdf).IsEmpty);
    }
}
