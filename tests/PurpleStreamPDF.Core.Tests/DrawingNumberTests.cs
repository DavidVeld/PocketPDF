namespace PurpleStreamPDF.Core.Tests;

public class DrawingNumberTests
{
    static string? Find(params (string Text, double X, double Y)[] pieces) => DrawingNumber.FindOnSheet(ScaleOnSheetTests.Sheet(pieces));

    [Fact]
    public void Picks_the_hyphenated_number_furthest_bottom_right()
    {
        Assert.Equal("P452275-WW-FF-00-D-S-41000", Find(
            ("SEE P452275-WW-FF-01-D-S-41001 FOR LEVEL 01", 100, 100),
            ("DRAWING No.", 900, 760),
            ("P452275-WW-FF-00-D-S-41000", 900, 780)));
    }

    [Fact]
    public void Skips_dates_clauses_and_words_without_digits()
    {
        Assert.Null(Find(("19-06-2026", 900, 780), ("BS EN 1995-1-1", 500, 500), ("LOAD-BEARING-STUD-WALL", 600, 600)));
        Assert.Null(Find(("FF_L00", 900, 780)));
    }
}
