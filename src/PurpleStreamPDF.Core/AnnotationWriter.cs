using PdfSharp.Pdf;

namespace PurpleStreamPDF.Core;

/// <summary>Turns a markup into a standard PDF annotation, with an appearance stream so every viewer shows it.</summary>
static class AnnotationWriter
{
    // The private keys below keep the app's first name, /PocketPDF…: PDFs already marked up carry them, and still read back.

    /// <summary>Marks a callout drawn without a box round its text (a tag).</summary>
    internal const string NoBorderKey = "/PocketPDFNoBorder";

    /// <summary>A cloud's tag: where its text goes (/At), its font size (/Size) and how its leader ends (/End, a line ending name). The text is /Contents.</summary>
    internal const string CloudTagKey = "/PocketPDFTag";

    /// <summary>An area that isn't a clouded polygon: its /Shape, its outline /Style and its defining /Points.</summary>
    internal const string AreaKey = "/PocketPDFArea";

    /// <summary>A CAD tool entity: its /Kind (/Line, /Circle or /Hatch) and the values that define it.</summary>
    internal const string CadKey = "/PocketPDFCad";

    /// <summary>The font family of a markup's text, when not the default Helvetica.</summary>
    internal const string FontKey = "/PocketPDFFont";

    /// <summary>Who last changed a markup; the standard /M only says when.</summary>
    internal const string ModifiedByKey = "/PocketPDFModifiedBy";

    /// <summary>A patterned fill: its /Color and /Pattern. A solid fill is the standard /IC instead.</summary>
    internal const string FillKey = "/PocketPDFFill";

    /// <summary>A placed sheet: its /Source (a one-page PDF stream), /Size, /Corners, /Clip, /Opacity and whether it is /Tinted.</summary>
    internal const string SheetKey = "/PocketPDFSheet";

    /// <summary>A placed sheet's resolution in the file, and the most pixels it is drawn with.</summary>
    const double SheetDpi = 150;
    const double MaxSheetPixels = 30_000_000; // shortcut: an A0 at 150 dpi; raise it (or embed the page as vectors) if prints of big sheets look soft

    /// <summary>The optional content group (layer) every CAD entity is on.</summary>
    internal const string CadLayerName = "CAD";

    public static PdfDictionary Create(PdfDocument document, Markup markup, PageGeometry page)
    {
        var annotation = new PdfDictionary(document);
        annotation.Elements["/NM"] = new PdfString(Guid.NewGuid().ToString());
        // Stamped on the markup too, so saving again keeps the same author and time.
        markup.Author ??= Environment.UserName;
        markup.Created ??= DateTime.Now;
        annotation.Elements["/T"] = new PdfString(markup.Author, PdfStringEncoding.Unicode); // author, shown in other viewers' markup lists
        annotation.Elements.SetDateTime("/CreationDate", markup.Created.Value);
        WriteModified(annotation, markup);
        Write(document, annotation, markup, page);
        document.Internals.AddObject(annotation);
        return annotation;
    }

    /// <summary>
    /// Rewrites an existing annotation after the markup was changed. Its ID, author, replies, pop-up and any keys
    /// PurpleStreamPDF doesn't know (another viewer's subject, columns, fill...) stay as they were.
    /// </summary>
    public static void Update(PdfDocument document, PdfDictionary annotation, Markup markup, PageGeometry page)
    {
        annotation.Elements.Remove("/RC"); // rich text would still show the old text when another viewer redraws it
        string fingerprint = markup.Fingerprint();
        if (fingerprint != markup.StampedFingerprint) // changed since last stamped: by this user, now (the time of the save)
        {
            markup.ModifiedBy = Environment.UserName;
            markup.Modified = DateTime.Now;
            markup.StampedFingerprint = fingerprint;
        }
        WriteModified(annotation, markup);
        Write(document, annotation, markup, page);
    }

    /// <summary>When it was last changed (/M), or drawn if never changed; and who changed it.</summary>
    static void WriteModified(PdfDictionary annotation, Markup markup)
    {
        if ((markup.Modified ?? markup.Created) is { } time) annotation.Elements.SetDateTime("/M", time);
        if (markup.ModifiedBy != null) annotation.Elements[ModifiedByKey] = new PdfString(markup.ModifiedBy, PdfStringEncoding.Unicode);
    }

