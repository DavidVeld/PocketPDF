using System.Globalization;
using System.Text.RegularExpressions;

namespace PocketPDF.Core;

/// <summary>
/// A drawing scale as real millimetres per PDF point. At 1:1 one point is 25.4/72 mm of paper; at 1:50 it's 50 times that.
/// </summary>
public static class DrawingScale
{
    public const double PaperMmPerPoint = 25.4 / 72;

    public static readonly int[] CommonRatios = [1, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 1250, 2500];

    public static double FromRatio(double ratio) => PaperMmPerPoint * ratio;

    /// <summary>The scale that makes a line of <paramref name="lengthInPoints"/> measure <paramref name="realMm"/>.</summary>
    public static double FromCalibration(double lengthInPoints, double realMm) => realMm / lengthInPoints;

    /// <summary>"1:50", or "1:47.3" for a calibrated scale that isn't a round ratio.</summary>
    public static string Describe(double mmPerPoint)
    {
        double ratio = mmPerPoint / PaperMmPerPoint;
        double rounded = Math.Round(ratio);
        return rounded >= 1 && Math.Abs(ratio - rounded) / rounded < 0.001
            ? $"1:{rounded.ToString("0", CultureInfo.InvariantCulture)}"
            : $"1:{ratio.ToString("0.##", CultureInfo.InvariantCulture)}";
    }

    /// <summary>What Shift rounds a drawn size to, in real mm: whole mm up to 1:20, tens of mm at coarser scales.</summary>
    public static double RoundingStepMm(double mmPerPoint) => Math.Round(mmPerPoint / PaperMmPerPoint, 2) <= 20 ? 1 : 10;

    public static string FormatLength(double mm) => $"{mm.ToString("#,0", CultureInfo.CurrentCulture)} mm";

    /// <summary>
    /// A typed length in mm: "600", "60 cm" or "0,6m" (mm when no unit; a comma counts as the decimal point).
    /// Null if it isn't a positive length.
    /// </summary>
    public static double? ParseLengthMm(string text)
    {
        text = text.Trim();
        int unitStart = text.Length;
        while (unitStart > 0 && (char.IsLetter(text[unitStart - 1]) || text[unitStart - 1] is '"' or '\'')) unitStart--;
        string unit = unitStart < text.Length ? text[unitStart..] : "mm";
        return double.TryParse(text[..unitStart].Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && value > 0 && MmPerUnit(unit) is { } mmPerUnit ? value * mmPerUnit : null;
    }

    public static string FormatArea(double squareMm) => $"{(squareMm / 1e6).ToString("#,0.00", CultureInfo.CurrentCulture)} m²";

    /// <summary>
    /// The ratio of the "1:###" written furthest bottom right on the sheet (where the title block is), or null when the
    /// sheet has none. "NTS" has no ratio, so it is never picked: another scale on the sheet is, if there is one.
    /// </summary>
    public static int? FindScaleOnSheet(PageText text)
    {
        string all = string.Concat(text.Characters.Select(character => character.Char));
        int? best = null;
        double bestCorner = double.MinValue;
        foreach (Match match in Regex.Matches(all, @"(?<![\d.:])1 ?: ?(\d{1,3}(?:,\d{3})+|\d+)(?![\d:])"))
        {
            var one = text.Characters[match.Index];
            if (!one.HasBox || !int.TryParse(match.Groups[1].Value.Replace(",", ""), out int ratio) || ratio == 0) continue;
            double corner = one.Box.Right + one.Box.Bottom; // view space: y grows downwards
            if (corner > bestCorner) (best, bestCorner) = (ratio, corner);
        }
        return best;
    }

    /// <summary>Millimetres in one of a PDF /Measure dictionary's units, or null for a unit PocketPDF doesn't know.</summary>
    public static double? MmPerUnit(string unit) => unit.Trim().ToLowerInvariant() switch
    {
        "mm" => 1,
        "cm" => 10,
        "m" => 1000,
        "km" => 1_000_000,
        "in" or "\"" => 25.4,
        "ft" or "'" => 304.8,
        "yd" => 914.4,
        "mi" => 1_609_344,
        "pt" => PaperMmPerPoint,
        _ => null,
    };
}
