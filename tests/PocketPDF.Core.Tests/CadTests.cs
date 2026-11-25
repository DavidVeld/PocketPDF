using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PocketPDF.Core.Tests;

public class CadTests
{
    static CadLineMarkup Line(params double[] xy) =>
        new() { Points = Enumerable.Range(0, xy.Length / 2).Select(i => new PdfPoint(xy[2 * i], xy[2 * i + 1])).ToList() };

    static bool Near(PdfPoint point, double x, double y) => Math.Abs(point.X - x) < 1e-6 && Math.Abs(point.Y - y) < 1e-6;

    [Fact]
    public void Offset_copies_a_polyline_parallel_with_mitred_corners_on_the_picked_side()
    {
        var corner = Line(0, 0, 100, 0, 100, 100);
        var inside = (CadLineMarkup)CadGeometry.Offset(corner, 10, new PdfPoint(50, 5))!;
        Assert.True(Near(inside.Points[0], 0, 10) && Near(inside.Points[1], 90, 10) && Near(inside.Points[2], 90, 100));

        var outside = (CadLineMarkup)CadGeometry.Offset(corner, 10, new PdfPoint(50, -5))!;
        Assert.True(Near(outside.Points[1], 110, -10));

        var circle = new CadCircleMarkup { Center = new(0, 0), Radius = 20 };
        Assert.Equal(30, ((CadCircleMarkup)CadGeometry.Offset(circle, 10, new PdfPoint(25, 0))!).Radius, 6);
        Assert.Equal(10, ((CadCircleMarkup)CadGeometry.Offset(circle, 10, new PdfPoint(5, 0))!).Radius, 6);
        Assert.Null(CadGeometry.Offset(circle, 30, new PdfPoint(5, 0)));
    }

    [Fact]
    public void Extend_lengthens_the_nearer_end_to_the_next_line_or_the_page_edge()
    {
        var page = new PdfRect(0, 0, 200, 100);
        var wall = Line(80, 0, 80, 100);
        var farWall = Line(150, 0, 150, 100);
        var line = Line(10, 50, 40, 50);
        var toWall = CadGeometry.Extend(line, [wall, farWall, line], new PdfPoint(38, 50), page)!;
        Assert.True(Near(toWall[0], 10, 50) && Near(toWall[1], 80, 50));
        line.Points = toWall; // now on the wall: extending again goes on to the next one
        Assert.True(Near(CadGeometry.Extend(line, [wall, farWall], new PdfPoint(79, 50), page)![1], 150, 50));
        Assert.True(Near(CadGeometry.Extend(line, [wall], new PdfPoint(12, 50), page)![0], 0, 50)); // the start end, nothing that way: the page edge
        Assert.Null(CadGeometry.Extend(new CadCircleMarkup { Center = new(100, 50), Radius = 10 }, [wall], new PdfPoint(110, 50), page));
    }

    [Fact]
    public void Crossings_are_where_a_line_runs_through_a_window_edge()
    {
        var crossings = CadGeometry.Crossings(Line(0, 50, 100, 50), new PdfRect(20, 40, 60, 60));
        Assert.Equal(2, crossings.Count);
        Assert.Contains(crossings, point => Near(point, 20, 50));
        Assert.Contains(crossings, point => Near(point, 60, 50));
        Assert.Empty(CadGeometry.Crossings(Line(30, 50, 40, 50), new PdfRect(20, 40, 60, 60))); // wholly inside
    }

