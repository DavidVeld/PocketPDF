using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PocketPDF.Core;

namespace PocketPDF;

/// <summary>
/// The system's fonts, through WPF: measures text in them, and gives the outlines of its letters, which the screen
/// draws and the saved file fills. Both come from the same outlines, so the text looks the same on screen and on paper.
/// </summary>
sealed class SystemFontSource : IFontSource
{
    static FormattedText Format(string family, string line, double fontSize) =>
        new(line, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(family), fontSize, Brushes.Black, pixelsPerDip: 1);

    public double LineWidth(string family, string line, double fontSize) => Format(family, line, fontSize).WidthIncludingTrailingWhitespace;

    /// <summary>The font's own line spacing centred in the line, as WPF spaces lines.</summary>
    public double BaselinePerFontSize(string family)
    {
        var fontFamily = new FontFamily(family);
        return (TextLayout.LineHeightPerFontSize - fontFamily.LineSpacing) / 2 + fontFamily.Baseline;
    }

    /// <summary>The letters of a line, the baseline starting at (<paramref name="x"/>, <paramref name="baselineY"/>).</summary>
    public static Geometry Letters(string family, string line, double fontSize, double x, double baselineY)
    {
        var text = Format(family, line, fontSize);
        return text.BuildGeometry(new Point(x, baselineY - text.Baseline));
    }

    /// <summary>All lines of a label, its top-left corner at (0, 0).</summary>
    public static Geometry Letters(string family, string text, double fontSize)
    {
        var letters = new GeometryGroup { FillRule = FillRule.Nonzero };
        var lines = TextLayout.Lines(text);
        double baseline = new SystemFontSource().BaselinePerFontSize(family);
        for (int i = 0; i < lines.Length; i++)
            letters.Children.Add(Letters(family, lines[i], fontSize, 0, (i * TextLayout.LineHeightPerFontSize + baseline) * fontSize));
        return letters;
    }

    public IReadOnlyList<IReadOnlyList<ViewPoint>> Outlines(string family, string line, double fontSize, ViewPoint baselineStart)
    {
        // Straight pieces 0.01 pt long at most: finer than any printer.
        var flat = Letters(family, line, fontSize, baselineStart.X, baselineStart.Y).GetFlattenedPathGeometry(0.01, ToleranceType.Absolute);
        return flat.Figures.Select(figure =>
        {
            var points = new List<ViewPoint> { new(figure.StartPoint.X, figure.StartPoint.Y) };
            foreach (var segment in figure.Segments)
            {
                if (segment is System.Windows.Media.LineSegment line) points.Add(new(line.Point.X, line.Point.Y));
                else if (segment is PolyLineSegment polyline) points.AddRange(polyline.Points.Select(point => new ViewPoint(point.X, point.Y)));
            }
            return (IReadOnlyList<ViewPoint>)points;
        }).ToList();
    }
}