    static void Write(PdfDocument document, PdfDictionary annotation, Markup markup, PageGeometry page)
    {
        var appearance = new AppearanceBuilder(page);
        ViewRect drawnArea;
        PdfDictionary? sheetImage = null;

        switch (markup)
        {
            case TextMarkup text:
            {
                var box = LabelGeometry.Box(page.ToView(text.TextPosition), text.Text, text.FontSize, hasBorder: false, text.LineWidth, text.Font);
                appearance.Text(box.TopLeft, text.Text, text.FontSize, text.Color, text.Font);
                drawnArea = box;
                SetFreeTextEntries(annotation, text.Text, text.FontSize, text.Color, text.Font, page);
                break;
            }
            case CalloutMarkup callout:
            {
                var box = LabelGeometry.Box(page.ToView(callout.TextPosition), callout.Text, callout.FontSize, callout.HasBorder, callout.LineWidth, callout.Font);
                var arrowTip = page.ToView(callout.ArrowTip);
                var leaderStart = LabelGeometry.LeaderStart(box, arrowTip);
                var arrowHead = appearance.Leader(leaderStart, arrowTip, callout.Ending, callout.Color, callout.LineWidth);
                double inset = LabelGeometry.TextInset(callout.HasBorder, callout.LineWidth);
                if (callout.HasBorder) appearance.BorderInside(box, callout.LineWidth);
                appearance.Text(new ViewPoint(box.Left + inset, box.Top + inset), callout.Text, callout.FontSize, callout.Color, callout.Font);

                drawnArea = ViewRect.Around([.. box.Corners(), arrowTip, .. arrowHead]).Inflate(callout.LineWidth);
                SetFreeTextEntries(annotation, callout.Text, callout.FontSize, callout.Color, callout.Font, page);
                annotation.Elements["/IT"] = new PdfName("/FreeTextCallout");
                annotation.Elements["/CL"] = Numbers(document, [callout.ArrowTip, page.ToPdf(leaderStart)]);
                annotation.Elements["/LE"] = new PdfName(EndingName(callout.Ending));
                annotation.Elements["/C"] = ColorArray(document, callout.Color); // line and border colour
                annotation.Elements["/RD"] = RectDifferences(document, page.ToPdf(drawnArea), page.ToPdf(box));
                // /BS sets both the leader and the border width, so a tag (no border) is marked with a key of our own.
                if (callout.HasBorder) annotation.Elements.Remove(NoBorderKey);
                else annotation.Elements[NoBorderKey] = new PdfBoolean(true);
                break;
            }
            case CloudMarkup cloud:
            {
                var vertices = cloud.Outline(page);
                double arcLength = CloudGeometry.ArcLength(page);
                WriteFill(document, annotation, appearance, cloud, page);
                appearance.Stroke(cloud.Color, cloud.LineWidth);
                if (cloud.Style == AreaLineStyle.Cloud) appearance.Cloud(vertices[0], CloudGeometry.Arcs(vertices, arcLength));
                else if (cloud.Style != AreaLineStyle.None) appearance.ClosedOutline(vertices, cloud.LineWidth, cloud.Style == AreaLineStyle.Dashed ? CloudMarkup.Dashes : null);

                drawnArea = ViewRect.Around(vertices).Inflate(CloudGeometry.BumpMargin(arcLength) + cloud.LineWidth);
                annotation.Elements["/Subtype"] = new PdfName("/Polygon");
                // Other viewers redraw the polygon from /Vertices, so they get the outline; the box or circle it came from is kept in AreaKey.
                annotation.Elements["/Vertices"] = Numbers(document, vertices.Select(page.ToPdf));
                if (cloud.Style == AreaLineStyle.Cloud)
                {
                    annotation.Elements["/IT"] = new PdfName("/PolygonCloud");
                    annotation.Elements["/BE"] = new PdfDictionary(document) { Elements = { ["/S"] = new PdfName("/C"), ["/I"] = new PdfInteger(1) } };
                }
                else
                {
                    annotation.Elements.Remove("/IT");
                    annotation.Elements.Remove("/BE");
                }
                if (cloud.Shape == AreaShape.Polygon && cloud.Style == AreaLineStyle.Cloud) annotation.Elements.Remove(AreaKey);
                else annotation.Elements[AreaKey] = new PdfDictionary(document)
                {
                    Elements =
                    {
                        ["/Shape"] = new PdfName("/" + cloud.Shape), ["/Style"] = new PdfName("/" + cloud.Style),
                        ["/Points"] = Numbers(document, cloud.Vertices),
                    },
                };
                annotation.Elements["/C"] = ColorArray(document, cloud.Color);
                if (annotation.Elements.ContainsKey(CloudTagKey))
                {
                    annotation.Elements.Remove(CloudTagKey);
                    annotation.Elements.Remove("/Contents");
                }
                if (cloud.TextPosition is { } textPosition)
                {
                    // shortcut: the tag lives in the cloud's appearance; a viewer that redraws the polygon from /Vertices drops it. Save it as a grouped callout if that matters.
                    var box = LabelGeometry.Box(page.ToView(textPosition), cloud.Text, cloud.FontSize, hasBorder: false, cloud.LineWidth, cloud.Font);
                    var (from, to) = LabelGeometry.CloudLeader(vertices, box);
                    var arrowHead = appearance.Leader(from, to, cloud.Ending, cloud.Color, cloud.LineWidth);
                    appearance.Text(box.TopLeft, cloud.Text, cloud.FontSize, cloud.Color, cloud.Font);
                    drawnArea = ViewRect.Around([.. drawnArea.Corners(), .. box.Corners(), .. arrowHead]).Inflate(cloud.LineWidth);
                    annotation.Elements["/Contents"] = new PdfString(cloud.Text.Replace("\n", "\r"), PdfStringEncoding.Unicode);
                    annotation.Elements[CloudTagKey] = new PdfDictionary(document)
                    {
                        Elements =
                        {
                            ["/At"] = Numbers(document, [textPosition]),
                            ["/Size"] = new PdfReal(cloud.FontSize),
                            ["/End"] = new PdfName(EndingName(cloud.Ending)),
                        },
                    };
                }
                break;
            }
            case PenMarkup pen:
            {
                var points = pen.Points.Select(page.ToView).ToList();
                appearance.Stroke(pen.Color, pen.LineWidth).Polyline(points);

                drawnArea = ViewRect.Around(points).Inflate(pen.LineWidth / 2 + 1);
                annotation.Elements["/Subtype"] = new PdfName("/Ink");
                annotation.Elements["/InkList"] = new PdfArray(document, Numbers(document, pen.Points));
                annotation.Elements["/C"] = ColorArray(document, pen.Color);
                break;
            }
            case LineMarkup line:
            {
                var (start, end) = (page.ToView(line.Start), page.ToView(line.End));
                var arrowHead = appearance.Leader(end, start, line.Ending, line.Color, line.LineWidth);

                drawnArea = ViewRect.Around([start, end, .. arrowHead]).Inflate(line.LineWidth);
                annotation.Elements["/Subtype"] = new PdfName("/Line");
                annotation.Elements["/L"] = Numbers(document, [line.Start, line.End]);
                annotation.Elements["/LE"] = new PdfArray(document, new PdfName(EndingName(line.Ending)), new PdfName("/None"));
                annotation.Elements["/C"] = ColorArray(document, line.Color);
                break;
            }
            case RectangleMarkup rectangle:
            {
                var middleOfStroke = ViewRect.Around([page.ToView(rectangle.Corner1), page.ToView(rectangle.Corner2)]);
                double strokeWidth = rectangle.HasOutline ? rectangle.LineWidth : 0;
                var outerEdge = middleOfStroke.Inflate(strokeWidth / 2);
                WriteFill(document, annotation, appearance, rectangle, page);
                if (rectangle.HasOutline) appearance.Stroke(rectangle.Color, rectangle.LineWidth).BorderInside(outerEdge, rectangle.LineWidth);

                drawnArea = outerEdge.Inflate(strokeWidth / 2);
                annotation.Elements["/Subtype"] = new PdfName("/Square");
                annotation.Elements["/C"] = ColorArray(document, rectangle.Color);
                annotation.Elements["/RD"] = RectDifferences(document, page.ToPdf(drawnArea), page.ToPdf(outerEdge));
                break;
            }
            case HighlightMarkup highlight:
            {
                var points = highlight.Points.Select(page.ToView).ToList();
                appearance.HighlightStroke(points, highlight.Color, highlight.Thickness);

                drawnArea = ViewRect.Around(points).Inflate(highlight.Thickness / 2 + 1);
                annotation.Elements["/Subtype"] = new PdfName("/Ink");
                annotation.Elements["/InkList"] = new PdfArray(document, Numbers(document, highlight.Points));
                annotation.Elements["/C"] = ColorArray(document, highlight.Color);
                annotation.Elements["/CA"] = new PdfReal(HighlightMarkup.Opacity);
                break;
            }
            case MeasureMarkup measure:
            {
                var points = measure.Points.Select(page.ToView).ToList();
                appearance.Stroke(measure.Color, measure.LineWidth);
                if (measure.Kind == MeasureKind.Distance) // arrows both ends: stop the line inside each head
                    appearance.Line(LabelGeometry.ArrowLineEnd(points[1], points[0], measure.LineWidth), LabelGeometry.ArrowLineEnd(points[0], points[1], measure.LineWidth));
                else
                    for (int i = 0; i + 1 < points.Count; i++) appearance.Line(points[i], points[i + 1]);
                if (measure.Kind == MeasureKind.Area) appearance.Line(points[^1], points[0]);
                var arrowHeads = new List<ViewPoint>();
                if (measure.Kind == MeasureKind.Distance)
                {
                    foreach (var (from, to) in new[] { (points[1], points[0]), (points[0], points[1]) })
                    {
                        var head = LabelGeometry.ArrowHead(from, to, measure.LineWidth);
                        appearance.Fill(measure.Color).FilledPolygon(head);
                        arrowHeads.AddRange(head);
                    }
                }
                var label = measure.LabelBox(page);
                appearance.Fill("#FFFFFF").FilledPolygon(label.Corners())
                    .Text(new ViewPoint(label.Left + MeasureMarkup.LabelPadding, label.Top + 1), measure.ValueText, MeasureMarkup.LabelFontSize, measure.Color, measure.Font);

                drawnArea = ViewRect.Around([.. points, .. arrowHeads, .. label.Corners()]).Inflate(measure.LineWidth);
                annotation.Elements["/Subtype"] = new PdfName(measure.Kind switch
                {
                    MeasureKind.Distance => "/Line", MeasureKind.Length => "/PolyLine", _ => "/Polygon",
                });
                annotation.Elements["/IT"] = new PdfName(measure.Kind switch
                {
                    MeasureKind.Distance => "/LineDimension", MeasureKind.Length => "/PolyLineDimension", _ => "/PolygonDimension",
                });
                if (measure.Kind == MeasureKind.Distance)
                {
                    annotation.Elements["/L"] = Numbers(document, measure.Points);
                    annotation.Elements["/LE"] = new PdfArray(document, new PdfName("/ClosedArrow"), new PdfName("/ClosedArrow"));
                    annotation.Elements["/CAP"] = new PdfBoolean(true); // show the value on the line
                }
                else annotation.Elements["/Vertices"] = Numbers(document, measure.Points);
                annotation.Elements["/Measure"] = MeasureDictionary.Create(document, measure.MmPerPoint);
                annotation.Elements["/Contents"] = new PdfString(measure.ValueText, PdfStringEncoding.Unicode);
                annotation.Elements["/C"] = ColorArray(document, measure.Color);
                break;
            }
            case CadLineMarkup line:
            {
                var points = line.Points.Select(page.ToView).ToList();
                WriteFill(document, annotation, appearance, line, page);
                appearance.Stroke(line.Color, line.LineWidth);
                if (line.Closed) appearance.ClosedOutline(points, line.LineWidth, null);
                else appearance.Polyline(points);

                drawnArea = ViewRect.Around(points).Inflate(line.LineWidth);
                annotation.Elements["/Subtype"] = new PdfName(line.Closed ? "/Polygon" : "/PolyLine");
                annotation.Elements["/Vertices"] = Numbers(document, line.Points);
                annotation.Elements["/C"] = ColorArray(document, line.Color);
                annotation.Elements[CadKey] = new PdfDictionary(document)
                {
                    Elements = { ["/Kind"] = new PdfName("/Line"), ["/Points"] = Numbers(document, line.Points), ["/Closed"] = new PdfBoolean(line.Closed) },
                };
                break;
            }
            case CadCircleMarkup circle:
            {
                var outline = CadGeometry.PathOf(circle)!.Value.Points.Select(page.ToView).ToList();
                WriteFill(document, annotation, appearance, circle, page);
                appearance.Stroke(circle.Color, circle.LineWidth).ClosedOutline(outline, circle.LineWidth, null);

                drawnArea = ViewRect.Around(outline).Inflate(circle.LineWidth);
                annotation.Elements["/Subtype"] = new PdfName("/Circle");
                annotation.Elements["/C"] = ColorArray(document, circle.Color);
                annotation.Elements["/RD"] = RectDifferences(document, page.ToPdf(drawnArea), page.ToPdf(ViewRect.Around(outline).Inflate(circle.LineWidth / 2)));
                annotation.Elements[CadKey] = new PdfDictionary(document)
                {
                    Elements = { ["/Kind"] = new PdfName("/Circle"), ["/Center"] = Numbers(document, [circle.Center]), ["/Radius"] = new PdfReal(circle.Radius) },
                };
                break;
            }
            case CadDimensionMarkup dimension:
            {
                var shape = Dimensioning.Shape(dimension, page);
                appearance.Stroke(dimension.Color, dimension.LineWidth);
                foreach (var (from, to) in shape.Lines) appearance.Line(from, to);
                ViewPoint[] ends =
                [
                    shape.EndA, shape.EndB,
                    .. appearance.Leader(shape.Middle, shape.EndA, dimension.Ending, dimension.Color, dimension.LineWidth),
                    .. appearance.Leader(shape.Middle, shape.EndB, dimension.Ending, dimension.Color, dimension.LineWidth),
                ];
                appearance.Text(shape.TextTopLeft, dimension.ValueText, CadDimensionMarkup.TextSize, dimension.Color, dimension.Font, shape.TextAngle);

                drawnArea = ViewRect.Around([.. shape.Lines.SelectMany(line => new[] { line.A, line.B }), .. ends, .. shape.TextCorners]).Inflate(dimension.LineWidth);
                annotation.Elements["/Subtype"] = new PdfName("/Line");
                annotation.Elements["/L"] = Numbers(document, [dimension.Start, dimension.End]);
                annotation.Elements["/LE"] = new PdfArray(document, new PdfName(EndingName(dimension.Ending)), new PdfName(EndingName(dimension.Ending)));
                annotation.Elements["/Contents"] = new PdfString(dimension.ValueText, PdfStringEncoding.Unicode);
                annotation.Elements["/C"] = ColorArray(document, dimension.Color);
                annotation.Elements[CadKey] = new PdfDictionary(document)
                {
                    Elements =
                    {
                        ["/Kind"] = new PdfName("/Dimension"), ["/Points"] = Numbers(document, [dimension.Start, dimension.End]),
                        ["/Offset"] = new PdfReal(dimension.Offset), ["/Scale"] = new PdfReal(dimension.MmPerPoint),
                    },
                };
                break;
            }
            case CadHatchMarkup hatch:
            {
                var boundary = hatch.Boundary.Select(page.ToView).ToList();
                drawnArea = ViewRect.Around(boundary).Inflate(hatch.LineWidth);
                annotation.Elements["/C"] = ColorArray(document, hatch.Color);
                if (hatch.Pattern == HatchPattern.Solid)
                {
                    appearance.Fill(hatch.Color).FilledPolygon(boundary);
                    annotation.Elements["/Subtype"] = new PdfName("/Polygon"); // viewers that redraw it from /Vertices still fill it (/IC)
                    annotation.Elements["/Vertices"] = Numbers(document, hatch.Boundary);
                    annotation.Elements["/IC"] = ColorArray(document, hatch.Color);
                    annotation.Elements.Remove("/InkList");
                }
                else
                {
                    var lines = CadGeometry.HatchStrokes(hatch);
                    appearance.Stroke(hatch.Color, hatch.LineWidth);
                    foreach (var (from, to) in lines) appearance.Line(page.ToView(from), page.ToView(to));
                    annotation.Elements["/Subtype"] = new PdfName("/Ink"); // viewers that redraw it from /InkList still get the hatch lines
                    annotation.Elements["/InkList"] = new PdfArray(document, lines.Select(line => (PdfItem)Numbers(document, [line.A, line.B])).ToArray());
                    annotation.Elements.Remove("/Vertices");
                    annotation.Elements.Remove("/IC");
                }
                annotation.Elements[CadKey] = new PdfDictionary(document)
                {
                    Elements =
                    {
                        ["/Kind"] = new PdfName("/Hatch"), ["/Points"] = Numbers(document, hatch.Boundary),
                        ["/Spacing"] = new PdfReal(hatch.Spacing), ["/Angle"] = new PdfReal(hatch.AngleDegrees), ["/Pattern"] = new PdfName("/" + hatch.Pattern),
                    },
                };
                break;
            }
            case PlacedSheetMarkup sheet:
            {
                drawnArea = sheet.Visible(page);
                sheetImage = SheetImage(document, sheet, page);
                appearance.Image(drawnArea);
                annotation.Elements["/Subtype"] = new PdfName("/Stamp");
                annotation.Elements["/C"] = ColorArray(document, sheet.Color);
                var source = (annotation.Elements.GetObject(SheetKey) as PdfDictionary)?.Elements.GetObject("/Source") as PdfDictionary;
                if (source == null) // the source never changes: an edited sheet keeps the stream it has
                {
                    source = new PdfDictionary(document);
                    source.CreateStream(sheet.Source);
                    document.Internals.AddObject(source);
                }
                annotation.Elements[SheetKey] = new PdfDictionary(document)
                {
                    Elements =
                    {
                        ["/Source"] = source.Reference,
                        ["/Size"] = new PdfArray(document, new PdfReal(sheet.SourceWidth), new PdfReal(sheet.SourceHeight)),
                        ["/Corners"] = Numbers(document, [sheet.TopLeft, sheet.BottomRight]),
                        ["/Clip"] = new PdfArray(document, new PdfReal(sheet.ClipLeft), new PdfReal(sheet.ClipTop), new PdfReal(sheet.ClipRight), new PdfReal(sheet.ClipBottom)),
                        ["/Opacity"] = new PdfReal(sheet.Opacity),
                        ["/Tinted"] = new PdfBoolean(sheet.Tinted),
                    },
                };
                break;
            }
            default:
                throw new ArgumentException($"Unknown markup type {markup.GetType().Name}", nameof(markup));
        }
        if (markup is CadMarkup) annotation.Elements["/OC"] = CadLayer(document).Reference;
        if (markup.Font != null) annotation.Elements[FontKey] = new PdfString(markup.Font, PdfStringEncoding.Unicode);
        else annotation.Elements.Remove(FontKey);
        // Only when it changed, so another viewer's subject stays as written; null leaves it too.
        if (markup.Subject != null && annotation.Elements.GetString("/Subj") != markup.Subject)
            annotation.Elements["/Subj"] = new PdfString(markup.Subject, PdfStringEncoding.Unicode);

        var rect = page.ToPdf(drawnArea);
        annotation.Elements["/Type"] = new PdfName("/Annot");
        annotation.Elements["/Rect"] = Numbers(document, [new PdfPoint(rect.Left, rect.Bottom), new PdfPoint(rect.Right, rect.Top)]);
        if (!annotation.Elements.ContainsKey("/F")) annotation.Elements["/F"] = new PdfInteger(4); // print
        annotation.Elements["/M"] = new PdfString($"D:{DateTime.UtcNow:yyyyMMddHHmmss}Z");
        double borderWidth = markup switch { HighlightMarkup marker => marker.Thickness, RectangleMarkup { HasOutline: false } or PlacedSheetMarkup => 0, _ => markup.LineWidth };
        annotation.Elements["/BS"] = markup is CloudMarkup { Style: AreaLineStyle.Dashed }
            ? new PdfDictionary(document)
            {
                Elements =
                {
                    ["/W"] = new PdfReal(borderWidth), ["/S"] = new PdfName("/D"),
                    ["/D"] = new PdfArray(document, CloudMarkup.Dashes.Select(dash => (PdfItem)new PdfReal(dash * borderWidth)).ToArray()),
                },
            }
            : new PdfDictionary(document) { Elements = { ["/W"] = new PdfReal(borderWidth), ["/S"] = new PdfName("/S") } };
        annotation.Elements["/AP"] = new PdfDictionary(document) { Elements = { ["/N"] = CreateAppearanceStream(document, appearance, rect, sheetImage).Reference } };
    }

