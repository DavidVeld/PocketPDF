namespace PurpleStreamPDF.Core.Tests;

public class ScaleOnSheetTests
{
    /// <summary>Page text with each string written left to right from its point, 6 pt per character.</summary>
    static PageText Sheet(params (string Text, double X, double Y)[] pieces) => new([.. pieces.SelectMany(piece =>
        piece.Text.Select((c, i) => new PageCharacter(c, new ViewRect(piece.X + i * 6, piece.Y, piece.X + i * 6 + 6, piece.Y + 10)))
            .Append(new PageCharacter('\n', default)))]);

    [Fact]
    public void Picks_the_scale_furthest_bottom_right()
    {
        var text = Sheet(("DETAIL A 1:5", 100, 100), ("SCALE 1:100 @ A3", 900, 780), ("SECTION 1 : 20", 500, 400));
        Assert.Equal(100, DrawingScale.FindScaleOnSheet(text));
    }

    [Fact]
    public void Skips_nts_and_reads_thousands()
    {
        Assert.Equal(1250, DrawingScale.FindScaleOnSheet(Sheet(("KEY PLAN 1:1,250", 100, 100), ("SCALE NTS", 900, 780))));
        Assert.Null(DrawingScale.FindScaleOnSheet(Sheet(("SCALE NTS", 900, 780), ("TIME 11:30, REF 21:50", 500, 500))));
    }
}
