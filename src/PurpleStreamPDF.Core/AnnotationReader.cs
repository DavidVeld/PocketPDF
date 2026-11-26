using System.Globalization;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;

namespace PurpleStreamPDF.Core;

/// <summary>
/// Reads the annotations PurpleStreamPDF can edit (free text, callouts, clouds, ink, lines, rectangles, measurements) back into markups,
/// whether PurpleStreamPDF or another viewer wrote them. Anything else returns null and stays untouched in the file.
/// </summary>
static partial class AnnotationReader
{
    public static Markup? TryRead(PdfDictionary annotation, PageGeometry page)
    {
        try
        {
            if (annotation.Elements.GetObject(AnnotationWriter.CadKey) is PdfDictionary cad) return ReadCad(annotation, cad);
            if (annotation.Elements.GetObject(AnnotationWriter.SheetKey) is PdfDictionary sheet) return ReadSheet(annotation, sheet);
            var markup = annotation.Elements.GetName("/Subtype") switch
            {
                "/FreeText" => ReadFreeText(annotation, page),
                "/Polygon" when IsCloud(annotation) || annotation.Elements.ContainsKey(AnnotationWriter.AreaKey) => ReadCloud(annotation),
                "/Ink" when IsSeeThrough(annotation) => ReadHighlight(annotation),
                "/Ink" => ReadPen(annotation),
                "/Line" when annotation.Elements.GetName("/IT") == "/LineDimension" => ReadMeasure(annotation, MeasureKind.Distance, "/L"),
                "/Line" when !annotation.Elements.ContainsKey("/IT") => ReadLine(annotation),
                // Cloudy or see-through rectangles stay untouched: redrawing them here would lose the bumps or make them opaque.
                "/Square" when !IsCloud(annotation) && !IsSeeThrough(annotation) => ReadRectangle(annotation),
                "/PolyLine" when annotation.Elements.GetName("/IT") == "/PolyLineDimension" => ReadMeasure(annotation, MeasureKind.Length, "/Vertices"),
                "/Polygon" when annotation.Elements.GetName("/IT") == "/PolygonDimension" => ReadMeasure(annotation, MeasureKind.Area, "/Vertices"),
                _ => null,
            };
            if (markup != null && annotation.Elements.ContainsKey(AnnotationWriter.FontKey)) markup.Font = annotation.Elements.GetString(AnnotationWriter.FontKey);
            if (markup != null && annotation.Elements.ContainsKey("/Subj")) markup.Subject = annotation.Elements.GetString("/Subj");
            return markup;
        }
        catch (Exception exception) when (exception is InvalidCastException or IndexOutOfRangeException or ArgumentException or FormatException)
        {
            return null; // malformed: leave it in the file as it is
        }
    }

    static Markup? ReadCad(PdfDictionary annotation, PdfDictionary cad)
    {
        CadMarkup? markup = cad.Elements.GetName("/Kind") switch
        {
            "/Line" when cad.Elements.GetObject("/Points") is PdfArray points && Points(points) is { Count: >= 2 } line =>
                new CadLineMarkup { Points = line, Closed = cad.Elements.GetBoolean("/Closed") },
            "/Circle" when cad.Elements.GetObject("/Center") is PdfArray center && Points(center) is [var middle] =>
                new CadCircleMarkup { Center = middle, Radius = cad.Elements.GetReal("/Radius") },
            "/Dimension" when cad.Elements.GetObject("/Points") is PdfArray points && Points(points) is [var start, var end] && start != end =>
                new CadDimensionMarkup
                {
                    Start = start, End = end, Offset = cad.Elements.GetReal("/Offset"), MmPerPoint = cad.Elements.GetReal("/Scale"),
                    Ending = annotation.Elements.GetObject("/LE") is PdfArray { Elements.Count: > 0 } ends ? EndingOf(ends.Elements.GetName(0)) : LineEnding.Tick, // older files: ticks
                },
            "/Hatch" when cad.Elements.GetObject("/Points") is PdfArray points && Points(points) is { Count: >= 3 } boundary =>
                new CadHatchMarkup
                {
                    Boundary = boundary, Spacing = cad.Elements.GetReal("/Spacing"), AngleDegrees = cad.Elements.GetReal("/Angle"),
                    Pattern = cad.Elements.ContainsKey("/Pattern") ? Enum.Parse<HatchPattern>(cad.Elements.GetName("/Pattern").TrimStart('/')) : HatchPattern.Lines,
                },
            _ => null,
        };
        if (markup == null) return null;
        if (markup is IFillable shape) ReadFill(annotation, shape);
        markup.Color = ColorOf(annotation) ?? "#000000";
        markup.LineWidth = LineWidthOf(annotation);
        if (annotation.Elements.ContainsKey(AnnotationWriter.FontKey)) markup.Font = annotation.Elements.GetString(AnnotationWriter.FontKey); // a dimension's value
        return markup;
    }

