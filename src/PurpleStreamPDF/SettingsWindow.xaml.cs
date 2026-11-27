using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PurpleStreamPDF.Core;

namespace PurpleStreamPDF;

/// <summary>The styles the app draws with on its own: QA boxes, compare clouds, new dimensions. OK writes them into the settings.</summary>
public partial class SettingsWindow : Window
{
    static readonly double[] LineWidths = [0.25, 0.35, 0.5, 0.7, 1, 1.5, 2, 3];
    static readonly double[] Margins = [0, 0.5, 1, 1.5, 2, 3, 5];
    static readonly double[] TextSizes = [6, 7, 8, 9, 10, 12, 14];

    readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ShowColor(QaColor, settings.QaBox.Color);
        ShowNumbers(QaLineWidth, LineWidths, settings.QaBox.LineWidth);
        ShowNumbers(QaMargin, Margins, settings.QaBox.Margin);
        ShowColor(DiffColor, settings.DiffCloud.Color);
        ShowNumbers(DiffLineWidth, LineWidths, settings.DiffCloud.LineWidth);
        ShowChoices(DiffOutline, [AreaLineStyle.Cloud, AreaLineStyle.Dashed, AreaLineStyle.Solid], settings.DiffCloud.Outline);
        ShowColor(DimensionColor, settings.Dimension.Color);
        ShowNumbers(DimensionLineWidth, LineWidths, settings.Dimension.LineWidth);
        ShowChoices(DimensionEnding, [LineEnding.Tick, LineEnding.Arrow, LineEnding.Dot, LineEnding.None], settings.Dimension.Ending);
        ShowNumbers(DimensionTextSize, TextSizes, settings.Dimension.TextSize);
    }

    void OnOk(object sender, RoutedEventArgs e)
    {
        _settings.QaBox = new() { Color = (string)QaColor.Tag, LineWidth = Number(QaLineWidth), Margin = Number(QaMargin) };
        _settings.DiffCloud = new() { Color = (string)DiffColor.Tag, LineWidth = Number(DiffLineWidth), Outline = (AreaLineStyle)DiffOutline.SelectedItem };
        // The dimension style is changed in place: the markup layer holds this object.
        _settings.Dimension.Color = (string)DimensionColor.Tag;
        _settings.Dimension.LineWidth = Number(DimensionLineWidth);
        _settings.Dimension.Ending = (LineEnding)DimensionEnding.SelectedItem;
        _settings.Dimension.TextSize = Number(DimensionTextSize);
        DialogResult = true;
    }

    void OnPickColor(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.ColorTranslator.FromHtml((string)button.Tag) };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) ShowColor(button, $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
    }

    /// <summary>A swatch and the colour's code on the button; the code is kept in its Tag.</summary>
    static void ShowColor(Button button, string hex)
    {
        button.Tag = hex;
        button.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new Border { Width = 28, Height = 14, Margin = new Thickness(0, 0, 8, 0), Background = (Brush)new BrushConverter().ConvertFromString(hex)!, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) },
                new TextBlock { Text = hex, VerticalAlignment = VerticalAlignment.Center },
            },
        };
    }

    /// <summary>Sizes in points to pick from, the current one among them even if it isn't a usual size.</summary>
    static void ShowNumbers(ComboBox box, double[] values, double current)
    {
        foreach (double value in values.Append(current).Distinct().Order())
            box.Items.Add(new ComboBoxItem { Content = $"{value:0.##} pt", Tag = value });
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().First(item => (double)item.Tag == current);
    }

    static double Number(ComboBox box) => (double)((ComboBoxItem)box.SelectedItem).Tag;

    static void ShowChoices<T>(ComboBox box, T[] choices, T current) where T : struct, Enum
    {
        box.ItemsSource = choices.Contains(current) ? choices : [.. choices, current];
        box.SelectedItem = current;
    }
}
