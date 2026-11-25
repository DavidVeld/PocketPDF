using System.Globalization;
using System.Text;

namespace PocketPDF.Core;

/// <summary>
/// Builds the drawing commands of an annotation's appearance stream (/AP). Coordinates are given in view space;
/// one "cm" at the start maps them onto the page, so the result looks the same as on screen at any page rotation.
/// </summary>
sealed class AppearanceBuilder
{
    public const string FontResourceName = "Helv";
    public const string HighlightStateName = "Highlight";
    public const string SheetImageName = "Sheet";

    readonly StringBuilder _commands = new();

    public AppearanceBuilder(PageGeometry page)
    {
        Append(string.Join(' ', page.ViewToPdfMatrix().Select(Number)) + " cm");
    }

    public byte[] ToBytes() => Encoding.Latin1.GetBytes(_commands.ToString());

    public AppearanceBuilder Stroke(string color, double lineWidth) =>
        Append($"{ColorComponents(color)} RG {Number(lineWidth)} w 1 j");

    /// <summary>Draws the image <see cref="SheetImageName"/> stretched over <paramref name="box"/>, upright as seen.</summary>
    public AppearanceBuilder Image(ViewRect box) =>
        Append($"q {Number(box.Width)} 0 0 {Number(-box.Height)} {Number(box.Left)} {Number(box.Bottom)} cm /{SheetImageName} Do Q"); // the image's y runs up

    public AppearanceBuilder Fill(string color) => Append($"{ColorComponents(color)} rg");

    public AppearanceBuilder Line(ViewPoint from, ViewPoint to) =>
        Append($"{Number(from.X)} {Number(from.Y)} m {Number(to.X)} {Number(to.Y)} l S");

    /// <summary>A stroke through the points with round ends, like a pen.</summary>
    public AppearanceBuilder Polyline(IReadOnlyList<ViewPoint> points)
    {
        Append($"1 J {Number(points[0].X)} {Number(points[0].Y)} m");
        foreach (var point in points.Skip(1)) Append($"{Number(point.X)} {Number(point.Y)} l");
        return Append("S");
    }

    /// <summary>A line from <paramref name="from"/> to <paramref name="to"/>, ending at <paramref name="to"/> as asked. Returns the points the ending reaches.</summary>
    public ViewPoint[] Leader(ViewPoint from, ViewPoint to, LineEnding ending, string color, double lineWidth)
    {
        Stroke(color, lineWidth).Line(from, ending == LineEnding.Arrow ? LabelGeometry.ArrowLineEnd(from, to, lineWidth) : to);
        switch (ending)
        {
            case LineEnding.Arrow:
                var head = LabelGeometry.ArrowHead(from, to, lineWidth);
                Fill(color).FilledPolygon(head);
                return head;
            case LineEnding.Dot:
                double radius = LabelGeometry.DotRadius(lineWidth);
                Fill(color).FilledCircle(to, radius);
                return new ViewRect(to.X - radius, to.Y - radius, to.X + radius, to.Y + radius).Corners();
            case LineEnding.Tick:
                var (a, b) = LabelGeometry.Tick(from, to, lineWidth);
                Line(a, b);
                return [a, b];
            default:
                return [];
        }
    }

    /// <summary>A filled circle, as four Bezier quarters.</summary>
    public AppearanceBuilder FilledCircle(ViewPoint center, double radius)
    {
        double k = radius * 0.5523; // handle length for a quarter circle
        var (x, y) = (center.X, center.Y);
        Append($"{Number(x + radius)} {Number(y)} m");
        Append($"{Number(x + radius)} {Number(y + k)} {Number(x + k)} {Number(y + radius)} {Number(x)} {Number(y + radius)} c");
        Append($"{Number(x - k)} {Number(y + radius)} {Number(x - radius)} {Number(y + k)} {Number(x - radius)} {Number(y)} c");
        Append($"{Number(x - radius)} {Number(y - k)} {Number(x - k)} {Number(y - radius)} {Number(x)} {Number(y - radius)} c");
        Append($"{Number(x + k)} {Number(y - radius)} {Number(x + radius)} {Number(y - k)} {Number(x + radius)} {Number(y)} c");
        return Append("h f");
    }

    public AppearanceBuilder FilledPolygon(IReadOnlyList<ViewPoint> corners)
    {
        Append($"{Number(corners[0].X)} {Number(corners[0].Y)} m");
        foreach (var corner in corners.Skip(1)) Append($"{Number(corner.X)} {Number(corner.Y)} l");
        return Append("h f");
    }

    /// <summary>Strokes a rectangle so its outer edge sits on <paramref name="box"/>, like a WPF border.</summary>
    public AppearanceBuilder BorderInside(ViewRect box, double lineWidth)
    {
        var inner = box.Inflate(-lineWidth / 2);
        return Append($"{Number(inner.Left)} {Number(inner.Top)} {Number(inner.Width)} {Number(inner.Height)} re S");
    }