    [Fact]
    public void Trim_cuts_out_the_piece_between_crossings_or_to_the_end()
    {
        var line = Line(0, 0, 100, 0);
        CadMarkup[] cutters = [line, Line(30, -10, 30, 10), Line(60, -10, 60, 10)];

        var middle = CadGeometry.Trim(line, cutters, new PdfPoint(45, 1))!;
        Assert.Equal(2, middle.Count);
        var (left, right) = ((CadLineMarkup)middle[0], (CadLineMarkup)middle[1]);
        Assert.True(Near(left.Points[0], 0, 0) && Near(left.Points[^1], 30, 0));
        Assert.True(Near(right.Points[0], 60, 0) && Near(right.Points[^1], 100, 0));

        var end = Assert.Single(CadGeometry.Trim(line, cutters, new PdfPoint(90, 0))!);
        Assert.True(Near(((CadLineMarkup)end).Points[^1], 60, 0));

        Assert.Empty(CadGeometry.Trim(line, [line], new PdfPoint(50, 0))!); // nothing crosses it: deleted
    }

    [Fact]
    public void A_trimmed_circle_leaves_the_arc_on_the_other_side()
    {
        var circle = new CadCircleMarkup { Center = new(0, 0), Radius = 10 };
        var arc = (CadLineMarkup)Assert.Single(CadGeometry.Trim(circle, [circle, Line(5, -20, 5, 20)], new PdfPoint(10, 0))!);
        Assert.False(arc.Closed);
        Assert.All(arc.Points, point => Assert.True(point.X <= 5 + 1e-6));
        Assert.Null(CadGeometry.Trim(circle, [circle, Line(10, -20, 10, 20)], new PdfPoint(-10, 0))); // touched once: can't trim a closed shape
    }

    [Fact]
    public void The_region_around_a_point_is_the_smallest_one_closed_by_lines_even_if_they_overshoot()
    {
        // A 100 x 100 square of four overshooting lines, split in two by a line at x = 40.
        CadMarkup[] edges =
        [
            Line(-10, 0, 110, 0), Line(-10, 100, 110, 100), Line(0, -10, 0, 110), Line(100, -10, 100, 110), Line(40, 0, 40, 100),
        ];
        var right = CadGeometry.RegionAround(edges, new PdfPoint(70, 50))!;
        Assert.Equal(60 * 100, Math.Abs(Area(right)), 3);
        Assert.Null(CadGeometry.RegionAround(edges, new PdfPoint(200, 50)));
    }

    [Fact]
    public void Hatch_lines_fill_the_region_and_stay_inside_it()
    {
        List<PdfPoint> square = [new(0, 0), new(100, 0), new(100, 100), new(0, 100)];
        var lines = CadGeometry.HatchLines(square, 10, 45);
        Assert.InRange(lines.Count, 13, 15); // the square's diagonal (141) over the spacing
        Assert.All(lines, line => Assert.True(line.A.X is >= -1e-6 and <= 100 + 1e-6 && line.B.Y is >= -1e-6 and <= 100 + 1e-6));

        var hatch = new CadHatchMarkup { Boundary = square, Spacing = 10 };
        int oneWay = CadGeometry.HatchStrokes(hatch).Count;
        hatch.Pattern = HatchPattern.Crosshatch;
        Assert.InRange(CadGeometry.HatchStrokes(hatch).Count, 2 * oneWay - 1, 2 * oneWay + 1); // the grid can fit one line more one way
        hatch.Pattern = HatchPattern.Solid;
        Assert.Empty(CadGeometry.HatchStrokes(hatch));
    }

    [Fact]
    public void Joined_lines_meet_in_clean_corners_and_close_when_they_go_all_round()
    {
        // A 100 x 50 box from four trimmed lines, picked in any order and direction: ends overshoot, fall short or meet.
        CadLineMarkup[] sides = [Line(-3, 0, 102, 0), Line(0, 50, 0, 2), Line(100, -2, 100, 48), Line(97, 50, 1, 50)];
        var box = CadGeometry.Join(sides, tolerance: 1)!;
        Assert.True(box.Closed);
        Assert.Equal(4, box.Points.Count);
        foreach (var (x, y) in new[] { (0.0, 0.0), (100.0, 0.0), (100.0, 50.0), (0.0, 50.0) })
            Assert.Contains(box.Points, point => Near(point, x, y));
        Assert.Equal(100 * 50, Math.Abs(Area(box.Points)), 6);

        var corner = CadGeometry.Join([Line(0, 0, 98, 0), Line(100, 3, 100, 60)], tolerance: 1)!; // an L stays open
        Assert.False(corner.Closed);
        Assert.Equal(3, corner.Points.Count);
        Assert.True(Near(corner.Points[1], 100, 0));

        Assert.Null(CadGeometry.Join([Line(0, 0, 10, 0)], tolerance: 1));
    }

