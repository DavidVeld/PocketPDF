using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PocketPDF;

/// <summary>"Show me the tricks": a tour that dims the window and lights up one area at a time with a short explanation.</summary>
public partial class MainWindow
{
    List<(FrameworkElement Target, string Title, string Text)>? _tricks; // the tour's steps while it's on, otherwise null
    int _trickIndex;

    void OnShowTricks(object sender, RoutedEventArgs e) => ShowTricks();

    void ShowTricks()
    {
        MarkupLayer.FinishTextEditing();
        (FrameworkElement Target, string Title, string Text)[] steps =
        [
            (MainMenu, "Menus",
                "File opens, saves and prints, and New makes a blank sheet. Edit has undo, copy and paste. Tools and CAD tools list every drawing tool with its key; "
                + "Tools also has Compare, to overlay an old revision. Help has this tour and puts Combine PDFs in the Explorer menu."),
            (MainToolBar, "Tool bar",
                "Undo and redo, then the style of new markups: colour, line end and line weight; font and text size; the shape and outline of clouds and callouts, and the fill colour and type of closed shapes. "
                + "With a markup selected, these change it."),
            (TabStrip, "Tabs",
                "Every open drawing has its own tab, with its own undo, page, zoom, scale and compare. Ctrl+Tab goes to the next one, Ctrl+W closes one. "
                + "Copy markups in one tab and paste them in another: Ctrl+Shift+V puts them on the same spot of the sheet."),
            (ToolsBar, "Drawing tools",
                "One at a time, each with a key: V select, S select text, T text, C callout, G tag, K cloud, P pen, H highlight, L line, R rectangle, M measure. "
                + "Shift keeps lines straight; Ctrl does the opposite of Snap. The CAD tools below come on once the sheet has a scale."),
            (PagePanel, "Pages", "Click a page to show it. Page Up and Page Down step through them."),
            (StatusBar, "Status bar",
                "Show or hide the page list, snap to the drawing's line ends and crossings, turn the page, the page (arrows to step, or type a number), "
                + "fit to window (Ctrl+0) and the zoom (type a percentage), and the cursor on the sheet in mm. "
                + "With a CAD tool on, just type a size: it goes into the boxes here, and Enter applies it. On the right, the sheet's scale: pick one, or calibrate."),
            (StartPanel, "Get started", "Open a PDF or drop one here. The page list and the status bar show up then: press F1 again for those."),
        ];
        _tricks = steps.Where(step => step.Target.IsVisible).ToList();
        _trickIndex = 0;
        TricksOverlay.Visibility = Visibility.Visible;
        UpdateLayout(); // the overlay needs its size before the first step is drawn
        ShowTrick();
    }

    void EndTricks()
    {
        _tricks = null;
        TricksOverlay.Children.Clear();
        TricksOverlay.Visibility = Visibility.Collapsed;
        Focus(); // not Keyboard.ClearFocus(): with nothing focused, keys reach no handler
    }

    void NextTrick()
    {
        if (_tricks == null) return;
        if (++_trickIndex >= _tricks.Count) EndTricks();
        else ShowTrick();
    }

    void OnTricksClick(object sender, MouseButtonEventArgs e)
    {
        NextTrick();
        e.Handled = true;
    }

    void OnTricksResized(object sender, SizeChangedEventArgs e)
    {
        if (_tricks != null) ShowTrick();
    }

    /// <summary>While the tour is on it has the keyboard: Esc ends it, Enter, Space or → go on, other keys do nothing.</summary>
    bool HandleTricksKey(Key key)
    {
        if (_tricks == null) return false;
        if (key == Key.Escape) EndTricks();
        else if (key is Key.Enter or Key.Space or Key.Right) NextTrick();
        return true;
    }

    /// <summary>Dims everything but the step's area, rings it, and puts the explanation beside it where it fits.</summary>
    void ShowTrick()
    {
        var (target, title, text) = _tricks![_trickIndex];
        TricksOverlay.Children.Clear();
        var window = new Rect(0, 0, TricksOverlay.ActualWidth, TricksOverlay.ActualHeight);
        var hole = target.TransformToVisual(TricksOverlay).TransformBounds(new Rect(target.RenderSize));
        hole.Inflate(3, 3);
        hole.Intersect(window);
        var accent = TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.DodgerBlue;
        TricksOverlay.Children.Add(new Path
        {
            Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(window), new RectangleGeometry(hole, 4, 4)),
            Fill = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
        });
        var ring = new Rectangle { Width = hole.Width, Height = hole.Height, RadiusX = 4, RadiusY = 4, Stroke = accent, StrokeThickness = 2 };
        Canvas.SetLeft(ring, hole.Left);
        Canvas.SetTop(ring, hole.Top);
        TricksOverlay.Children.Add(ring);

        var card = new Border
        {
            Width = 380, Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(6),
            Background = TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.White,
            BorderBrush = accent, BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) },
                    new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                    new TextBlock
                    {
                        Text = $"{_trickIndex + 1} of {_tricks.Count}   ·   click for the next, Esc to close", FontSize = 11, Margin = new Thickness(0, 10, 0, 0),
                        Foreground = TryFindResource("TextFillColorSecondaryBrush") as Brush ?? Brushes.Gray,
                    },
                },
            },
        };
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = card.DesiredSize;
        const double Gap = 12;
        double alongX = Math.Clamp(hole.Left, Gap, Math.Max(Gap, window.Width - size.Width - Gap));
        double alongY = Math.Clamp(hole.Top, Gap, Math.Max(Gap, window.Height - size.Height - Gap));
        // Below, above, left of or right of the area, whichever fits first; the middle of the window if none does.
        Point[] places =
        [
            new(alongX, hole.Bottom + Gap), new(alongX, hole.Top - Gap - size.Height),
            new(hole.Left - Gap - size.Width, alongY), new(hole.Right + Gap, alongY),
        ];
        var place = places.FirstOrDefault(point => window.Contains(new Rect(point, size)),
            new Point((window.Width - size.Width) / 2, (window.Height - size.Height) / 2));
        Canvas.SetLeft(card, place.X);
        Canvas.SetTop(card, place.Y);
        TricksOverlay.Children.Add(card);
    }
}