    /// <summary>Draws the shape's fill under its outline: solid as /IC, which other viewers fill too; hatch lines in <see cref="FillKey"/>.</summary>
    static void WriteFill(PdfDocument document, PdfDictionary annotation, AppearanceBuilder appearance, IFillable shape, PageGeometry page)
    {
        annotation.Elements.Remove("/IC");
        annotation.Elements.Remove(FillKey);
        if (shape.FillColor is not { } color || !shape.CanFill) return;
        var outline = shape.FillOutline(page);
        if (shape.FillPattern == HatchPattern.Solid)
        {
            appearance.Fill(color).FilledPolygon(outline.Select(page.ToView).ToList());
            annotation.Elements["/IC"] = ColorArray(document, color);
            return;
        }
        appearance.Stroke(color, IFillable.PatternLineWidth);
        foreach (var (from, to) in CadGeometry.HatchStrokes(outline, shape.FillPattern)) appearance.Line(page.ToView(from), page.ToView(to));
        annotation.Elements[FillKey] = new PdfDictionary(document)
        {
            Elements = { ["/Color"] = ColorArray(document, color), ["/Pattern"] = new PdfName("/" + shape.FillPattern) },
        };
    }

    static void SetFreeTextEntries(PdfDictionary annotation, string text, double fontSize, string color, string? font, PageGeometry page)
    {
        string size = AppearanceBuilder.Number(fontSize);
        annotation.Elements["/Subtype"] = new PdfName("/FreeText");
        annotation.Elements["/Contents"] = new PdfString(text.Replace("\n", "\r"), PdfStringEncoding.Unicode);
        annotation.Elements["/DA"] = new PdfString($"/{AppearanceBuilder.FontResourceName} {size} Tf {AppearanceBuilder.ColorComponents(color)} rg");
        // A viewer that redraws the text takes the font from here.
        annotation.Elements["/DS"] = new PdfString($"font: {(font == null ? "Helvetica" : $"'{font}'")},sans-serif {size}pt; text-align:left; color:{color}");
        // Viewers use this to keep the text upright on rotated pages when they redraw it.
        if (page.Rotation != 0) annotation.Elements["/Rotate"] = new PdfInteger(page.Rotation);
        else annotation.Elements.Remove("/Rotate"); // a page turned back upright
    }

