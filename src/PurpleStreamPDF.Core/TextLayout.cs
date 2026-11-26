namespace PurpleStreamPDF.Core;

/// <summary>
/// The system fonts, which only the app can read. Text in one of them is measured with it, and drawn into the PDF as the
/// outlines of its letters, so every viewer shows it without the font file.
/// </summary>
public interface IFontSource
{
    double LineWidth(string family, string line, double fontSize);

    /// <summary>From the top of a line (<see cref="TextLayout.LineHeightPerFontSize"/> high) to its baseline, per point of font size.</summary>
    double BaselinePerFontSize(string family);

    /// <summary>The letters of one line as closed polygons in view space (Y down), the baseline starting at <paramref name="baselineStart"/>.</summary>
    IReadOnlyList<IReadOnlyList<ViewPoint>> Outlines(string family, string line, double fontSize, ViewPoint baselineStart);
}

/// <summary>
/// Measures text in Helvetica, the font written into the PDF. The app shows text in Arial, which has the same widths,
/// so the box on screen and the box in the file match. Text in another font (<see cref="Markup.Font"/>) is measured by <see cref="Fonts"/>.
/// </summary>
public static class TextLayout
{
    public const string ScreenFontFamily = "Arial";

    /// <summary>Set by the app. Without it (in the tests) all text is Helvetica.</summary>
    public static IFontSource? Fonts { get; set; }

    /// <summary>The font source for <paramref name="font"/>, or null for the default Helvetica.</summary>
    internal static IFontSource? SourceFor(string? font) => font == null ? null : Fonts;
    public const double LineHeightPerFontSize = 1.2;
    /// <summary>Distance from the top of a line to its baseline, per point of font size (Arial at 1.2 line height).</summary>
    public const double BaselinePerFontSize = 0.97;

    // Characters outside the table (Ø, é, ...) count as wide as "O", so their box is never too small and clips the text.
    const int DefaultCharWidth = 778;

    // Helvetica advance widths for characters 32 (space) to 126 (~), in 1/1000 of the font size. From the standard Adobe AFM.
    static readonly short[] HelveticaWidths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    public static string[] Lines(string text) => text.Split('\n');

    public static double LineWidth(string line, double fontSize, string? font = null) =>
        SourceFor(font) is { } fonts ? fonts.LineWidth(font!, line, fontSize)
            : line.Sum(character => character is >= ' ' and <= '~' ? HelveticaWidths[character - ' '] : DefaultCharWidth) * fontSize / 1000;

    /// <summary>From the top of a line to its baseline, per point of font size.</summary>
    public static double Baseline(string? font) => SourceFor(font) is { } fonts ? fonts.BaselinePerFontSize(font!) : BaselinePerFontSize;

    /// <summary>Breaks each line at spaces so it fits <paramref name="width"/>. A word wider than that keeps a line of its own.</summary>
    public static string Wrap(string text, double fontSize, double width, string? font = null) => string.Join('\n', Lines(text).Select(line =>
    {
        var wrapped = new List<string>();
        string current = "";
        foreach (var word in line.Split(' '))
        {
            string longer = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && LineWidth(longer, fontSize, font) > width)
            {
                wrapped.Add(current);
                current = word;
            }
            else current = longer;
        }
        wrapped.Add(current);
        return string.Join('\n', wrapped);
    }));

    public static (double Width, double Height) Measure(string text, double fontSize, string? font = null)
    {
        var lines = Lines(text);
        return (lines.Max(line => LineWidth(line, fontSize, font)), lines.Length * fontSize * LineHeightPerFontSize);
    }
}