    [Theory]
    [InlineData("600", 600)]
    [InlineData(" 60 cm", 600)]
    [InlineData("0,6m", 600)]
    [InlineData("1.25 M", 1250)]
    public void Typed_lengths_read_in_mm_unless_a_unit_is_given(string typed, double mm) =>
        Assert.Equal(mm, DrawingScale.ParseLengthMm(typed)!.Value, 9);

    [Theory]
    [InlineData("en-GB", "2,400", 2400)] // as FormatLength shows it
    [InlineData("en-GB", "1,250.5", 1250.5)]
    [InlineData("en-GB", "1,5", 1.5)]
    [InlineData("en-GB", "0,6m", 600)]
    [InlineData("de-DE", "2.400", 2400)]
    [InlineData("de-DE", "2,4", 2.4)]
    [InlineData("de-DE", "2,400", 2.4)]
    public void Thousands_read_the_way_lengths_are_shown(string culture, string typed, double mm)
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
        try
        {
            Assert.Equal(mm, DrawingScale.ParseLengthMm(typed)!.Value, 9);
            if (mm == Math.Round(mm)) Assert.Equal(mm, DrawingScale.ParseLengthMm(DrawingScale.FormatLength(mm))!.Value, 9); // what is shown reads back
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("5 parsecs")]
    public void Anything_else_is_not_a_length(string typed) => Assert.Null(DrawingScale.ParseLengthMm(typed));

    [Fact]
    public void Text_in_a_box_wraps_at_spaces_to_its_width()
    {
        Assert.Equal("one two\nthree\nfour", TextLayout.Wrap("one two three\nfour", 10, TextLayout.LineWidth("one two", 10) + 0.1));
        Assert.Equal("unbreakable", TextLayout.Wrap("unbreakable", 10, 5)); // too long for the box: keeps its own line
    }

    [Fact]
    public void Cad_entities_load_back_the_same_and_sit_on_the_cad_layer()
    {
        using var source = TestPdf.Create();
        using var saved = new TestPdf();
        List<Markup> markups =
        [
            new CadLineMarkup { Points = [new(10, 10), new(60, 10), new(60, 50)], Closed = true, Color = "#000000", LineWidth = 0.5 },
            new CadCircleMarkup { Center = new(100, 60), Radius = 25 },
            new CadHatchMarkup { Boundary = [new(10, 10), new(60, 10), new(60, 50)], LineWidth = 0.25 },
            new CadHatchMarkup { Boundary = [new(70, 10), new(90, 10), new(90, 50)], Pattern = HatchPattern.Crosshatch, Color = "#1E88E5" },
            new CadHatchMarkup { Boundary = [new(100, 10), new(120, 10), new(120, 50)], Pattern = HatchPattern.Solid, Color = "#43A047" },
            new CadDimensionMarkup { Start = new(10, 100), End = new(60, 130), Offset = -15, MmPerPoint = 20, Font = "Courier New" },
            new CadDimensionMarkup { Start = new(100, 100), End = new(160, 100), Offset = 10, MmPerPoint = 5, Ending = LineEnding.Arrow },
        ];
        MarkupFile.Save(MarkupFile.Load(source.FilePath), new Dictionary<int, List<Markup>> { [1] = markups }, saved.FilePath);

        var reloaded = MarkupFile.Load(saved.FilePath);
        static string Values(Markup markup) => System.Text.Json.JsonSerializer.Serialize(markup, markup.GetType());
        Assert.Equal(markups.Select(Values), reloaded.MarkupsByPage[1].Select(Values));
        Assert.Equal(7, MarkupFile.AnnotationsToHide(reloaded).Count); // the markup layer draws them, so the layer can be switched off

        using var document = PdfReader.Open(saved.FilePath, PdfDocumentOpenMode.Import);
        var layers = document.Internals.Catalog.Elements.GetDictionary("/OCProperties")!.Elements.GetArray("/OCGs")!;
        var layer = Assert.Single(layers.Elements.Select(item => ((PdfSharp.Pdf.Advanced.PdfReference)item).Value).OfType<PdfDictionary>());
        Assert.Equal("CAD", layer.Elements.GetString("/Name"));
        var annotations = document.Pages[1].Elements.GetArray("/Annots")!;
        Assert.All(annotations.Elements, item => Assert.Same(layer, ((PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)item).Value).Elements.GetDictionary("/OC")));

