# Phase 14 – Vector snapping and measuring (2–3 days)

## Plan

**Keep showing the page as a bitmap. Read the vectors alongside it.** Redrawing a CAD sheet as WPF vector shapes would be slow (100k+ segments), would look different from PDFium's rendering, and would bring back the blur problems fixed in phase 1. Snapping only needs the *geometry*, not a vector *display*. So read the line work from PDFium into a hidden snap index, and keep the bitmap for display.

The PDFium bundled with Docnet exports every function needed (checked: `FPDFPage_CountObjects`, `FPDFPageObj_GetMatrix`, `FPDFPath_CountSegments`, `FPDFPathSegment_GetPoint`, `FPDFFormObj_CountObjects`). Docnet doesn't wrap them, so call them with `[LibraryImport("pdfium")]` from the same native DLL, inside `PdfRenderer`'s lock.

1. **Extract (Core, `VectorExtractor`):** for one page, walk the page objects. Recurse into form XObjects, multiplying matrices on the way down. For each path object, read its segments (moveto / lineto / bezierto / close) and turn them into line segments in PDF space; flatten Béziers into ~8 pieces. Also keep the end points and centres of arcs/circles where cheap. Output: `List<(PdfPoint A, PdfPoint B)>`. Cache per page; build it off the UI thread the first time a snap tool is used.
2. **Snap index (Core, `SnapIndex`):** a uniform grid of cells holding segments. Query `Snap(point, radius)` returns the best candidate, by priority: **endpoint** > **intersection** (of segments in nearby cells) > **midpoint** > **nearest point on a line**. Radius is ~10 screen pixels, converted to points at the current zoom. Unit-test it with hand-made segments.
3. **Snap feedback (UI):** while a snap-aware tool is active, show a marker at the snap point. The marker's shape shows the kind: square = endpoint, X = intersection, triangle = midpoint, hourglass = nearest. Hold **Shift** to snap to horizontal/vertical from the previous point, and **Alt** to turn snapping off.
4. **Calibrate:** "Set scale…" offers presets (1:1, 1:5, 1:10, 1:20, 1:50, 1:100, 1:200, 1:500, 1:1250) or "Calibrate": click two points with a known length, type the real distance in mm. Store it per page. Save it in the PDF as a `/Measure` (viewport) dictionary, so Bluebeam picks up the same scale, and read it back if the file already has one (Bluebeam writes them).
5. **Measure tools:**
   - **Distance:** click two snapped points.
   - **Polylength:** a chain of points, double-click to end.
   - **Area:** a polygon; shows area and perimeter.
   - Results show live in the status bar. Placing one creates a markup saved as a standard annotation: `/Line` with `/IT /LineDimension` (distance), `/PolyLine` with `/IT /PolyLineDimension`, and `/Polygon` with `/IT /PolygonDimension`, each with `/Measure` and an `/AP`. These are standard PDF types, so other viewers that support them keep them as measurements.
6. **Snap in the other tools:** cloud vertices and callout arrow tips use the same snap, so markups land exactly on the drawing.

**Risks:**
- Some CAD PDFs are huge (500k segments). Extract lazily, cap the work, and cache it.
- Scanned sheets have no vectors: show "No line work to snap to on this page".
- Hatching makes a lot of noise: start without filtering, and add a "skip filled paths" option only if needed.

**Done when:** on a Revit A1 sheet, the snap marker lands exactly on line ends and wall intersections at any zoom. A 1:50 calibrated distance between two gridlines matches the drawn dimension to within 1 mm. Measurements saved here open in Bluebeam as measurements with the same values. Extracting the heaviest sample takes under 1 s.

## Prompt

```
Add vector snapping and measuring to PocketPDF (WPF, .NET 10). Read PLAN.md and src/PocketPDF.Core/PdfRenderer.cs first. Keep showing the page as the PDFium bitmap. Read the vector geometry into a snap index alongside it; don't redraw the page as WPF shapes.

Step 1, spike (check before building on it): Docnet ships pdfium.dll, which exports FPDF_LoadMemDocument, FPDF_LoadPage, FPDFPage_CountObjects, FPDFPage_GetObject, FPDFPageObj_GetType, FPDFPageObj_GetMatrix, FPDFPath_CountSegments, FPDFPath_GetPathSegment, FPDFPathSegment_GetPoint, FPDFPathSegment_GetType, FPDFPathSegment_GetClose, FPDFFormObj_CountObjects, FPDFFormObj_GetObject. Declare them with [LibraryImport("pdfium")] in a Core class `PdfiumNative`, and call them only inside PdfRenderer's static PdfiumLock. Write a test that generates a PDF with known lines (TestPdf.cs helpers), including a form XObject and a page with /Rotate 90, and checks the extracted segments in PDF space.

Step 2, Core:
- `VectorExtractor.Extract(pdfBytes, pageIndex)` returns a list of line segments in PDF space. Apply the object matrix (multiplied through nested form XObjects). Flatten Béziers into 8 segments. Cap at ~1M segments. PdfRenderer.GetVectors(pageIndex) caches the result.
- `SnapIndex(segments)` uses a uniform grid. Snap(PdfPoint, radius) returns (point, kind) with priority Endpoint > Intersection > Midpoint > Nearest. Unit tests with hand-made segments, including crossing lines and near-misses.
- `DrawingScale`: a ratio (real mm per PDF pt), preset ratios, and calibration from two points + a real length. Read and write the PDF /Measure dictionary (/Type /Measure /Subtype /RL with /R, /X, /D, /A) in MarkupFile, keeping any existing one. Unit-test the round trip.
- New markups: DistanceMarkup (2 points), PolylengthMarkup (points), AreaMarkup (polygon). Each has a measured value from the scale. Write them as /Line /IT /LineDimension, /PolyLine /IT /PolyLineDimension and /Polygon /IT /PolygonDimension, each with /Measure, /Contents (the value text) and an /AP stream (follow AnnotationWriter and AppearanceBuilder). Read them back in AnnotationReader. Round-trip tests in MarkupFileTests.

Step 3, UI (src/PocketPDF):
- While a snapping tool is active (Distance, Polylength, Area, Cloud, Callout arrow tip), find the snap target on mouse move. Use a radius of ~10 screen px converted to points at the current zoom. Draw a marker on MarkupCanvas: square for Endpoint, X for Intersection, triangle for Midpoint, hourglass for Nearest. Shift = lock to horizontal/vertical from the previous point. Alt = no snap. Build vectors off the UI thread on first use, with a wait cursor. If there are none, show "No line work to snap to on this page".
- "Scale" picker with presets + "Calibrate…" (click two points, type a length in mm). Per page, shown in the status bar.
- Live readout of distance/length/area in the status bar while drawing. The placed measurement shows its value as a label.
- Measurements go through MarkupsChanged, so they're undoable and editable like other markups.

Performance target: extraction under 1 s for the heaviest file in data/, snap query under 1 ms. Run `dotnet build` and `dotnet test`. Check by hand on a Revit sheet: calibrate at the drawing's scale, measure between two gridlines, compare with the drawn dimension. Save, open in Bluebeam (if available), and confirm the measurements and scale are recognised. Add a "Phase 14 … **Done.**" section to PLAN.md with the measured timings.
```
