using System.Runtime.InteropServices;

namespace PocketPDF.Core;

/// <summary>A straight piece of the drawing's line work, in PDF space.</summary>
public readonly record struct LineSegment(PdfPoint A, PdfPoint B);

/// <summary>
/// Every call into PDFium, made directly to the pdfium.dll Docnet ships; callers must hold the PDFium lock. Reads the
/// line work of a page: every path, including those inside form XObjects, as straight segments (curves are split into
/// short pieces). Also renders pages and reads their text. Docnet's document readers are not used: they hand PDFium a
/// pointer to a .NET object the garbage collector can move, and closing the document then crashes the app at random.
/// </summary>
public static class VectorExtractor
{
    /// <summary>Stops reading here, so a pathological page can't eat all the memory. A dense A0 sheet is around 200k.</summary>
    public const int MaxSegments = 1_000_000;

    const int BezierPieces = 8;
    const int TextObject = 1, PathObject = 2, FormObject = 5;
    const int LineTo = 0, BezierTo = 1, MoveTo = 2;

    /// <summary>The page's line work, and the centres of the circles in it (closed paths of curves only, round to within 2%).</summary>
    internal static (List<LineSegment> Segments, List<PdfPoint> CircleCenters) Extract(byte[] pdfBytes, int pageIndex) => WithPage(pdfBytes, pageIndex, page =>
    {
        var (segments, centers) = (new List<LineSegment>(), new List<PdfPoint>());
        int count = Native.FPDFPage_CountObjects(page);
        for (int i = 0; i < count && segments.Count < MaxSegments; i++)
            AddObject(Native.FPDFPage_GetObject(page, i), Matrix.Identity, segments, centers);
        return (segments, centers);
    });

    /// <summary>The centre of a closed outline if it is a circle: every point within 2% of the same distance from the middle.</summary>
    internal static PdfPoint? CircleCenter(IReadOnlyList<PdfPoint> outline)
    {
        if (outline.Count < 8 || Math.Abs(outline[0].X - outline[^1].X) + Math.Abs(outline[0].Y - outline[^1].Y) > 0.01) return null;
        var points = outline.Take(outline.Count - 1).ToList(); // the last one is the first again
        var center = new PdfPoint(points.Average(point => point.X), points.Average(point => point.Y));
        var radii = points.Select(point => Math.Sqrt(Math.Pow(point.X - center.X, 2) + Math.Pow(point.Y - center.Y, 2))).ToList();
        double radius = radii.Average();
        return radius > 0.5 && radii.All(r => Math.Abs(r - radius) <= 0.02 * radius) ? center : null;
    }

    /// <summary>
    /// Renders a page like Docnet does, but with every line drawn one pixel wide ("line weights off"). Only PDFium's
    /// in-memory copy of the page is changed. Annotations keep their own line widths.
    /// </summary>
    internal static (byte[] BgraPixels, int Width, int Height) RenderHairlines(byte[] pdfBytes, int pageIndex, double scale, int flags) =>
        WithPage(pdfBytes, pageIndex, page =>
        {
            // Width 0 is PDF's "thinnest line the device can draw": one pixel at any zoom.
            EachObject(page, (pageObject, type) => { if (type == PathObject) Native.FPDFPageObj_SetStrokeWidth(pageObject, 0); });
            return RenderPage(page, scale, flags);
        });

    /// <summary>The page drawn as is, then again with all its text see-through, at one scale and without annotations.</summary>
    internal static (PageBitmap WithText, PageBitmap WithoutText) RenderWithAndWithoutText(byte[] pdfBytes, int pageIndex, double scale) =>
        WithPage(pdfBytes, pageIndex, page =>
        {
            var withText = RenderPage(page, scale, 0);
            EachObject(page, (pageObject, type) =>
            {
                if (type != TextObject) return;
                Native.FPDFPageObj_SetFillColor(pageObject, 0, 0, 0, 0);
                Native.FPDFPageObj_SetStrokeColor(pageObject, 0, 0, 0, 0);
            });
            var withoutText = RenderPage(page, scale, 0);
            return (new PageBitmap(withText.BgraPixels, withText.Width, withText.Height), new PageBitmap(withoutText.BgraPixels, withoutText.Width, withoutText.Height));
        });

