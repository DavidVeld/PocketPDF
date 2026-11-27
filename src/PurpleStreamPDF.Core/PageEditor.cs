using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PurpleStreamPDF.Core;

/// <summary>
/// New page orders for the picked pages of a document of <c>count</c> pages: each lists the page indexes in their new order.
/// Picked pages keep their order among themselves.
/// </summary>
public static class PageOrder
{
    public static int[] Without(int count, IReadOnlySet<int> picked) => Enumerable.Range(0, count).Where(page => !picked.Contains(page)).ToArray();

    public static int[] ToTop(int count, IReadOnlySet<int> picked) => [.. picked.Order(), .. Without(count, picked)];

    public static int[] ToEnd(int count, IReadOnlySet<int> picked) => [.. Without(count, picked), .. picked.Order()];

    /// <summary>Each picked page one place up, past the page before it unless that one is picked too (or it is already first).</summary>
    public static int[] Up(int count, IReadOnlySet<int> picked)
    {
        var order = Enumerable.Range(0, count).ToArray();
        for (int i = 1; i < count; i++)
            if (picked.Contains(order[i]) && !picked.Contains(order[i - 1])) (order[i - 1], order[i]) = (order[i], order[i - 1]);
        return order;
    }

    public static int[] Down(int count, IReadOnlySet<int> picked)
    {
        var order = Enumerable.Range(0, count).ToArray();
        for (int i = count - 2; i >= 0; i--)
            if (picked.Contains(order[i]) && !picked.Contains(order[i + 1])) (order[i], order[i + 1]) = (order[i + 1], order[i]);
        return order;
    }
}

/// <summary>
/// Changes the pages of a PDF: keeps some in a new order, or puts in a blank sheet. The file is changed in place, so what
/// belongs to the whole document (layers, forms, document properties) stays.
/// </summary>
public static class PageEditor
{
    /// <summary>The PDF with only the pages in <paramref name="order"/> (indexes, each once), in that order: the others are deleted.</summary>
    public static byte[] Rearrange(byte[] pdfBytes, IReadOnlyList<int> order)
    {
        if (order.Count == 0) throw new ArgumentException("A PDF needs at least one page.", nameof(order));
        using var document = Open(pdfBytes);
        var pages = document.Pages.Cast<PdfPage>().ToList();
        if (order.Distinct().Count() != order.Count || order.Any(page => page < 0 || page >= pages.Count))
            throw new ArgumentException("Each page index must be in the document, and once only.", nameof(order));
        foreach (var page in pages.Where((_, pageIndex) => !order.Contains(pageIndex))) document.Pages.Remove(page);
        for (int i = 0; i < order.Count; i++) document.Pages.MovePage(document.Pages.Cast<PdfPage>().ToList().IndexOf(pages[order[i]]), i);
        return Save(document);
    }

    /// <summary>The PDF with a blank sheet of this size put in at <paramref name="pageIndex"/> (the page count adds it at the end).</summary>
    public static byte[] InsertBlank(byte[] pdfBytes, int pageIndex, double widthMm, double heightMm)
    {
        if (!BlankPdf.IsValidSide(widthMm) || !BlankPdf.IsValidSide(heightMm))
            throw new ArgumentOutOfRangeException(nameof(widthMm), $"Each side must be from {PaperSize.MinSideMm} to {PaperSize.MaxSideMm} mm.");
        using var document = Open(pdfBytes);
        var page = document.InsertPage(pageIndex);
        page.Width = XUnit.FromMillimeter(widthMm);
        page.Height = XUnit.FromMillimeter(heightMm);
        return Save(document);
    }

    static PdfDocument Open(byte[] pdfBytes) =>
        PdfReader.Open(new MemoryStream(pdfBytes), PdfDocumentOpenMode.Modify, request => request.Abort = true)
            ?? throw new InvalidOperationException("A password is needed to change this file.");

    static byte[] Save(PdfDocument document)
    {
        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }
}
