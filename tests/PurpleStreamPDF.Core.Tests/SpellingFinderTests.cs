namespace PurpleStreamPDF.Core.Tests;

public class SpellingFinderTests
{
    static readonly HashSet<string> Dictionary = ["Structural", "Toppings", "In", "Precast", "Planks", "To", "Be", "Reinforced", "Dartford", "with", "mesh"];

    [Fact]
    public void Finds_misspelt_words_in_capitals_and_where_they_are()
    {
        var asked = new List<string>();
        var text = ScaleOnSheetTests.Sheet(("STRUCTURAL TOPPNGS IN PRECAST PLANKS TO BE", 100, 100), ("REINFORCED WTH mesh, Dartford", 100, 120));

        var misspelt = SpellingFinder.FindMisspelt(text, word => { asked.Add(word); return Dictionary.Contains(word); });

        Assert.Equal(["TOPPNGS"], misspelt.Select(found => found.Word)); // WTH is three capitals: skipped as an abbreviation
        Assert.Equal(new ViewRect(166, 100, 208, 110), misspelt[0].Box); // characters 11 to 17, 6 pt each
        Assert.Contains("Toppngs", asked);
        Assert.DoesNotContain("TOPPNGS", asked);
    }

    [Fact]
    public void Skips_numbers_with_units_short_words_and_asks_each_word_once()
    {
        var asked = new List<string>();
        var text = ScaleOnSheetTests.Sheet(("AT 150mm CENTRES S355 QS TBC", 100, 100), ("CENTRES", 100, 120));

        var misspelt = SpellingFinder.FindMisspelt(text, word => { asked.Add(word); return false; });

        Assert.Equal(["CENTRES", "CENTRES"], misspelt.Select(found => found.Word));
        Assert.Equal(["Centres"], asked);
    }
}
