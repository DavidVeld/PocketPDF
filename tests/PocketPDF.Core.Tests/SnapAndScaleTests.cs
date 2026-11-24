namespace PocketPDF.Core.Tests;

public class SnapAndScaleTests
{
    // A cross (two lines meeting at (100, 100)) and a separate horizontal line from (200, 50) to (300, 50).
    static readonly SnapIndex Drawing = new([
        (new ViewPoint(50, 100), new ViewPoint(150, 100)),
        (new ViewPoint(100, 50), new ViewPoint(100, 150)),
        (new ViewPoint(200, 50), new ViewPoint(300, 50)),
    ]);

    [Fact]
    public void End_point_beats_everything_else_nearby()
    {
        Assert.Equal(new SnapPoint(new(150, 100), SnapKind.Endpoint), Drawing.Snap(new(147, 103), radius: 8));
    }

    [Fact]
    public void Lines_crossing_give_an_intersection()
    {
        Assert.Equal(new SnapPoint(new(100, 100), SnapKind.Intersection), Drawing.Snap(new(104, 97), radius: 8));
    }

    [Fact]
    public void Middle_of_a_line()
    {
        Assert.Equal(new SnapPoint(new(250, 50), SnapKind.Midpoint), Drawing.Snap(new(253, 46), radius: 8));
    }

    [Fact]
    public void Otherwise_the_nearest_point_on_a_line()
    {
        Assert.Equal(new SnapPoint(new(225, 50), SnapKind.Nearest), Drawing.Snap(new(225, 55), radius: 8));
    }

    [Fact]
    public void Nothing_near_means_no_snap()
    {
        Assert.Null(Drawing.Snap(new(250, 80), radius: 8));
    }

    [Fact]
    public void Long_diagonal_lines_are_found_anywhere_along_them()
    {
        var index = new SnapIndex([(new ViewPoint(0, 0), new ViewPoint(2000, 1500))]);
        var snap = index.Snap(new ViewPoint(1203, 897), radius: 5);
        Assert.Equal(SnapKind.Nearest, snap?.Kind);
        Assert.Equal(1200 * 0.75, snap!.Value.Point.Y, 0.5);
    }

    [Fact]
    public void A_dash_dot_gridline_is_picked_whole()
    {
        // Dashes 20 long with gaps of 5 holding a dot, from x 0 to 222.5 along y 100; a far piece in line at 400; a line across.
        var pieces = Enumerable.Range(0, 9).SelectMany(i => new[]
        {
            (new ViewPoint(i * 25, 100), new ViewPoint(i * 25 + 20, 100)),
            (new ViewPoint(i * 25 + 22.5, 100), new ViewPoint(i * 25 + 22.5, 100)),
        }).Append((new ViewPoint(400, 100), new ViewPoint(450, 100))).Append((new ViewPoint(110, 0), new ViewPoint(110, 200)));
        var line = new SnapIndex(pieces).LineThrough(new ViewPoint(57, 103), radius: 8);
        Assert.Equal((new ViewPoint(0, 100), new ViewPoint(222.5, 100)), line); // to the last dot; the piece at 400 is too far
        Assert.Null(Drawing.LineThrough(new(250, 80), radius: 8));
    }

    [Fact]
    public void Scales_from_ratios_and_calibration()
    {
        Assert.Equal("1:50", DrawingScale.Describe(DrawingScale.FromRatio(50)));
        // 1:100 on paper: 72 pt is one inch of paper, 2540 mm real.
        Assert.Equal(2540, 72 * DrawingScale.FromRatio(100), 6);
        // Calibrated: a 200 pt line is 3500 mm, so 17.5 mm per point, about 1:49.6.
        double calibrated = DrawingScale.FromCalibration(200, 3500);
        Assert.Equal(17.5, calibrated, 6);
        Assert.Equal("1:49.61", DrawingScale.Describe(calibrated));
        Assert.Equal(304.8, DrawingScale.MmPerUnit("ft"));
        Assert.Null(DrawingScale.MmPerUnit("furlong"));
    }
}
