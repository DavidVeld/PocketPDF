using System.Text.RegularExpressions;

namespace PurpleStreamPDF.Core;

public static class DrawingNumber
{
    /// <summary>
    /// The drawing number written furthest bottom right on the sheet (where the title block is), or null when there is none:
    /// a word of four or more parts joined by hyphens, with a digit in it, such as P452275-WW-FF-00-D-S-41000.
    /// Dates (19-06-2026) and clause numbers (1995-1-1) have too few parts to count.
    /// </summary>
    public static string? FindOnSheet(PageText text)
    {
        string all = string.Concat(text.Characters.Select(character => character.Char));
        string? best = null;
        double bestCorner = double.MinValue;
        foreach (Match match in Regex.Matches(all, @"(?<![\w-])[A-Za-z0-9]+(?:-[A-Za-z0-9]+){3,}(?![\w-])"))
        {
            var last = text.Characters[match.Index + match.Length - 1];
            if (!last.HasBox || !match.Value.Any(char.IsDigit)) continue;
            double corner = last.Box.Right + last.Box.Bottom; // view space: y grows downwards
            if (corner > bestCorner) (best, bestCorner) = (match.Value, corner);
        }
        return best;
    }
}