        // Other viewers draw them from their appearance, with the layer on.
        var (pixels, _, _) = new PdfRenderer(File.ReadAllBytes(saved.FilePath)).Render(1, scale: 2);
        Assert.Contains(Enumerable.Range(0, pixels.Length / 4), i => pixels[i * 4 + 3] > 128);
    }

    [Fact]
    public void Closed_shapes_keep_their_fill_colour_and_type()
    {
        using var source = TestPdf.Create();
        using var saved = new TestPdf();
        List<Markup> markups =
        [
            new RectangleMarkup { Corner1 = new(10, 10), Corner2 = new(60, 50), FillColor = "#FFFF00" },
            new RectangleMarkup { Corner1 = new(70, 10), Corner2 = new(120, 50), FillColor = "#E53935", FillPattern = HatchPattern.Crosshatch },
            new CloudMarkup { Vertices = [new(10, 100), new(60, 140)], Shape = AreaShape.Box, Style = AreaLineStyle.Solid, FillColor = "#1E88E5", FillPattern = HatchPattern.Lines },
            new CadLineMarkup { Points = [new(130, 10), new(180, 10), new(180, 50)], Closed = true, FillColor = "#43A047", FillPattern = HatchPattern.Lines },
            new CadCircleMarkup { Center = new(100, 160), Radius = 25, FillColor = "#000000" },
            new CadCircleMarkup { Center = new(160, 160), Radius = 20 },
        ];
        MarkupFile.Save(MarkupFile.Load(source.FilePath), new Dictionary<int, List<Markup>> { [1] = markups }, saved.FilePath);

        var reloaded = MarkupFile.Load(saved.FilePath).MarkupsByPage[1];
        static string Fill(Markup markup) => markup is IFillable { FillColor: { } color } shape ? $"{color} {shape.FillPattern}" : "none";
        Assert.Equal(markups.Select(Fill), reloaded.Select(Fill));
    }

    static readonly PageGeometry Page = new(0, 0, 600, 600, 0);

    static CadDimensionMarkup Dimension(double x1, double y1, double x2, double y2) => new() { Start = new(x1, y1), End = new(x2, y2), MmPerPoint = 1 };

    [Fact]
    public void A_new_dimension_value_stretches_the_shape_it_measures_and_the_dimensions_on_it_follow()
    {
        var box = Line(0, 0, 100, 0, 100, 50, 0, 50);
        box.Closed = true;
        var width = Dimension(0, 0, 100, 0);
        var toTheSide = Dimension(100, 50, 130, 50); // nothing at its far end: it follows the box's corner
        Assert.True(Dimensioning.Resize(width, 150, [box, width, toTheSide], Page));
        Assert.True(Near(box.Points[0], 0, 0) && Near(box.Points[1], 150, 0) && Near(box.Points[2], 150, 50) && Near(box.Points[3], 0, 50));
        Assert.True(Near(width.End, 150, 0) && Near(width.Start, 0, 0));
        Assert.Equal(150, width.LengthMm, 6);
        Assert.True(Near(toTheSide.Start, 150, 50) && Near(toTheSide.End, 130, 50));
    }

    [Fact]
    public void A_new_dimension_value_moves_a_shape_only_its_far_end_sits_on()
    {
        var box = Line(0, 0, 100, 0, 100, 50);
        var post = Line(200, 0, 200, 50);
        var gap = Dimension(100, 0, 200, 0);
        Assert.True(Dimensioning.Resize(gap, 120, [box, post, gap], Page));
        Assert.True(Near(post.Points[0], 220, 0) && Near(post.Points[1], 220, 50));
        Assert.True(Near(box.Points[1], 100, 0));

        var loose = Dimension(300, 300, 400, 300); // a shape only at the start: that one moves back instead
        var stub = Line(300, 300, 300, 350);
        Assert.True(Dimensioning.Resize(loose, 80, [stub, loose], Page));
        Assert.True(Near(stub.Points[0], 320, 300) && Near(loose.Start, 320, 300) && Near(loose.End, 400, 300));

        Assert.False(Dimensioning.Resize(Dimension(500, 500, 550, 500), 80, [box, post], Page)); // nothing at either end
    }

    [Fact]
    public void A_new_radius_or_diameter_grows_the_circle_about_its_centre()
    {
        var circle = new CadCircleMarkup { Center = new(0, 0), Radius = 20 };
        var radius = Dimension(0, 0, 20, 0);
        var diameter = Dimension(0, -20, 0, 20);
        Assert.True(Dimensioning.Resize(radius, 30, [circle, radius, diameter], Page));
        Assert.Equal(30, circle.Radius, 6);
        Assert.True(Near(radius.End, 30, 0) && Near(diameter.Start, 0, -30) && Near(diameter.End, 0, 30));
        Assert.Contains((new ViewPoint(30, 600), new ViewPoint(30, 600)), Dimensioning.SnapSegments(circle, Page)); // its quarter points snap
    }

    static double Area(List<PdfPoint> points) =>
        points.Select((point, i) => point.X * points[(i + 1) % points.Count].Y - points[(i + 1) % points.Count].X * point.Y).Sum() / 2;

    [Theory]
    [InlineData(100, 3, 0)] // nearly flat: 0°
    [InlineData(100, 55, 30)]
    [InlineData(100, 95, 45)]
    [InlineData(55, 100, 60)]
    [InlineData(3, 100, 90)]
    [InlineData(-100, -95, -135)]
    public void Shift_turns_a_line_to_the_nearest_30_or_45_degrees_and_keeps_its_length(double x, double y, double degrees)
    {
        var end = CadGeometry.RoundedAngle(new ViewPoint(10, 20), new ViewPoint(10 + x, 20 + y));
        double radians = degrees * Math.PI / 180, length = Math.Sqrt(x * x + y * y);
        Assert.Equal(10 + length * Math.Cos(radians), end.X, 6);
        Assert.Equal(20 + length * Math.Sin(radians), end.Y, 6);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(20, 1)]
    [InlineData(25, 10)]
    [InlineData(100, 10)]
    public void Shift_rounds_sizes_to_whole_mm_up_to_1_to_20_and_to_tens_beyond(double ratio, double stepMm) =>
        Assert.Equal(stepMm, DrawingScale.RoundingStepMm(DrawingScale.FromRatio(ratio)));

    [Fact]
    public void Shift_rounds_a_rectangles_width_and_height_on_either_side_of_its_corner()
    {
        var corner = CadGeometry.RoundedCorner(new ViewPoint(10, 20), new ViewPoint(10 + 23.6, 20 - 14.4), 10);
        Assert.Equal(new ViewPoint(30, 10), corner);
    }
}