    /// <summary>The document's CAD layer, added to its layers (on, and listed in the layer panel) the first time.</summary>
    static PdfDictionary CadLayer(PdfDocument document)
    {
        var catalog = document.Internals.Catalog;
        if (catalog.Elements.GetObject("/OCProperties") is not PdfDictionary properties)
            catalog.Elements["/OCProperties"] = properties = new PdfDictionary(document);
        if (properties.Elements.GetObject("/OCGs") is not PdfArray groups)
            properties.Elements["/OCGs"] = groups = new PdfArray(document);
        foreach (var item in groups.Elements)
            if (AnnotationReader.Resolve(item) is PdfDictionary group
                && group.Elements.GetString("/Name") == CadLayerName) return group;

        var layer = new PdfDictionary(document) { Elements = { ["/Type"] = new PdfName("/OCG"), ["/Name"] = new PdfString(CadLayerName) } };
        document.Internals.AddObject(layer);
        groups.Elements.Add(layer.Reference!);
        if (properties.Elements.GetObject("/D") is not PdfDictionary defaults)
            properties.Elements["/D"] = defaults = new PdfDictionary(document);
        foreach (string list in new[] { "/Order", "/ON" }) // /ON too, in case the file's layers start off
        {
            if (defaults.Elements.GetObject(list) is PdfArray array) array.Elements.Add(layer.Reference!);
            else defaults.Elements[list] = new PdfArray(document, layer.Reference!);
        }
        return layer;
    }

