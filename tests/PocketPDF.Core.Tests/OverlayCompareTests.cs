namespace PocketPDF.Core.Tests;

public class OverlayCompareTests
{
    static readonly byte[] Ink = [0, 0, 0, 255];
    static readonly byte[] Paper = [0, 0, 0, 0]; // PDFium leaves empty paper transparent

    // Four pixels in a row: line in both, only old, only new, in neither.
    static readonly PageBitmap OldPage = new([.. Ink, .. Ink, .. Paper, .. Paper], 4, 1);
    static readonly PageBitmap NewPage = new([.. Ink, .. Paper, .. Ink, .. Paper], 4, 1);

    static (byte Red, byte Green, byte Blue) PixelAt(byte[] bgra, int x) => (bgra[x * 4 + 2], bgra[x * 4 + 1], bgra[x * 4]);

    [Fact]
    public void Both_shows_unchanged_black_old_red_new_blue_and_white_paper()
    {
        var result = OverlayCompare.Combine(OldPage, NewPage, showOld: true, showNew: true);

        Assert.Equal(((byte)0, (byte)0, (byte)0), PixelAt(result, 0));
        Assert.Equal(((byte)255, (byte)0, (byte)0), PixelAt(result, 1));
        Assert.Equal(((byte)0, (byte)0, (byte)255), PixelAt(result, 2));
        Assert.Equal(((byte)255, (byte)255, (byte)255), PixelAt(result, 3));
    }

    [Fact]
    public void Showing_one_revision_draws_all_its_lines_in_its_colour()
    {
        var oldOnly = OverlayCompare.Combine(OldPage, NewPage, showOld: true, showNew: false);
        Assert.Equal(((byte)255, (byte)0, (byte)0), PixelAt(oldOnly, 0));
        Assert.Equal(((byte)255, (byte)255, (byte)255), PixelAt(oldOnly, 2));

        var newOnly = OverlayCompare.Combine(OldPage, NewPage, showOld: false, showNew: true);
        Assert.Equal(((byte)0, (byte)0, (byte)255), PixelAt(newOnly, 0));
        Assert.Equal(((byte)255, (byte)255, (byte)255), PixelAt(newOnly, 1));
    }

    [Fact]
    public void Smaller_old_page_counts_as_paper_outside_its_edge()
    {
        var smallOld = new PageBitmap([.. Ink], 1, 1);
        var result = OverlayCompare.Combine(smallOld, NewPage, showOld: true, showNew: true);
        Assert.Equal(((byte)0, (byte)0, (byte)255), PixelAt(result, 2)); // new-only line, no old pixel there
    }
    [Fact]
    public void Alignment_from_two_points_finds_scale_and_offset()
    {
        // The old sheet was exported at half size and shifted by (10, 20).
        var alignment = PageAlignment.FromPoints(old1: (60, 70), old2: (110, 70), new1: (100, 100), new2: (200, 100));
        Assert.Equal(0.5, alignment.Scale, 6);
        Assert.Equal(10, alignment.OffsetX, 6);
        Assert.Equal(20, alignment.OffsetY, 6);
        Assert.Equal(new PageAlignment(0.5, 30, 60), alignment.InPixels(3));
    }

    [Fact]
    public void Alignment_rejects_two_identical_points() =>
        Assert.Throws<ArgumentException>(() => PageAlignment.FromPoints((5, 5), (5, 5), (0, 0), (10, 0)));

    [Fact]
    public void Aligned_old_line_lands_on_the_new_line_and_shows_black()
    {
        // The same line, two pixels further right on the old sheet.
        var shiftedOld = new PageBitmap([.. Paper, .. Paper, .. Ink, .. Paper], 4, 1);
        var newLine = new PageBitmap([.. Ink, .. Paper, .. Paper, .. Paper], 4, 1);

        var unaligned = OverlayCompare.Combine(shiftedOld, newLine, showOld: true, showNew: true);
        Assert.Equal(((byte)0, (byte)0, (byte)255), PixelAt(unaligned, 0)); // looks like a change

        var aligned = OverlayCompare.Combine(shiftedOld, newLine, new PageAlignment(1, 2, 0), showOld: true, showNew: true);
        Assert.Equal(((byte)0, (byte)0, (byte)0), PixelAt(aligned, 0));
        Assert.Equal(((byte)255, (byte)255, (byte)255), PixelAt(aligned, 2));
    }
}
