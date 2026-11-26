namespace PurpleStreamPDF.Core;

/// <summary>
/// Finds text that lines, fills or hatching run through, so it may be hard to read. Each character's box is looked at
/// on the page drawn without its text: dark ink left inside it is something else crossing the character.
/// </summary>
public static class OverlapFinder
{
    /// <summary>Room around the clashing characters for the cloud, in points. Also joins the characters of one word.</summary>
    public const double Padding = 5;
    /// <summary>Share of the box trimmed off each side, so a leader or underline that only touches the text doesn't count.</summary>
    const double Inset = 0.15;
    /// <summary>A character clashes when at least this share of its trimmed box is ink.</summary>
    const double InkShare = 0.04;
    /// <summary>Darker than this (0 black, 255 white) is ink. Light tints behind text, and faint thin grey lines, stay under it.</summary>
    const int DarkLevel = 170;

    /// <summary>Areas around clashing text, in view points, padded and clamped to the page. Overlapping areas are merged.</summary>
    public static List<ViewRect> FindOverlaps(PageText text, PageBitmap withText, PageBitmap withoutText, double pixelsPerPoint)
    {
        double pageWidth = withText.Width / pixelsPerPoint, pageHeight = withText.Height / pixelsPerPoint;
        var areas = new List<ViewRect>();
        foreach (var character in text.Characters)
        {
            // Only letters and digits: punctuation such as the "__" divider in CAD section tags is line work itself.
            if (!character.HasBox || !char.IsLetterOrDigit(character.Char)) continue;
            var box = character.Box;
            if (!IsDrawn(box, withText, withoutText, pixelsPerPoint)) continue; // invisible text, such as the searchable layer of a scan
            var inner = new ViewRect(box.Left + box.Width * Inset, box.Top + box.Height * Inset, box.Right - box.Width * Inset, box.Bottom - box.Height * Inset);
            var (left, top, right, bottom) = Pixels(inner, withoutText, pixelsPerPoint);
            int ink = 0;
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
                if (IsInk(withoutText.BgraPixels, (y * withoutText.Width + x) * 4)) ink++;
            if (ink < Math.Max(2, InkShare * (right - left) * (bottom - top))) continue;
            var area = box.Inflate(Padding);
            areas.Add(new ViewRect(Math.Max(0, area.Left), Math.Max(0, area.Top), Math.Min(pageWidth, area.Right), Math.Min(pageHeight, area.Bottom)));
        }
        return ChangeFinder.MergeOverlapping(areas);
    }

    /// <summary>Whether hiding the text changed any pixel in the box: if not, the character isn't drawn.</summary>
    static bool IsDrawn(ViewRect box, PageBitmap withText, PageBitmap withoutText, double pixelsPerPoint)
    {
        var (left, top, right, bottom) = Pixels(box, withText, pixelsPerPoint);
        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
        {
            int offset = (y * withText.Width + x) * 4;
            if (withText.BgraPixels.AsSpan(offset, 4).SequenceEqual(withoutText.BgraPixels.AsSpan(offset, 4))) continue;
            return true;
        }
        return false;
    }

    /// <summary>A box in whole pixels, at least one pixel big, within the bitmap.</summary>
    static (int Left, int Top, int Right, int Bottom) Pixels(ViewRect box, PageBitmap bitmap, double pixelsPerPoint)
    {
        int left = Math.Clamp((int)Math.Floor(box.Left * pixelsPerPoint), 0, bitmap.Width - 1);
        int top = Math.Clamp((int)Math.Floor(box.Top * pixelsPerPoint), 0, bitmap.Height - 1);
        int right = Math.Clamp((int)Math.Ceiling(box.Right * pixelsPerPoint), left + 1, bitmap.Width);
        int bottom = Math.Clamp((int)Math.Ceiling(box.Bottom * pixelsPerPoint), top + 1, bitmap.Height);
        return (left, top, right, bottom);
    }

    /// <summary>The pixel on white paper (the bitmap is see-through where nothing is drawn) is darker than <see cref="DarkLevel"/>.</summary>
    static bool IsInk(byte[] bgra, int offset)
    {
        double brightness = 0.114 * bgra[offset] + 0.587 * bgra[offset + 1] + 0.299 * bgra[offset + 2];
        return 255 - bgra[offset + 3] * (255 - brightness) / 255 < DarkLevel;
    }
}