    static Markup? ReadSheet(PdfDictionary annotation, PdfDictionary sheet)
    {
        if (sheet.Elements.GetObject("/Source") is not PdfDictionary { Stream: { } source }
            || sheet.Elements.GetObject("/Corners") is not PdfArray corners || Points(corners) is not [var topLeft, var bottomRight]
            || sheet.Elements.GetObject("/Size") is not PdfArray { Elements.Count: 2 } size
            || sheet.Elements.GetObject("/Clip") is not PdfArray { Elements.Count: 4 } clip) return null;
        return new PlacedSheetMarkup
        {
            Source = source.UnfilteredValue, SourceWidth = size.Elements.GetReal(0), SourceHeight = size.Elements.GetReal(1),
            TopLeft = topLeft, BottomRight = bottomRight,
            ClipLeft = clip.Elements.GetReal(0), ClipTop = clip.Elements.GetReal(1), ClipRight = clip.Elements.GetReal(2), ClipBottom = clip.Elements.GetReal(3),
            Opacity = sheet.Elements.GetReal("/Opacity"), Tinted = sheet.Elements.GetBoolean("/Tinted"),
            Color = ColorOf(annotation) ?? "#1E88E5",
        };
    }

    static Markup ReadFreeText(PdfDictionary annotation, PageGeometry page)
    {
        string text = annotation.Elements.GetString("/Contents").Replace("\r\n", "\n").Replace('\r', '\n');
        string defaultAppearance = annotation.Elements.GetString("/DA");
        double fontSize = FontSizeIn(defaultAppearance) ?? 12;
        string color = FillColorIn(defaultAppearance) ?? ColorOf(annotation) ?? "#E53935";
        var textBox = page.ToView(TextBox(annotation));

        if (annotation.Elements.GetName("/IT") == "/FreeTextCallout" && annotation.Elements.GetObject("/CL") is PdfArray calloutLine)
        {
            return new CalloutMarkup
            {
                ArrowTip = new PdfPoint(calloutLine.Elements.GetReal(0), calloutLine.Elements.GetReal(1)),
                TextPosition = page.ToPdf(textBox.TopLeft),
                Text = text, FontSize = fontSize, Color = color, LineWidth = LineWidthOf(annotation),
                HasBorder = !annotation.Elements.ContainsKey(AnnotationWriter.NoBorderKey),
                Ending = annotation.Elements.GetName("/LE") is "" ? LineEnding.Arrow : EndingOf(annotation.Elements.GetName("/LE")), // as before: no /LE read as an arrow
            };
        }
        return new TextMarkup { TextPosition = page.ToPdf(textBox.TopLeft), Text = text, FontSize = fontSize, Color = color };
    }

    static Markup? ReadCloud(PdfDictionary annotation)
    {
        if (annotation.Elements.GetObject("/Vertices") is not PdfArray numbers) return null;
        var vertices = Points(numbers);
        if (vertices.Count > 3 && vertices[0] == vertices[^1]) vertices.RemoveAt(vertices.Count - 1); // some writers repeat the first point
        if (vertices.Count < 3) return null;
        var cloud = new CloudMarkup { Vertices = vertices, Color = ColorOf(annotation) ?? "#E53935", LineWidth = LineWidthOf(annotation) };
        ReadFill(annotation, cloud);
        if (annotation.Elements.GetObject(AnnotationWriter.AreaKey) is PdfDictionary area && area.Elements.GetObject("/Points") is PdfArray points)
        {
            cloud.Shape = Enum.Parse<AreaShape>(area.Elements.GetName("/Shape").TrimStart('/'));
            cloud.Style = Enum.Parse<AreaLineStyle>(area.Elements.GetName("/Style").TrimStart('/'));
            cloud.Vertices = Points(points);
        }
        if (annotation.Elements.GetObject(AnnotationWriter.CloudTagKey) is PdfDictionary tag && tag.Elements.GetObject("/At") is PdfArray at)
        {
            cloud.TextPosition = Points(at)[0];
            cloud.Text = annotation.Elements.GetString("/Contents").Replace("\r\n", "\n").Replace('\r', '\n');
            cloud.FontSize = tag.Elements.GetReal("/Size");
            cloud.Ending = tag.Elements.ContainsKey("/End") ? EndingOf(tag.Elements.GetName("/End")) : tag.Elements.GetBoolean("/Arrow") ? LineEnding.Arrow : LineEnding.None;
        }
        return cloud;
    }

