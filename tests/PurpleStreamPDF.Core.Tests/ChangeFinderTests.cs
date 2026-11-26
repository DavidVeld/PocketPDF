namespace PurpleStreamPDF.Core.Tests;

public class ChangeFinderTests
{
    // 1 pixel per point keeps the numbers readable.
    const int PageSize = 400;

    static PageBitmap BlankOverlay()
    {
        var pixels = new byte[PageSize * PageSize * 4];
        Array.Fill(pixels, (byte)255);
        return new PageBitmap(pixels, PageSize, PageSize);
    }

    static void Paint(PageBitmap overlay, int left, int top, int size, (byte Blue, byte Green, byte Red) colour)
    {
        for (int y = top; y < top + size; y++)
        for (int x = left; x < left + size; x++)
        {
            int offset = (y * overlay.Width + x) * 4;
            (overlay.BgraPixels[offset], overlay.BgraPixels[offset + 1], overlay.BgraPixels[offset + 2]) = colour;
        }
    }

    static readonly (byte, byte, byte) Red = (0, 0, 255), Blue = (255, 0, 0), Black = (0, 0, 0);

    [Fact]
    public void Two_changes_far_apart_get_one_padded_cloud_each()
    {
        var overlay = BlankOverlay();
        Paint(overlay, 50, 50, 10, Red);
        Paint(overlay, 300, 300, 10, Blue);

        var areas = ChangeFinder.FindChangedAreas(overlay, pixelsPerPoint: 1);

        Assert.Equal(2, areas.Count);
        Assert.Contains(new ViewRect(50 - ChangeFinder.Padding, 50 - ChangeFinder.Padding, 60 + ChangeFinder.Padding, 60 + ChangeFinder.Padding), areas);
    }

    [Fact]
    public void Nearby_changes_join_into_one_cloud()
    {
        var overlay = BlankOverlay();
        Paint(overlay, 100, 100, 4, Red);
        Paint(overlay, 110, 104, 4, Blue); // a few points away, like an old and a new position of one line

        var area = Assert.Single(ChangeFinder.FindChangedAreas(overlay, pixelsPerPoint: 1));
        Assert.Equal(100 - ChangeFinder.Padding, area.Left);
        Assert.Equal(108 + ChangeFinder.Padding, area.Bottom);
    }

    [Fact]
    public void Unchanged_black_lines_and_specks_get_no_cloud()
    {
        var overlay = BlankOverlay();
        Paint(overlay, 0, 200, 150, Black);
        Paint(overlay, 350, 20, 1, Red); // one stray pixel

        Assert.Empty(ChangeFinder.FindChangedAreas(overlay, pixelsPerPoint: 1));
    }

    [Fact]
    public void Clouds_stay_on_the_page()
    {
        var overlay = BlankOverlay();
        Paint(overlay, 0, 0, 5, Red);

        var area = Assert.Single(ChangeFinder.FindChangedAreas(overlay, pixelsPerPoint: 1));
        Assert.Equal(new ViewRect(0, 0, 5 + ChangeFinder.Padding, 5 + ChangeFinder.Padding), area);
    }
}
