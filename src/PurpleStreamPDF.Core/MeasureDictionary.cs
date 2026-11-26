using PdfSharp.Pdf;
using static PurpleStreamPDF.Core.AnnotationReader;

namespace PurpleStreamPDF.Core;

/// <summary>
/// Reads and writes a PDF /Measure dictionary (ISO 32000 12.9, "rectilinear"), which PDF viewers use for a
/// measurement's or a viewport's scale. Its /X entry says how many units one PDF point is.
/// </summary>
static class MeasureDictionary
{
    public static PdfDictionary Create(PdfDocument document, double mmPerPoint)
    {
        PdfDictionary Format(string unit, double conversion, int precision) => new(document)
        {
            Elements =
            {
                ["/Type"] = new PdfName("/NumberFormat"), ["/U"] = new PdfString(unit),
                ["/C"] = new PdfReal(conversion), ["/D"] = new PdfInteger(precision),
            },
        };
        var distance = Format("mm", mmPerPoint, 1);
        return new PdfDictionary(document)
        {
            Elements =
            {
                ["/Type"] = new PdfName("/Measure"),
                ["/Subtype"] = new PdfName("/RL"),
                ["/R"] = new PdfString(DrawingScale.Describe(mmPerPoint)),
                ["/X"] = new PdfArray(document, distance),
                ["/D"] = new PdfArray(document, Format("mm", 1, 1)), // distances: the X units as they are
                ["/A"] = new PdfArray(document, Format("sq m", mmPerPoint * mmPerPoint / 1e6, 100)),
            },
        };
    }

    /// <summary>Millimetres per point from a /Measure dictionary, or null if it isn't one PurpleStreamPDF understands.</summary>
    public static double? MmPerPoint(PdfItem? item)
    {
        if (Resolve(item) is not PdfDictionary measure || measure.Elements.GetName("/Subtype") is not ("/RL" or "")) return null;
        if (Resolve(measure.Elements["/X"]) is not PdfArray { Elements.Count: > 0 } formats || Resolve(formats.Elements[0]) is not PdfDictionary first) return null;
        double conversion = first.Elements.GetReal("/C");
        double? mmPerUnit = DrawingScale.MmPerUnit(first.Elements.GetString("/U"));
        return conversion > 0 && mmPerUnit is { } unit ? conversion * unit : null;
    }
}
