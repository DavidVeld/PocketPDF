using Docnet.Core;
using Docnet.Core.Models;

namespace PurpleStreamPDF.Core;

/// <summary>
/// The only gateway to PDFium. PDFium is not thread-safe, so every call goes through one lock. Docnet only loads and
/// starts PDFium: its document readers crash the app at random (see <see cref="VectorExtractor"/>).
/// </summary>
public sealed class PdfRenderer
{
    static readonly object PdfiumLock = new();

    readonly byte[] _pdfBytes;
    readonly Dictionary<int, PageText> _textByPage = []; // guarded by PdfiumLock
    readonly Dictionary<int, (List<LineSegment> Segments, List<PdfPoint> CircleCenters)> _lineWorkByPage = []; // guarded by PdfiumLock

    public int PageCount { get; }

    public PdfRenderer(byte[] pdfBytes)
    {
        _pdfBytes = pdfBytes;
        lock (PdfiumLock)
        {
            _ = DocLib.Instance; // makes sure Docnet has started PDFium before calling it directly
            PageCount = VectorExtractor.PageCount(pdfBytes);
        }
    }

    /// <summary>Page size as displayed, in whole points.</summary>
    public (double Width, double Height) PageSize(int pageIndex)
    {
        lock (PdfiumLock) return VectorExtractor.PageSize(_pdfBytes, pageIndex);
    }

    /// <summary>The page's text with character boxes in view points. Read once per page; empty if the text is drawn as lines.</summary>
    public PageText GetText(int pageIndex)
    {
        // PDFium gives whole pixels: reading at 4x gives boxes to a quarter point, enough for 2 mm drawing text.
        const int Scale = 4;
        lock (PdfiumLock)
        {
            if (_textByPage.TryGetValue(pageIndex, out var cached)) return cached;
            var characters = VectorExtractor.ReadCharacters(_pdfBytes, pageIndex, Scale).Select(character =>
            {
                var (_, left, top, right, bottom) = character; // on rotated pages left and right (or top and bottom) can come swapped
                return new PageCharacter(character.Char, new ViewRect(
                    Math.Min(left, right) / (double)Scale, Math.Min(top, bottom) / (double)Scale,
                    Math.Max(left, right) / (double)Scale, Math.Max(top, bottom) / (double)Scale));
            }).ToList();
            return _textByPage[pageIndex] = new PageText(characters);
        }
    }

    /// <summary>The page's line work in PDF space, for snapping. Read once per page; empty for a scan.</summary>
    public IReadOnlyList<LineSegment> GetLineWork(int pageIndex) => LineWork(pageIndex).Segments;

    /// <summary>The centres of the circles in the page's line work, in PDF space, for snapping.</summary>
    public IReadOnlyList<PdfPoint> GetCircleCenters(int pageIndex) => LineWork(pageIndex).CircleCenters;

    (List<LineSegment> Segments, List<PdfPoint> CircleCenters) LineWork(int pageIndex)
    {
        lock (PdfiumLock)
        {
            if (_lineWorkByPage.TryGetValue(pageIndex, out var cached)) return cached;
            return _lineWorkByPage[pageIndex] = VectorExtractor.Extract(_pdfBytes, pageIndex);
        }
    }

    /// <summary>The drawing (no annotations) with and without its text, for <see cref="OverlapFinder"/>.</summary>
    public (PageBitmap WithText, PageBitmap WithoutText) RenderWithAndWithoutText(int pageIndex, double scale)
    {
        lock (PdfiumLock)
        {
            return VectorExtractor.RenderWithAndWithoutText(_pdfBytes, pageIndex, scale);
        }
    }

    /// <summary>
    /// Renders a page to BGRA pixels. scale 1 = 72 dpi. Without annotations it draws only the drawing itself (for comparing revisions).
    /// With hairlines every line of the drawing is one pixel wide, whatever its line weight. With an area, only that part
    /// of the page is drawn, so a close zoom can be sharp without drawing the whole sheet that big.
    /// </summary>
    public (byte[] BgraPixels, int Width, int Height) Render(int pageIndex, double scale, bool includeAnnotations = true, bool hairlines = false, PixelArea? area = null)
    {
        lock (PdfiumLock)
        {
            int flags = includeAnnotations ? (int)RenderFlags.RenderAnnotations : 0;
            // shortcut: reopens the document for every render; keep it open if big files feel slow.
            return hairlines ? VectorExtractor.RenderHairlines(_pdfBytes, pageIndex, scale, flags, area) : VectorExtractor.Render(_pdfBytes, pageIndex, scale, flags, area);
        }
    }
}