    /// <summary>A single see-through ink stroke is a highlight. Multi-stroke ink stays untouched.</summary>
    static Markup? ReadHighlight(PdfDictionary annotation)
    {
        if (SingleInkStroke(annotation) is not { } points) return null;
        return new HighlightMarkup
        {
            Points = points,
            Color = ColorOf(annotation) ?? HighlightMarkup.DefaultColor,
            Thickness = LineWidthOf(annotation),
        };
    }

    /// <summary>A single opaque ink stroke is a pen markup. Multi-stroke ink stays untouched.</summary>
    static Markup? ReadPen(PdfDictionary annotation) =>
        SingleInkStroke(annotation) is { } points
            ? new PenMarkup { Points = points, Color = ColorOf(annotation) ?? "#E53935", LineWidth = LineWidthOf(annotation) }
            : null;

    static List<PdfPoint>? SingleInkStroke(PdfDictionary annotation)
    {
        if (annotation.Elements.GetObject("/InkList") is not PdfArray { Elements.Count: 1 } strokes) return null;
        if (Resolve(strokes.Elements[0]) is not PdfArray numbers) return null;
        var points = Points(numbers);
        return points.Count == 0 ? null : points;
    }

    static Markup? ReadLine(PdfDictionary annotation)
    {
        if (annotation.Elements.GetObject("/L") is not PdfArray numbers || Points(numbers) is not [var start, var end]) return null;
        var lineEndings = annotation.Elements.GetObject("/LE") as PdfArray;
        return new LineMarkup
        {
            Start = start, End = end,
            Ending = lineEndings is { Elements.Count: > 0 } ? EndingOf(lineEndings.Elements.GetName(0)) : LineEnding.None,
            Color = ColorOf(annotation) ?? "#E53935", LineWidth = LineWidthOf(annotation),
        };
    }

    /// <summary>/Rect less /RD is the outer edge of the stroke; the rectangle's corners are on the middle of it. No border (width 0) is a mask.</summary>
    static Markup ReadRectangle(PdfDictionary annotation)
    {
        double lineWidth = LineWidthOf(annotation), half = lineWidth / 2;
        var outerEdge = TextBox(annotation);
        var rectangle = new RectangleMarkup
        {
            Corner1 = new(outerEdge.Left + half, outerEdge.Bottom + half),
            Corner2 = new(outerEdge.Right - half, outerEdge.Top - half),
            Color = ColorOf(annotation) ?? "#E53935", LineWidth = lineWidth > 0 ? lineWidth : 1, HasOutline = lineWidth > 0,
        };
        ReadFill(annotation, rectangle);
        return rectangle;
    }

    /// <summary>A solid fill from /IC, or a patterned one from <see cref="AnnotationWriter.FillKey"/>.</summary>
    static void ReadFill(PdfDictionary annotation, IFillable shape)
    {
        if (annotation.Elements.GetObject(AnnotationWriter.FillKey) is PdfDictionary fill)
        {
            shape.FillColor = HexOf(fill.Elements.GetObject("/Color"));
            shape.FillPattern = Enum.Parse<HatchPattern>(fill.Elements.GetName("/Pattern").TrimStart('/'));
        }
        else shape.FillColor = HexOf(annotation.Elements.GetObject("/IC"));

        static string? HexOf(PdfItem? color) => color is PdfArray { Elements.Count: 3 } rgb
            ? ToHex(rgb.Elements.GetReal(0), rgb.Elements.GetReal(1), rgb.Elements.GetReal(2)) : null;
    }

    static List<PdfPoint> Points(PdfArray numbers)
    {
        var points = new List<PdfPoint>();
        for (int i = 0; i + 1 < numbers.Elements.Count; i += 2)
            points.Add(new PdfPoint(numbers.Elements.GetReal(i), numbers.Elements.GetReal(i + 1)));
        return points;
    }

