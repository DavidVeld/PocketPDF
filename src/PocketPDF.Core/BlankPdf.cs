using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PocketPDF.Core;

/// <summary>A sheet size in mm.</summary>
public sealed record PaperSize(string Name, double WidthMm, double HeightMm)
{
    /// <summary>The ISO A sizes from A4 to A0, portrait.</summary>
    public static readonly IReadOnlyList<PaperSize> Presets =
        [new("A4", 210, 297), new("A3", 297, 420), new("A2", 420, 594), new("A1", 594, 841), new("A0", 841, 1189)];

    /// <summary>Smallest side of a new sheet.</summary>
    public const double MinSideMm = 10;

    /// <summary>Largest side a PDF page may have: 200 inches.</summary>
    public const double MaxSideMm = 5080;

    /// <summary>This size turned so its long side runs across (landscape) or up (portrait).</summary>
    public PaperSize Oriented(bool landscape) =>
        WidthMm == HeightMm || WidthMm > HeightMm == landscape ? this : this with { WidthMm = HeightMm, HeightMm = WidthMm };

    /// <summary>Just the name, as lists and screen readers show it.</summary>
    public override string ToString() => Name;
}

/// <summary>New, empty PDFs to draw on.</summary>
public static class BlankPdf
{
    /// <summary>A PDF with one empty page of this size. Each side must be from <see cref="PaperSize.MinSideMm"/> to <see cref="PaperSize.MaxSideMm"/>.</summary>
    public static byte[] Create(double widthMm, double heightMm)
    {
        if (!IsValidSide(widthMm) || !IsValidSide(heightMm))
            throw new ArgumentOutOfRangeException(nameof(widthMm), $"Each side must be from {PaperSize.MinSideMm} to {PaperSize.MaxSideMm} mm.");
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = XUnit.FromMillimeter(widthMm);
        page.Height = XUnit.FromMillimeter(heightMm);
        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }

    public static bool IsValidSide(double mm) => mm is >= PaperSize.MinSideMm and <= PaperSize.MaxSideMm;
}