    /// <summary>A thick see-through stroke with round ends, multiplied onto the page like a marker pen.</summary>
    public AppearanceBuilder HighlightStroke(IReadOnlyList<ViewPoint> points, string color, double thickness)
    {
        Append($"q /{HighlightStateName} gs {ColorComponents(color)} RG {Number(thickness)} w 1 J 1 j");
        Append($"{Number(points[0].X)} {Number(points[0].Y)} m");
        foreach (var point in points.Skip(1)) Append($"{Number(point.X)} {Number(point.Y)} l");
        return Append("S Q");
    }

    public AppearanceBuilder Cloud(ViewPoint start, IEnumerable<BezierArc> arcs)
    {
        Append($"{Number(start.X)} {Number(start.Y)} m");
        foreach (var arc in arcs)
            Append($"{Number(arc.Control1.X)} {Number(arc.Control1.Y)} {Number(arc.Control2.X)} {Number(arc.Control2.Y)} {Number(arc.End.X)} {Number(arc.End.Y)} c");
        return Append("h S");
    }

    /// <summary>A closed outline through the corners, solid or dashed (<paramref name="dashes"/> in line widths).</summary>
    public AppearanceBuilder ClosedOutline(IReadOnlyList<ViewPoint> corners, double lineWidth, double[]? dashes)
    {
        Append(dashes == null ? "q" : $"q [{string.Join(' ', dashes.Select(dash => Number(dash * lineWidth)))}] 0 d");
        Append($"{Number(corners[0].X)} {Number(corners[0].Y)} m");
        foreach (var corner in corners.Skip(1)) Append($"{Number(corner.X)} {Number(corner.Y)} l");
        return Append("h S Q");
    }

    /// <summary>
    /// Writes text lines from a top-left corner, in Helvetica, or in <paramref name="font"/> as the outlines of its letters,
    /// turned <paramref name="angleDegrees"/> clockwise about that corner.
    /// </summary>
    public AppearanceBuilder Text(ViewPoint topLeft, string text, double fontSize, string color, string? font = null, double angleDegrees = 0)
    {
        if (angleDegrees == 0) return UprightText(topLeft, text, fontSize, color, font);
        double radians = angleDegrees * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
        var (x, y) = (topLeft.X, topLeft.Y);
        Append($"q {Number(cos)} {Number(sin)} {Number(-sin)} {Number(cos)} {Number(x - cos * x + sin * y)} {Number(y - sin * x - cos * y)} cm");
        return UprightText(topLeft, text, fontSize, color, font).Append("Q");
    }

    AppearanceBuilder UprightText(ViewPoint topLeft, string text, double fontSize, string color, string? font)
    {
        var lines = TextLayout.Lines(text);
        if (TextLayout.SourceFor(font) is { } fonts)
        {
            bool any = false;
            Fill(color);
            for (int i = 0; i < lines.Length; i++)
            {
                double baseline = topLeft.Y + (i * TextLayout.LineHeightPerFontSize + fonts.BaselinePerFontSize(font!)) * fontSize;
                foreach (var outline in fonts.Outlines(font!, lines[i], fontSize, new ViewPoint(topLeft.X, baseline)))
                {
                    if (outline.Count < 3) continue;
                    Append($"{Number(outline[0].X)} {Number(outline[0].Y)} m");
                    foreach (var point in outline.Skip(1)) Append($"{Number(point.X)} {Number(point.Y)} l");
                    Append("h");
                    any = true;
                }
            }
            return any ? Append("f") : this; // all the letters as one path: holes (o, e, a) stay open
        }
        Append($"BT /{FontResourceName} {Number(fontSize)} Tf {ColorComponents(color)} rg");
        for (int i = 0; i < lines.Length; i++)
        {
            double baseline = topLeft.Y + (i * TextLayout.LineHeightPerFontSize + TextLayout.BaselinePerFontSize) * fontSize;
            // The -1 flips the text upright again, because view space has Y pointing down.
            Append($"1 0 0 -1 {Number(topLeft.X)} {Number(baseline)} Tm {LiteralString(lines[i])} Tj");
        }
        return Append("ET");
    }

    AppearanceBuilder Append(string command)
    {
        _commands.Append(command).Append('\n');
        return this;
    }

    public static string ColorComponents(string hexColor)
    {
        int rgb = int.Parse(hexColor.TrimStart('#'), NumberStyles.HexNumber);
        return $"{Number(((rgb >> 16) & 255) / 255.0)} {Number(((rgb >> 8) & 255) / 255.0)} {Number((rgb & 255) / 255.0)}";
    }

    public static string Number(double value) => Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>WinAnsi is Windows-1252: Latin-1 plus ’ “ ” – — • € … and a few more in 0x80–0x9F.</summary>
    static readonly Encoding WinAnsi = CodePagesEncodingProvider.Instance.GetEncoding(1252, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback)!;

    // shortcut: Helvetica in WinAnsi covers Western European text only; anything else shows as "?" (the /Contents text keeps the real characters).
    static string LiteralString(string text)
    {
        var escaped = new StringBuilder("(");
        foreach (byte code in WinAnsi.GetBytes(text))
        {
            char safe = (char)code; // ToBytes writes it back out as this same byte
            if (safe is '(' or ')' or '\\') escaped.Append('\\');
            escaped.Append(safe);
        }
        return escaped.Append(')').ToString();
    }
}
