namespace PocketPDF.Core;

/// <summary>A character on the page, with its box in view points. PDFium's own spaces and line breaks have an empty box.</summary>
public readonly record struct PageCharacter(char Char, ViewRect Box)
{
    public bool HasBox => Box.Width > 0 && Box.Height > 0;
}

/// <summary>
/// The text on a page in PDFium's reading order, for selecting and copying. PDFium adds spaces between words drawn
/// apart and \r\n at line ends, so joining the characters gives readable text without guessing gaps.
/// </summary>
public sealed class PageText(IReadOnlyList<PageCharacter> characters)
{
    public IReadOnlyList<PageCharacter> Characters { get; } = characters;

    public bool IsEmpty => !Characters.Any(character => character.HasBox);

    /// <summary>The character nearest to a point, if one is within <paramref name="tolerance"/> points of it; otherwise -1.</summary>
    public int CharacterAt(ViewPoint point, double tolerance = double.MaxValue)
    {
        int nearest = -1;
        double nearestDistance = tolerance;
        for (int i = 0; i < Characters.Count; i++)
        {
            if (!Characters[i].HasBox) continue;
            var box = Characters[i].Box;
            double dx = Math.Max(0, Math.Max(box.Left - point.X, point.X - box.Right));
            double dy = Math.Max(0, Math.Max(box.Top - point.Y, point.Y - box.Bottom));
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance <= nearestDistance) (nearest, nearestDistance) = (i, distance);
            if (distance == 0) break;
        }
        return nearest;
    }

    /// <summary>First and last character of the word around a character.</summary>
    public (int Start, int End) WordAt(int index)
    {
        if (!char.IsLetterOrDigit(Characters[index].Char)) return (index, index);
        int start = index, end = index;
        while (start > 0 && IsWordCharacter(start - 1)) start--;
        while (end < Characters.Count - 1 && IsWordCharacter(end + 1)) end++;
        return (start, end);
    }

    bool IsWordCharacter(int index) => Characters[index].Char is var c && (char.IsLetterOrDigit(c) || c is '.' or ',' or '-' or '_' or '/');

    /// <summary>The text from one character to another, both included, in either order. Line breaks are \r\n, as the clipboard wants.</summary>
    public string TextOf(int from, int to)
    {
        var (start, end) = Ordered(from, to);
        var text = new System.Text.StringBuilder(end - start + 1);
        for (int i = start; i <= end; i++) text.Append(Characters[i].Char);
        return text.ToString().Trim(' ', '\r', '\n');
    }

    /// <summary>One rectangle per line of the selection, covering its characters, for showing and highlighting it.</summary>
    public List<ViewRect> LineRects(int from, int to)
    {
        var (start, end) = Ordered(from, to);
        var rects = new List<ViewRect>();
        ViewRect? line = null;
        for (int i = start; i <= end; i++)
        {
            var character = Characters[i];
            if (character.Char == '\n' && line != null)
            {
                rects.Add(line.Value);
                line = null;
            }
            if (!character.HasBox) continue;
            line = line is { } current ? ViewRect.Around(current.Corners().Concat(character.Box.Corners())) : character.Box;
        }
        if (line != null) rects.Add(line.Value);
        return rects;
    }

    static (int Start, int End) Ordered(int from, int to) => from <= to ? (from, to) : (to, from);
}