    static (byte[] BgraPixels, int Width, int Height) RenderPage(IntPtr page, double scale, int flags)
    {
        int width = (int)(Native.FPDF_GetPageWidthF(page) * scale), height = (int)(Native.FPDF_GetPageHeightF(page) * scale);
        var bitmap = Native.FPDFBitmap_Create(width, height, 1);
        if (bitmap == IntPtr.Zero) throw new OutOfMemoryException("Not enough memory to draw the page.");
        try
        {
            Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0);
            Native.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, flags);
            var buffer = Native.FPDFBitmap_GetBuffer(bitmap);
            int stride = Native.FPDFBitmap_GetStride(bitmap);
            var pixels = new byte[width * height * 4];
            for (int row = 0; row < height; row++) Marshal.Copy(buffer + row * stride, pixels, row * width * 4, width * 4);
            return (pixels, width, height);
        }
        finally
        {
            Native.FPDFBitmap_Destroy(bitmap);
        }
    }

    /// <summary>Calls <paramref name="visit"/> with every object on the page and its type, inside forms too. Only PDFium's in-memory copy changes.</summary>
    static void EachObject(IntPtr page, Action<IntPtr, int> visit)
    {
        for (int i = 0, count = Native.FPDFPage_CountObjects(page); i < count; i++) Visit(Native.FPDFPage_GetObject(page, i));

        void Visit(IntPtr pageObject)
        {
            int type = Native.FPDFPageObj_GetType(pageObject);
            if (type != FormObject) visit(pageObject, type);
            else
                for (int i = 0, count = Native.FPDFFormObj_CountObjects(pageObject); i < count; i++)
                    Visit(Native.FPDFFormObj_GetObject(pageObject, (uint)i));
        }
    }

    internal static int PageCount(byte[] pdfBytes) => WithDocument(pdfBytes, Native.FPDF_GetPageCount);

    /// <summary>Page size as displayed (rotated), in whole points.</summary>
    internal static (int Width, int Height) PageSize(byte[] pdfBytes, int pageIndex) =>
        WithPage(pdfBytes, pageIndex, page => ((int)Native.FPDF_GetPageWidthF(page), (int)Native.FPDF_GetPageHeightF(page)));

    internal static (byte[] BgraPixels, int Width, int Height) Render(byte[] pdfBytes, int pageIndex, double scale, int flags) =>
        WithPage(pdfBytes, pageIndex, page => RenderPage(page, scale, flags));

    /// <summary>
    /// The page's characters in PDFium's reading order, with boxes in whole pixels of the page drawn at <paramref name="scale"/>.
    /// Characters PDFium has no box for are left out.
    /// </summary>
    internal static List<(char Char, int Left, int Top, int Right, int Bottom)> ReadCharacters(byte[] pdfBytes, int pageIndex, int scale) =>
        WithPage(pdfBytes, pageIndex, page =>
        {
            int width = (int)(Native.FPDF_GetPageWidthF(page) * scale), height = (int)(Native.FPDF_GetPageHeightF(page) * scale);
            var characters = new List<(char, int, int, int, int)>();
            var textPage = Native.FPDFText_LoadPage(page);
            if (textPage == IntPtr.Zero) return characters;
            try
            {
                for (int i = 0, count = Native.FPDFText_CountChars(textPage); i < count; i++)
                {
                    if (!Native.FPDFText_GetCharBox(textPage, i, out double left, out double right, out double bottom, out double top)) continue;
                    Native.FPDF_PageToDevice(page, 0, 0, width, height, 0, left, top, out int x1, out int y1);
                    Native.FPDF_PageToDevice(page, 0, 0, width, height, 0, right, bottom, out int x2, out int y2);
                    characters.Add(((char)Native.FPDFText_GetUnicode(textPage, i), x1, y1, x2, y2));
                }
                return characters;
            }
            finally
            {
                Native.FPDFText_ClosePage(textPage);
            }
        });

    static T WithPage<T>(byte[] pdfBytes, int pageIndex, Func<IntPtr, T> use) => WithDocument(pdfBytes, document =>
    {
        var page = Native.FPDF_LoadPage(document, pageIndex);
        if (page == IntPtr.Zero) throw new InvalidDataException($"Page {pageIndex + 1} couldn't be read.");
        try
        {
            return use(page);
        }
        finally
        {
            Native.FPDF_ClosePage(page);
        }
    });

    static T WithDocument<T>(byte[] pdfBytes, Func<IntPtr, T> use)
    {
        var pinned = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned); // PDFium reads the bytes for as long as the document is open
        try
        {
            var document = Native.FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(), pdfBytes.Length, null);
            if (document == IntPtr.Zero) throw new InvalidDataException("This file isn't a valid PDF, or it is damaged.");
            try
            {
                return use(document);
            }
            finally
            {
                Native.FPDF_CloseDocument(document);
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>Adds a path's segments, or a form's contents, mapped through <paramref name="outer"/> (the matrices of the forms around it).</summary>
    static void AddObject(IntPtr pageObject, Matrix outer, List<LineSegment> segments, List<PdfPoint> centers)
    {
        int type = Native.FPDFPageObj_GetType(pageObject);
        if (type is not (PathObject or FormObject)) return;
        var matrix = Native.FPDFPageObj_GetMatrix(pageObject, out var own) ? own.Then(outer) : outer;

        if (type == FormObject)
        {
            int count = Native.FPDFFormObj_CountObjects(pageObject);
            for (int i = 0; i < count && segments.Count < MaxSegments; i++)
                AddObject(Native.FPDFFormObj_GetObject(pageObject, (uint)i), matrix, segments, centers);
            return;
        }

        PdfPoint? current = null, subpathStart = null;
        var outline = new List<PdfPoint>(); // the subpath's points while it is all curves: a circle has a centre to snap to
        bool onlyCurves = true;
        void EndSubpath()
        {
            if (onlyCurves && CircleCenter(outline) is { } center) centers.Add(center);
            (onlyCurves, outline) = (true, []);
        }
        int segmentCount = Native.FPDFPath_CountSegments(pageObject);
        for (int i = 0; i < segmentCount && segments.Count < MaxSegments; i++)
        {
            var segment = Native.FPDFPath_GetPathSegment(pageObject, i);
            var point = PointOf(segment, matrix);
            switch (Native.FPDFPathSegment_GetType(segment))
            {
                case MoveTo:
                    EndSubpath();
                    current = subpathStart = point;
                    outline.Add(point);
                    break;
                case LineTo when current is { } from:
                    Add(segments, from, point);
                    current = point;
                    onlyCurves = false;
                    break;
                case BezierTo when current is { } from && i + 2 < segmentCount:
                    // A curve is three segments in a row: two control points, then the end point.
                    var control2 = PointOf(Native.FPDFPath_GetPathSegment(pageObject, i + 1), matrix);
                    segment = Native.FPDFPath_GetPathSegment(pageObject, i + 2);
                    var end = PointOf(segment, matrix);
                    AddBezier(segments, from, point, control2, end, outline);
                    current = end;
                    i += 2;
                    break;
            }
            if (Native.FPDFPathSegment_GetClose(segment) && current is { } last && subpathStart is { } first)
            {
                Add(segments, last, first);
                current = first;
            }
        }
        EndSubpath();
    }

    static PdfPoint PointOf(IntPtr segment, Matrix matrix)
    {
        Native.FPDFPathSegment_GetPoint(segment, out float x, out float y);
        return matrix.Apply(x, y);
    }

    static void AddBezier(List<LineSegment> segments, PdfPoint start, PdfPoint control1, PdfPoint control2, PdfPoint end, List<PdfPoint> outline)
    {
        var previous = start;
        for (int piece = 1; piece <= BezierPieces; piece++)
        {
            double t = (double)piece / BezierPieces, u = 1 - t;
            double a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
            var next = new PdfPoint(
                a * start.X + b * control1.X + c * control2.X + d * end.X,
                a * start.Y + b * control1.Y + c * control2.Y + d * end.Y);
            Add(segments, previous, next);
            outline.Add(next);
            previous = next;
        }
    }

    static void Add(List<LineSegment> segments, PdfPoint from, PdfPoint to)
    {
        if (Math.Abs(from.X - to.X) + Math.Abs(from.Y - to.Y) > 1e-6) segments.Add(new LineSegment(from, to));
    }

    /// <summary>PDFium's FS_MATRIX: x' = a x + c y + e, y' = b x + d y + f.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Matrix(float a, float b, float c, float d, float e, float f)
    {
        public float A = a, B = b, C = c, D = d, E = e, F = f;

        public static Matrix Identity => new(1, 0, 0, 1, 0, 0);

        public readonly PdfPoint Apply(double x, double y) => new(A * x + C * y + E, B * x + D * y + F);

        /// <summary>This matrix followed by <paramref name="outer"/>.</summary>
        public readonly Matrix Then(Matrix outer) => new(
            A * outer.A + B * outer.C, A * outer.B + B * outer.D,
            C * outer.A + D * outer.C, C * outer.B + D * outer.D,
            E * outer.A + F * outer.C + outer.E, E * outer.B + F * outer.D + outer.F);
    }

    static class Native
    {
        const string Pdfium = "pdfium"; // the same native library Docnet loads, so it shares Docnet's initialised PDFium

        [DllImport(Pdfium)] public static extern IntPtr FPDF_LoadMemDocument(IntPtr data, int size, [MarshalAs(UnmanagedType.LPStr)] string? password);
        [DllImport(Pdfium)] public static extern void FPDF_CloseDocument(IntPtr document);
        [DllImport(Pdfium)] public static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);
        [DllImport(Pdfium)] public static extern void FPDF_ClosePage(IntPtr page);
        [DllImport(Pdfium)] public static extern int FPDFPage_CountObjects(IntPtr page);
        [DllImport(Pdfium)] public static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);
        [DllImport(Pdfium)] public static extern int FPDFPageObj_GetType(IntPtr pageObject);
        [DllImport(Pdfium)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool FPDFPageObj_GetMatrix(IntPtr pageObject, out Matrix matrix);
        [DllImport(Pdfium)] public static extern int FPDFFormObj_CountObjects(IntPtr form);
        [DllImport(Pdfium)] public static extern IntPtr FPDFFormObj_GetObject(IntPtr form, uint index);
        [DllImport(Pdfium)] public static extern int FPDFPath_CountSegments(IntPtr path);
        [DllImport(Pdfium)] public static extern IntPtr FPDFPath_GetPathSegment(IntPtr path, int index);
        [DllImport(Pdfium)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool FPDFPathSegment_GetPoint(IntPtr segment, out float x, out float y);
        [DllImport(Pdfium)] public static extern int FPDFPathSegment_GetType(IntPtr segment);
        [DllImport(Pdfium)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool FPDFPathSegment_GetClose(IntPtr segment);
        [DllImport(Pdfium)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool FPDFPageObj_SetStrokeWidth(IntPtr pageObject, float width);
        [DllImport(Pdfium)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool FPDFPageObj_SetFillColor(IntPtr pageObject, uint r, uint g, uint b, uint a);
        [DllImport(Pdfium)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool FPDFPageObj_SetStrokeColor(IntPtr pageObject, uint r, uint g, uint b, uint a);
        [DllImport(Pdfium)] public static extern int FPDF_GetPageCount(IntPtr document);
        [DllImport(Pdfium)] public static extern IntPtr FPDFText_LoadPage(IntPtr page);
        [DllImport(Pdfium)] public static extern void FPDFText_ClosePage(IntPtr textPage);
        [DllImport(Pdfium)] public static extern int FPDFText_CountChars(IntPtr textPage);
        [DllImport(Pdfium)] public static extern uint FPDFText_GetUnicode(IntPtr textPage, int index);
        [DllImport(Pdfium)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool FPDFText_GetCharBox(IntPtr textPage, int index, out double left, out double right, out double bottom, out double top);
        [DllImport(Pdfium)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool FPDF_PageToDevice(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, out int deviceX, out int deviceY);
        [DllImport(Pdfium)] public static extern float FPDF_GetPageWidthF(IntPtr page);
        [DllImport(Pdfium)] public static extern float FPDF_GetPageHeightF(IntPtr page);
        [DllImport(Pdfium)] public static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
        [DllImport(Pdfium)] public static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
        [DllImport(Pdfium)] public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
        [DllImport(Pdfium)] public static extern int FPDFBitmap_GetStride(IntPtr bitmap);
        [DllImport(Pdfium)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);
        [DllImport(Pdfium)] public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int left, int top, int width, int height, int rotate, int flags);
    }
}