    /// <summary>A measurement whose scale PurpleStreamPDF understands; any other stays untouched in the file.</summary>
    static Markup? ReadMeasure(PdfDictionary annotation, MeasureKind kind, string pointsKey)
    {
        if (annotation.Elements.GetObject(pointsKey) is not PdfArray numbers) return null;
        if (MeasureDictionary.MmPerPoint(annotation.Elements["/Measure"]) is not { } mmPerPoint) return null;
        var points = Points(numbers);
        int minimum = kind == MeasureKind.Area ? 3 : 2;
        if (points.Count < minimum || (kind == MeasureKind.Distance && points.Count != 2)) return null;
        return new MeasureMarkup
        {
            Kind = kind, Points = points, MmPerPoint = mmPerPoint,
            Color = ColorOf(annotation) ?? "#E53935", LineWidth = LineWidthOf(annotation),
        };
    }

    /// <summary>A /LE name as one of PurpleStreamPDF's endings: filled shapes count as a dot.</summary>
    static LineEnding EndingOf(string name) => name switch
    {
        "/ClosedArrow" or "/OpenArrow" => LineEnding.Arrow, "/Circle" or "/Square" or "/Diamond" => LineEnding.Dot, "/Slash" => LineEnding.Tick, _ => LineEnding.None,
    };

    static bool IsSeeThrough(PdfDictionary annotation) =>
        annotation.Elements.ContainsKey("/CA") && annotation.Elements.GetReal("/CA") < 1;

    /// <summary>The object a reference points to, or the item itself if it is no reference.</summary>
    internal static PdfItem? Resolve(PdfItem? item) => item is PdfSharp.Pdf.Advanced.PdfReference reference ? reference.Value : item;

    static bool IsCloud(PdfDictionary annotation) =>
        annotation.Elements.GetName("/IT") == "/PolygonCloud"
        || (annotation.Elements.GetObject("/BE") as PdfDictionary)?.Elements.GetName("/S") == "/C";

    /// <summary>/Rect shrunk by /RD: the box around the text, without the callout line.</summary>
    static PdfRect TextBox(PdfDictionary annotation)
    {
        var rect = annotation.Elements.GetRectangle("/Rect");
        double left = Math.Min(rect.X1, rect.X2), right = Math.Max(rect.X1, rect.X2);
        double bottom = Math.Min(rect.Y1, rect.Y2), top = Math.Max(rect.Y1, rect.Y2);
        if (annotation.Elements.GetObject("/RD") is PdfArray { Elements.Count: 4 } differences)
        {
            left += differences.Elements.GetReal(0);
            top -= differences.Elements.GetReal(1);
            right -= differences.Elements.GetReal(2);
            bottom += differences.Elements.GetReal(3);
        }
        return new PdfRect(left, bottom, right, top);
    }

    static double LineWidthOf(PdfDictionary annotation) =>
        annotation.Elements.GetObject("/BS") is PdfDictionary borderStyle && borderStyle.Elements.ContainsKey("/W")
            ? borderStyle.Elements.GetReal("/W")
            : 1;

    static string? ColorOf(PdfDictionary annotation) =>
        annotation.Elements.GetObject("/C") is PdfArray { Elements.Count: 3 } rgb
            ? ToHex(rgb.Elements.GetReal(0), rgb.Elements.GetReal(1), rgb.Elements.GetReal(2))
            : null;

    static double? FontSizeIn(string defaultAppearance) =>
        FontSizePattern().Match(defaultAppearance) is { Success: true } match ? ParseNumber(match.Groups[1].Value) : null;

    static string? FillColorIn(string defaultAppearance) =>
        FillColorPattern().Match(defaultAppearance) is { Success: true } match
            ? ToHex(ParseNumber(match.Groups[1].Value), ParseNumber(match.Groups[2].Value), ParseNumber(match.Groups[3].Value))
            : null;

    static double ParseNumber(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    static string ToHex(double red, double green, double blue) =>
        $"#{ToByte(red):X2}{ToByte(green):X2}{ToByte(blue):X2}";

    static int ToByte(double component) => (int)Math.Round(Math.Clamp(component, 0, 1) * 255);

    [GeneratedRegex(@"([\d.]+)\s+Tf")]
    private static partial Regex FontSizePattern();

    [GeneratedRegex(@"([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+rg")]
    private static partial Regex FillColorPattern();
}
