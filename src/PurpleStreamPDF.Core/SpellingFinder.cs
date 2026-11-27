using System.Text.RegularExpressions;

namespace PurpleStreamPDF.Core;

/// <summary>Finds the words on a page that a spell checker doesn't know, with where they are.</summary>
public static class SpellingFinder
{
    /// <summary>
    /// The misspelt words and their boxes in view points, in reading order. <paramref name="isSpeltRight"/> is asked once
    /// per word. Words in capitals are asked in title case ("Toppings"), so capitals and names are checked like ordinary
    /// text. Skipped: words touching a digit (150mm, S355), words under three letters, and words of three capitals
    /// (TBC, MEP), which on drawings are mostly abbreviations.
    /// </summary>
    public static List<(string Word, ViewRect Box)> FindMisspelt(PageText text, Func<string, bool> isSpeltRight)
    {
        string all = string.Concat(text.Characters.Select(character => character.Char));
        var known = new Dictionary<string, bool>();
        var misspelt = new List<(string, ViewRect)>();
        foreach (Match match in Regex.Matches(all, @"(?<![\p{L}\p{N}'’])\p{L}+(?:['’]\p{L}+)*(?![\p{L}\p{N}'’])"))
        {
            string word = match.Value;
            bool capitals = !word.Any(char.IsLower);
            if (word.Length < 3 || (capitals && word.Length == 3)) continue;
            string asked = capitals ? word[0] + word[1..].ToLowerInvariant() : word;
            if (!known.TryGetValue(asked, out bool right)) known[asked] = right = isSpeltRight(asked);
            if (right) continue;
            var corners = text.Characters.Skip(match.Index).Take(match.Length).Where(character => character.HasBox).SelectMany(character => character.Box.Corners()).ToList();
            if (corners.Count > 0) misspelt.Add((word, ViewRect.Around(corners)));
        }
        return misspelt;
    }
}