    /// <summary>The standard /LE name, so other viewers draw the same ending when they redraw the line.</summary>
    internal static string EndingName(LineEnding ending) => ending switch
    {
        LineEnding.Arrow => "/ClosedArrow", LineEnding.Dot => "/Circle", LineEnding.Tick => "/Slash", _ => "/None",
    };

    /// <summary>
    /// The visible part of a placed sheet as an image, see-through as it shows on screen. An image rather than the page's
    /// own vectors, as only an image can be tinted and have its white paper taken out the same way in every viewer.
    /// </summary>
    static PdfDictionary SheetImage(PdfDocument document, PlacedSheetMarkup sheet, PageGeometry page)
    {
        double scale = sheet.Placed(page).Width / sheet.SourceWidth * SheetDpi / 72; // pixels per point of the source
        double pixels = sheet.SourceWidth * sheet.SourceHeight * scale * scale;
        if (pixels > MaxSheetPixels) scale *= Math.Sqrt(MaxSheetPixels / pixels);
        var bitmap = PlacedSheet.Render(sheet.Source, scale, sheet.Tinted ? sheet.Color : null);
        int left = Math.Clamp((int)Math.Floor(sheet.ClipLeft * bitmap.Width), 0, bitmap.Width - 1);
        int right = Math.Clamp((int)Math.Ceiling(sheet.ClipRight * bitmap.Width), left + 1, bitmap.Width);
        int top = Math.Clamp((int)Math.Floor(sheet.ClipTop * bitmap.Height), 0, bitmap.Height - 1);
        int bottom = Math.Clamp((int)Math.Ceiling(sheet.ClipBottom * bitmap.Height), top + 1, bitmap.Height);
        int width = right - left, height = bottom - top;
        var colors = new byte[width * height * 3];
        var alphas = new byte[width * height];
        double opacity = Math.Clamp(sheet.Opacity, 0, 1);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int from = ((top + y) * bitmap.Width + left + x) * 4, to = y * width + x;
                colors[to * 3] = bitmap.BgraPixels[from + 2];
                colors[to * 3 + 1] = bitmap.BgraPixels[from + 1];
                colors[to * 3 + 2] = bitmap.BgraPixels[from];
                alphas[to] = (byte)Math.Round(bitmap.BgraPixels[from + 3] * opacity);
            }
        var mask = ImageXObject(document, width, height, "/DeviceGray", alphas);
        var image = ImageXObject(document, width, height, "/DeviceRGB", colors);
        image.Elements["/SMask"] = mask.Reference;
        return image;
    }

    static PdfDictionary ImageXObject(PdfDocument document, int width, int height, string colorSpace, byte[] samples)
    {
        var image = new PdfDictionary(document)
        {
            Elements =
            {
                ["/Type"] = new PdfName("/XObject"), ["/Subtype"] = new PdfName("/Image"),
                ["/Width"] = new PdfInteger(width), ["/Height"] = new PdfInteger(height),
                ["/ColorSpace"] = new PdfName(colorSpace), ["/BitsPerComponent"] = new PdfInteger(8),
                ["/Filter"] = new PdfName("/FlateDecode"),
            },
        };
        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal)) zlib.Write(samples);
        image.CreateStream(compressed.ToArray());
        document.Internals.AddObject(image);
        return image;
    }

    static PdfDictionary CreateAppearanceStream(PdfDocument document, AppearanceBuilder appearance, PdfRect rect, PdfDictionary? sheetImage = null)
    {
        var font = new PdfDictionary(document)
        {
            Elements =
            {
                ["/Type"] = new PdfName("/Font"), ["/Subtype"] = new PdfName("/Type1"),
                ["/BaseFont"] = new PdfName("/Helvetica"), ["/Encoding"] = new PdfName("/WinAnsiEncoding"),
            },
        };
        var highlightState = new PdfDictionary(document)
        {
            Elements =
            {
                ["/CA"] = new PdfReal(HighlightMarkup.Opacity),
                ["/ca"] = new PdfReal(HighlightMarkup.Opacity),
                ["/BM"] = new PdfName("/Multiply"), // darkens like a marker pen, so black lines stay black
            },
        };
        var form = new PdfDictionary(document)
        {
            Elements =
            {
                ["/Type"] = new PdfName("/XObject"),
                ["/Subtype"] = new PdfName("/Form"),
                // The form is drawn in page coordinates: with BBox equal to /Rect, viewers place it 1:1.
                ["/BBox"] = Numbers(document, [new PdfPoint(rect.Left, rect.Bottom), new PdfPoint(rect.Right, rect.Top)]),
                ["/Resources"] = new PdfDictionary(document)
                {
                    Elements =
                    {
                        ["/Font"] = new PdfDictionary(document) { Elements = { ["/" + AppearanceBuilder.FontResourceName] = font } },
                        ["/ExtGState"] = new PdfDictionary(document) { Elements = { ["/" + AppearanceBuilder.HighlightStateName] = highlightState } },
                    },
                },
            },
        };
        if (sheetImage != null)
            form.Elements.GetDictionary("/Resources")!.Elements["/XObject"] = new PdfDictionary(document) { Elements = { ["/" + AppearanceBuilder.SheetImageName] = sheetImage.Reference } };
        form.CreateStream(appearance.ToBytes());
        document.Internals.AddObject(form);
        return form;
    }

    /// <summary>/RD: how far the text box sits inside /Rect, as [left top right bottom].</summary>
    static PdfArray RectDifferences(PdfDocument document, PdfRect outer, PdfRect inner) => new(document,
        new PdfReal(inner.Left - outer.Left), new PdfReal(outer.Top - inner.Top),
        new PdfReal(outer.Right - inner.Right), new PdfReal(inner.Bottom - outer.Bottom));

    static PdfArray Numbers(PdfDocument document, IEnumerable<PdfPoint> points) =>
        new(document, points.SelectMany(point => new PdfItem[] { new PdfReal(point.X), new PdfReal(point.Y) }).ToArray());

    static PdfArray ColorArray(PdfDocument document, string hexColor) =>
        new(document, AppearanceBuilder.ColorComponents(hexColor).Split(' ').Select(c => (PdfItem)new PdfReal(double.Parse(c, System.Globalization.CultureInfo.InvariantCulture))).ToArray());
}
