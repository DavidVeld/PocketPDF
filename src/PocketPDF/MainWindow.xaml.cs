using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PocketPDF.Core;

namespace PocketPDF;

public partial class MainWindow : Window
{
    const double DipPerPoint = 96.0 / 72;
    // shortcut: renders the whole page, capped at this many pixels (~160 MB), so A0/A1 go soft past ~250% zoom; upgrade to viewport tiles via PDFiumCore if that matters.
    const double MaxPixels = 40_000_000;

    DocumentTab? _doc; // the document in view, otherwise null (the start screen)
    List<Point>? _alignPoints; // points clicked so far while aligning (two on the old revision, then two on the new), otherwise null
    int _renderVersion;
    Point? _panPreviousPosition;
    readonly DispatcherTimer _rerenderTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly AppSettings _settings = AppSettings.Load();
    // One page render at a time, so zooming in steps doesn't queue full-page renders that are out of date before they start.
    readonly SemaphoreSlim _renderGate = new(1);
    readonly SemaphoreSlim _thumbnailGate = new(1); // one thumbnail at a time, so they never hold up the page render for long
    bool _documentSelected; // Ctrl+A twice: every markup on every page, for Delete and Copy
    List<(PageGeometry Page, int PageIndex, Markup Markup)> _clipboard = []; // copies, with the page they came from

    /// <param name="filesToOpen">Files from the command line, each opened in a tab once the window is up.</param>
    public MainWindow(IReadOnlyList<string> filesToOpen)
    {
        InitializeComponent();
        TextLayout.Fonts = new SystemFontSource();
        FontPicker.ItemsSource = Fonts.SystemFontFamilies.Select(family => family.Source).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        FontPicker.SelectedItem = TextLayout.ScreenFontFamily;
        foreach (var item in ColorPicker.Items.OfType<ComboBoxItem>().Concat(FillPicker.Items.OfType<ComboBoxItem>()).Where(item => item.Tag is string tag && tag.StartsWith('#')))
            item.Content = ColorSwatch((string)item.Tag, (string)item.Content);
        RestoreWindowSettings();
        ShowRecentFiles();
        UpdateTitle(); // with the version
        _rerenderTimer.Tick += (_, _) => { _rerenderTimer.Stop(); RenderPage(); };
        MarkupLayer.MarkupsChanged += () =>
        {
            _doc?.History.Commit(CurrentPageIndex);
            UpdateUndoState();
            HideChangedFileMarkups();
            ShowSheetControls(); // a clip turns Show all on
        };
        MarkupLayer.SelectionChanged += () =>
        {
            if (_documentSelected) NoticeStatus.Text = "";
            _documentSelected = false;
            ShowSelectedMarkupStyle();
            UpdateFontPicker();
            ShowSheetControls();
        };
        MarkupLayer.MeasureReadout += text => MeasureStatus.Text = text;
        MarkupLayer.CadSizeChanged += ShowCadSize;
        // A click on the sheet leaves the size boxes; a length typed there is used by that click (see TypedFirstMm).
        // Focus the window rather than Keyboard.ClearFocus(): with nothing focused, keys (Delete, tool letters) reach no handler.
        MarkupLayer.PreviewMouseDown += (_, _) =>
        {
            if (!CadInputBar.IsKeyboardFocusWithin) return;
            Focus();
            _offsetBeforeTyping = null; // a click uses the distance typed: it stays
        };
        Loaded += (_, _) =>
        {
            foreach (var filePath in filesToOpen) OpenFile(filePath);
        };
    }

    int CurrentPageIndex => PageList.SelectedIndex;

    bool IsViewOnly => _doc?.IsViewOnly == true;

    /// <summary>The open documents, in tab order.</summary>
    IEnumerable<DocumentTab> Documents => TabStrip.Items.Cast<TabItem>().Select(tab => (DocumentTab)tab.Tag);

    /// <summary>The app's version, 0.x.y: the csproj makes it from the number of commits (see SetVersionFromGit there).</summary>
    static readonly string AppVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";

    /// <summary>The window's title and the document's tab, after it was changed, saved or renamed.</summary>
    void UpdateTitle()
    {
        Title = _doc == null ? $"PocketPDF {AppVersion}" : $"{_doc.Title}{(IsViewOnly ? " (view only)" : "")} – PocketPDF {AppVersion}";
        if (_doc == null) return;
        ((TextBlock)((StackPanel)_doc.Tab.Header).Children[0]).Text = _doc.Title;
        _doc.Tab.ToolTip = _doc.FilePath;
    }

    void OnAbout(object sender, RoutedEventArgs e)
    {
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, Padding = new Thickness(18, 4, 18, 4), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var dialog = new Window
        {
            Title = "About PocketPDF", Owner = this, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20), Width = 320,
                Children =
                {
                    new Image { Source = new BitmapImage(new Uri("pack://application:,,,/icon.png")), Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) },
                    new TextBlock { Text = "PocketPDF", FontSize = 20, FontWeight = FontWeights.SemiBold },
                    new TextBlock { Text = $"Version {AppVersion}", Margin = new Thickness(0, 2, 0, 10) },
                    new TextBlock { Text = "A PDF viewer for engineering drawings: markups, measuring to scale, CAD tools, revision compare and combining PDFs.", TextWrapping = TextWrapping.Wrap },
                    ok,
                },
            },
        };
        dialog.ShowDialog();
    }

    // ---- Open and save ------------------------------------------------------------------------

    /// <summary>Opens the file in a new tab; a file already open goes to its tab instead (with its unsaved markups).</summary>
    void OpenFile(string filePath)
    {
        string fullPath = Path.GetFullPath(filePath);
        if (Documents.FirstOrDefault(doc => !doc.IsOnlyInMemory && string.Equals(Path.GetFullPath(doc.FilePath), fullPath, StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            TabStrip.SelectedItem = open.Tab;
            return;
        }
        LoadedPdf loaded;
        PdfRenderer renderer;
        DateTime fileTime;
        try
        {
            fileTime = File.GetLastWriteTimeUtc(filePath); // before reading: a change while reading then counts as a change
            loaded = MarkupFile.Load(filePath);
            renderer = new PdfRenderer(loaded.OriginalBytes);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open {Path.GetFileName(filePath)}.\n\n{ex.Message}", "PocketPDF",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        AddDocument(new DocumentTab(loaded, renderer, filePath, isOnlyInMemory: false) { FileTime = fileTime });
        _settings.AddRecentFile(fullPath);
        ShowRecentFiles();
    }

    /// <summary>A file another PocketPDF process was started for (Explorer opens each selected file in its own): a tab here.</summary>
    public void OpenHandedOverFile(string filePath)
    {
        OpenFile(filePath);
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    // ---- Tabs ---------------------------------------------------------------------------------

    /// <summary>Gives the document a tab, with a close button, and shows it.</summary>
    void AddDocument(DocumentTab doc)
    {
        var close = new Button
        {
            Content = new TextBlock { Style = (Style)FindResource("ToolIcon"), Text = "\uE711", FontSize = 10 },
            Padding = new Thickness(4), Margin = new Thickness(8, 0, -6, 0), MinWidth = 0, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "Close (Ctrl+W)",
        };
        close.Click += (_, _) => CloseDocument(doc);
        doc.Tab = new TabItem { Tag = doc, Header = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { VerticalAlignment = VerticalAlignment.Center }, close } } };
        TabStrip.Items.Add(doc.Tab);
        TabStrip.SelectedItem = doc.Tab; // OnTabSelected shows it
    }

    void OnTabSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == TabStrip) ShowDocument((TabStrip.SelectedItem as TabItem)?.Tag as DocumentTab);
    }

    void OnCloseDocument(object sender, RoutedEventArgs e)
    {
        if (_doc != null) CloseDocument(_doc);
    }

    /// <summary>Closes the document's tab, after asking to save unsaved work. False if the user cancelled.</summary>
    bool CloseDocument(DocumentTab doc)
    {
        TabStrip.SelectedItem = doc.Tab; // the question is about the document in view
        if (!ConfirmDiscardChanges()) return false;
        int index = TabStrip.Items.IndexOf(doc.Tab);
        TabStrip.Items.Remove(doc.Tab);
        if (TabStrip.Items.Count == 0) ShowDocument(null);
        else TabStrip.SelectedIndex = Math.Min(index, TabStrip.Items.Count - 1);
        return true;
    }

    /// <summary>
    /// Shows a document in the window, or the start screen for null. The window has one page view for all tabs: the
    /// document shown before keeps its page, zoom, scroll position and compare view, for when its tab comes back.
    /// </summary>
    void ShowDocument(DocumentTab? doc)
    {
        if (doc == _doc) return;
        if (_doc != null)
        {
            MarkupLayer.FinishTextEditing(); // onto the document it was typed on
            EndAlign();
            (_doc.PageIndex, _doc.Zoom, _doc.ScrollX, _doc.ScrollY, _doc.CompareView) =
                (CurrentPageIndex, Zoom.ScaleX, Scroller.HorizontalOffset, Scroller.VerticalOffset, CompareView.SelectedIndex);
        }
        _doc = doc;
        _documentSelected = false;
        PageList.ItemsSource = doc?.Thumbnails; // first: the current page index is this list's
        PageImage.Source = null;
        MeasureStatus.Text = NoticeStatus.Text = "";
        StartPanel.Visibility = doc == null ? Visibility.Visible : Visibility.Collapsed;
        StatusBar.Visibility = TabStrip.Visibility = doc == null ? Visibility.Collapsed : Visibility.Visible;
        SaveMenuItem.IsEnabled = SaveAsMenuItem.IsEnabled = doc != null && !IsViewOnly;
        PrintMenuItem.IsEnabled = CloseMenuItem.IsEnabled = doc != null;
        MarkupLayer.ToolTip = doc?.Loaded.ViewOnlyReason is { } reason ? $"Markups are off for this file: it couldn't be read for editing ({reason})" : null;
        CompareControls.Visibility = doc?.ComparePdf != null ? Visibility.Visible : Visibility.Collapsed;
        CompareLegend.Text = doc?.CompareLegendText ?? "";
        UpdateUndoState();
        UpdateEditingState();
        UpdateMarkupLayerVisibility();
        UpdatePagePanel();
        if (doc == null) return;
        CompareView.SelectedIndex = doc.CompareView;
        PageList.SelectedIndex = doc.PageIndex; // shows the page
        if (doc.Zoom is not { } zoom)
        {
            FitPage();
            return;
        }
        SetZoom(zoom);
        Scroller.UpdateLayout();
        Scroller.ScrollToHorizontalOffset(doc.ScrollX);
        Scroller.ScrollToVerticalOffset(doc.ScrollY);
    }

    /// <summary>Ctrl+Tab and Ctrl+Shift+Tab: the next or previous tab, round and round.</summary>
    void StepTab(int step)
    {
        if (TabStrip.Items.Count > 1)
            TabStrip.SelectedIndex = (TabStrip.SelectedIndex + step + TabStrip.Items.Count) % TabStrip.Items.Count;
    }

    void OnOpen(object sender, RoutedEventArgs e) => ShowOpenDialog();

    void OnNew(object sender, RoutedEventArgs e) => ShowNewDialog();

    /// <summary>Asks for a sheet size and opens a blank one-page document; it's only in memory until saved.</summary>
    void ShowNewDialog()
    {
        var custom = new PaperSize("Custom", 420, 297);
        var sizes = new ComboBox { ItemsSource = PaperSize.Presets.Append(custom).ToList(), SelectedIndex = 1, Width = 110 };
        var width = new TextBox { Width = 70, Margin = new Thickness(12, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Right };
        var height = new TextBox { Width = 70, HorizontalContentAlignment = HorizontalAlignment.Right };
        var portrait = new RadioButton { Content = "Portrait", Margin = new Thickness(0, 12, 16, 0) };
        var landscape = new RadioButton { Content = "Landscape", IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };
        bool IsLandscape() => landscape.IsChecked == true;
        bool IsCustom() => ReferenceEquals(sizes.SelectedItem, custom);
        bool TryReadSize(out double widthMm, out double heightMm) =>
            double.TryParse(width.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out widthMm) & double.TryParse(height.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out heightMm);
        // A preset shows its size, turned to the orientation; a custom size is typed, and turned too when the orientation changes.
        void ShowSize()
        {
            var size = IsCustom() && TryReadSize(out double w, out double h) ? custom with { WidthMm = w, HeightMm = h } : (PaperSize)sizes.SelectedItem;
            var oriented = size.Oriented(IsLandscape());
            (width.Text, height.Text) = (FormatMm(oriented.WidthMm), FormatMm(oriented.HeightMm));
            width.IsEnabled = height.IsEnabled = IsCustom();
        }
        sizes.SelectionChanged += (_, _) => ShowSize();
        portrait.Checked += (_, _) => ShowSize();
        landscape.Checked += (_, _) => ShowSize();
        ShowSize();
        var ok = new Button { Content = "Create", IsDefault = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0), Style = TryFindResource("AccentButtonStyle") as Style };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 4, 12, 4) };
        TextBlock Label(string text) => new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        var dialog = new Window
        {
            Title = "New sheet", Owner = this, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = "A blank sheet to draw on:", Margin = new Thickness(0, 0, 0, 8) },
                    new StackPanel { Orientation = Orientation.Horizontal, Children = { sizes, width, Label("×"), height, Label("mm") } },
                    new StackPanel { Orientation = Orientation.Horizontal, Children = { portrait, landscape } },
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } },
                },
            },
        };
        ok.Click += (_, _) =>
        {
            if (TryReadSize(out double w, out double h) && BlankPdf.IsValidSide(w) && BlankPdf.IsValidSide(h)) dialog.DialogResult = true;
            else MessageBox.Show(dialog, $"Type the width and height in mm, each from {PaperSize.MinSideMm} to {PaperSize.MaxSideMm}.", "New sheet");
        };
        if (dialog.ShowDialog() != true || !TryReadSize(out double widthMm, out double heightMm)) return;
        var bytes = BlankPdf.Create(widthMm, heightMm);
        string name = IsCustom() ? "sheet" : ((PaperSize)sizes.SelectedItem).Name;
        string folder = _settings.LastFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        AddDocument(new DocumentTab(MarkupFile.Load(bytes), new PdfRenderer(bytes), Path.Combine(folder, $"New {name}.pdf"), isOnlyInMemory: true));
    }

    void ShowOpenDialog()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "PDF files (*.pdf)|*.pdf",
            InitialDirectory = _doc != null ? Path.GetDirectoryName(_doc.FilePath) : _settings.LastFolder,
        };
        if (dialog.ShowDialog(this) == true) OpenFile(dialog.FileName);
    }

    void OnRecentFile(object sender, RoutedEventArgs e)
    {
        var filePath = (string)((FrameworkElement)sender).Tag;
        if (File.Exists(filePath))
        {
            OpenFile(filePath);
            return;
        }
        MessageBox.Show(this, $"{Path.GetFileName(filePath)} isn't there any more. It was moved, renamed or deleted.\n\n{filePath}", "PocketPDF");
        _settings.RecentFiles.Remove(filePath);
        ShowRecentFiles();
    }

    void ShowRecentFiles()
    {
        RecentList.ItemsSource = _settings.RecentFiles.ToList();
        RecentHeading.Visibility = _settings.RecentFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Window settings ----------------------------------------------------------------------

    void RestoreWindowSettings()
    {
        PagesToggle.IsChecked = _settings.PagesPanelOpen;
        SnapToggle.IsChecked = MarkupLayer.SnapEnabled = _settings.Snap;
        LineWeightsMenuItem.IsChecked = _settings.LineWeights;
        DarkModeMenuItem.IsChecked = _settings.DarkMode ?? App.WindowsUsesDarkMode();
        SelectByTag(ShapePicker, tag => tag == _settings.AreaShape);
        SelectByTag(LineStylePicker, tag => tag == _settings.AreaOutline);
        SelectByTag(HatchPicker, tag => tag == _settings.HatchPattern);
        UpdatePagePanel();
        if (_settings is { Left: { } left, Top: { } top, Width: { } width, Height: { } height } && width > 200 && height > 150)
        {
            // Only if it's still on a screen: a monitor may have been unplugged since.
            var screens = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (screens.IntersectsWith(new Rect(left, top, width, height)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                (Left, Top, Width, Height) = (left, top, width, height);
            }
        }
        if (_settings.Maximized) WindowState = WindowState.Maximized;
    }

    protected override void OnClosed(EventArgs e)
    {
        _settings.Maximized = WindowState == WindowState.Maximized;
        _settings.PagesPanelOpen = PagesToggle.IsChecked == true;
        _settings.Snap = SnapToggle.IsChecked == true;
        _settings.LineWeights = LineWeightsMenuItem.IsChecked;
        _settings.AreaShape = (string)((FrameworkElement)ShapePicker.SelectedItem).Tag;
        _settings.AreaOutline = (string)((FrameworkElement)LineStylePicker.SelectedItem).Tag;
        _settings.HatchPattern = (string)((FrameworkElement)HatchPicker.SelectedItem).Tag;
        _settings.Save();
        base.OnClosed(e);
    }

    void OnTogglePages(object sender, RoutedEventArgs e) => UpdatePagePanel();

    void OnDarkModeToggled(object sender, RoutedEventArgs e)
    {
        _settings.DarkMode = DarkModeMenuItem.IsChecked; // from now on the switch decides, not Windows
        App.SetDarkMode(DarkModeMenuItem.IsChecked);
    }

    void OnSnapToggled(object sender, RoutedEventArgs e) => MarkupLayer.SnapEnabled = SnapToggle.IsChecked == true;

    void OnHelpMenuOpened(object sender, RoutedEventArgs e) =>
        ExplorerMenuItem.Header = ExplorerMenu.InstalledCommand() != null
            ? "Remove “Combine PDFs” from the Explorer menu" : "Add “Combine PDFs” to the Explorer menu";

    void OnExplorerMenuItem(object sender, RoutedEventArgs e) => ToggleExplorerMenu(ExplorerMenu.InstalledCommand() != null);

    void ToggleExplorerMenu(bool isInstalled)
    {
        try
        {
            if (isInstalled)
            {
                ExplorerMenu.Uninstall();
                MessageBox.Show(this, "“Combine PDFs” is no longer in the Explorer menu.", "PocketPDF");
                return;
            }
            ExplorerMenu.Install(Environment.ProcessPath!);
            MessageBox.Show(this, "Select PDFs in Explorer, right-click, and choose “Combine PDFs”. "
                + "On Windows 11 it's under “Show more options” (or Shift+right-click).\n\n"
                + $"It starts this copy of PocketPDF:\n{Environment.ProcessPath}\nIf you move PocketPDF, add it again from here.", "PocketPDF");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            MessageBox.Show(this, $"Couldn't change the Explorer menu.\n\n{ex.Message}", "PocketPDF");
        }
    }

    void UpdatePagePanel() =>
        PagePanel.Visibility = PagesToggle.IsChecked == true && _doc != null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Renders a page's thumbnail when its list item first comes into view.</summary>
    async void OnThumbnailShown(object sender, RoutedEventArgs e)
    {
        // The thumbnail is its document's, as is the renderer: one finished after a switch of tabs is there when its tab comes back.
        if (((FrameworkElement)sender).DataContext is not PageThumbnail { IsRequested: false } thumbnail || _doc?.Pdf is not { } pdf) return;
        await RenderThumbnail(thumbnail, pdf);
    }

    async Task RenderThumbnail(PageThumbnail thumbnail, PdfRenderer pdf)
    {
        thumbnail.IsRequested = true;
        await _thumbnailGate.WaitAsync();
        try
        {
            int pageIndex = thumbnail.Number - 1;
            var (width, height) = pdf.PageSize(pageIndex);
            double scale = 208 / Math.Max(width, height); // 2 pixels per unit of the 104-wide box: sharp on high-DPI screens
            var (pixels, pixelWidth, pixelHeight) = await Task.Run(() => pdf.Render(pageIndex, scale));
            var image = BitmapSource.Create(pixelWidth, pixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, pixelWidth * 4);
            image.Freeze();
            thumbnail.Image = image;
        }
        catch (Exception)
        {
            // A missing thumbnail is harmless; the page itself reports render errors when it's opened.
        }
        finally
        {
            _thumbnailGate.Release();
        }
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] droppedFiles)
            foreach (var droppedFile in droppedFiles) OpenFile(droppedFile); // each in its own tab
    }

    void OnSave(object sender, RoutedEventArgs e) => Save();

    void OnSaveAs(object sender, RoutedEventArgs e) => SaveAs();

    void OnExit(object sender, RoutedEventArgs e) => Close(); // OnClosing asks about unsaved work

    /// <summary>Saves into the open file; a document not saved anywhere yet (a new sheet, an accepted comparison) asks where.</summary>
    bool Save()
    {
        if (_doc == null || IsViewOnly) return false;
        return _doc.IsOnlyInMemory ? SaveAs() : WriteFile(_doc.FilePath);
    }

    /// <summary>Asks where to save; replacing an existing file (such as the original) needs the dialog's confirmation.</summary>
    bool SaveAs()
    {
        if (_doc == null || IsViewOnly) return false;
        MarkupLayer.FinishTextEditing();
        var dialog = new SaveFileDialog
        {
            Filter = "PDF files (*.pdf)|*.pdf",
            InitialDirectory = Path.GetDirectoryName(_doc.FilePath),
            FileName = Path.GetFileName(_doc.FilePath),
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(this) == true && WriteFile(dialog.FileName);
    }

    bool WriteFile(string filePath)
    {
        if (_doc == null) return false;
        MarkupLayer.FinishTextEditing();
        // Saving rebuilds the file from the copy read at opening, so anything another viewer saved into it since would be lost.
        if (_doc.FileTime is { } readTime && string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(_doc.FilePath), StringComparison.OrdinalIgnoreCase)
            && File.Exists(filePath) && File.GetLastWriteTimeUtc(filePath) != readTime
            && MessageBox.Show(this, $"{Path.GetFileName(filePath)} was changed by someone else since you opened it. Saving over it loses their changes.\n\n"
                + "Save over it anyway? Choose No to keep their changes, then use Save As to save yours under another name.", "PocketPDF",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
        // Written beside it first, then swapped in: a failed save never leaves a half-written file in place of the drawing.
        string tempPath = filePath + ".saving";
        try
        {
            MarkupFile.Save(_doc.Loaded, _doc.Loaded.MarkupsByPage, tempPath);
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            try { File.Delete(tempPath); } catch (Exception) { } // best effort: the message below is what matters
            MessageBox.Show(this, $"Could not save {Path.GetFileName(filePath)}.\n\n{ex.Message}", "PocketPDF",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _doc.FilePath = filePath;
        _doc.FileTime = File.GetLastWriteTimeUtc(filePath);
        _doc.History.MarkSaved();
        _doc.IsOnlyInMemory = false;
        _doc.ScaleChanged = false;
        _doc.PageTurned = false;
        UpdateUndoState();
        return true;
    }

    // ---- Undo and redo ------------------------------------------------------------------------

    void OnUndo(object sender, RoutedEventArgs e) => UndoOrRedo(redo: false);

    void OnRedo(object sender, RoutedEventArgs e) => UndoOrRedo(redo: true);

    void UndoOrRedo(bool redo)
    {
        if (_doc?.Loaded is not { } loaded) return;
        MarkupLayer.FinishTextEditing(); // text being typed becomes its own step first
        if (!(redo ? _doc.History.CanRedo : _doc.History.CanUndo)) return;
        int pageIndex = redo ? _doc.History.Redo() : _doc.History.Undo();
        if (pageIndex != CurrentPageIndex) PageList.SelectedIndex = pageIndex; // shows the page and its markups
        else
        {
            MarkupLayer.ShowPage(loaded.Pages[pageIndex], loaded.MarkupsByPage[pageIndex]);
            LoadToolData(); // ShowPage drops the page text and line work
        }
        UpdateUndoState();
        HideChangedFileMarkups(); // an undone change to a file markup shows the file's own appearance again
    }

    /// <summary>Unsaved-changes mark and undo buttons, after any change to the markups or the history.</summary>
    void UpdateUndoState()
    {
        UpdateTitle();
        UndoButton.IsEnabled = _doc?.History.CanUndo == true;
        RedoButton.IsEnabled = _doc?.History.CanRedo == true;
    }

    /// <summary>Asks to save the unsaved work in the document in view. Returns false if the user cancelled.</summary>
    bool ConfirmDiscardChanges()
    {
        MarkupLayer.FinishTextEditing();
        if (_doc is not { HasUnsavedWork: true } doc) return true;
        string fileName = Path.GetFileName(doc.FilePath);
        string question = doc.IsOnlyInMemory ? $"{fileName} hasn't been saved yet. Save it first?" : $"Save your markups to {fileName} first?";
        var answer = MessageBox.Show(this, question, "PocketPDF",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    /// <summary>Asks about each document with unsaved work, showing its tab; a cancel keeps the window open.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        MarkupLayer.FinishTextEditing(); // text being typed is unsaved work too
        foreach (var doc in Documents.Where(doc => doc.HasUnsavedWork).ToList())
        {
            TabStrip.SelectedItem = doc.Tab;
            if (ConfirmDiscardChanges()) continue;
            e.Cancel = true;
            break;
        }
        base.OnClosing(e);
        if (e.Cancel) return;
        // Read here, not in OnClosed: once the window is gone RestoreBounds is Rect.Empty, whose infinities JSON can't hold.
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (double.IsFinite(bounds.Left + bounds.Top + bounds.Width + bounds.Height)) // otherwise keep the last good bounds
            (_settings.Left, _settings.Top, _settings.Width, _settings.Height) = (bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    // ---- Pages --------------------------------------------------------------------------------

    void OnPageSelected(object sender, SelectionChangedEventArgs e) => ShowCurrentPage();

    void OnRotateClockwise(object sender, RoutedEventArgs e) => RotatePage(90);

    void OnRotateCounterClockwise(object sender, RoutedEventArgs e) => RotatePage(-90);

    void OnRotate180(object sender, RoutedEventArgs e) => RotatePage(180);

    /// <summary>Turns the page in view, the drawing and its markups together. Saving keeps it turned.</summary>
    void RotatePage(int degrees)
    {
        if (!CanEditMarkups || _doc is not { } doc) return;
        MarkupLayer.FinishTextEditing();
        int pageIndex = CurrentPageIndex;
        doc.Loaded.RotatePage(pageIndex, degrees);
        doc.PageTurned = true;
        MarkupLayer.ShowPage(doc.Loaded.Pages[pageIndex], doc.Loaded.MarkupsByPage[pageIndex]);
        LoadToolData(); // ShowPage drops the page text and line work
        UpdateUndoState(); // the * for unsaved work
        HideChangedFileMarkups(turnedPage: pageIndex); // the page image, drawn turned
    }

    void ShowCurrentPage()
    {
        if (_doc == null || CurrentPageIndex < 0) return;
        var page = _doc.Loaded.Pages[CurrentPageIndex];
        _documentSelected = false; // Ctrl+A twice was for the page left behind: Delete mustn't still empty the document
        ShowPageNumber();
        PageList.ScrollIntoView(PageList.SelectedItem);
        PageImage.Source = null;
        MarkupLayer.ShowPage(page, _doc.Loaded.MarkupsByPage[CurrentPageIndex]);
        RenderPage();
        ShowScale();
        MeasureStatus.Text = "";
        LoadToolData();
    }

    // ---- Page buttons (status bar) -------------------------------------------------------------

    /// <summary>Shows the page, or the first or last one for a number past either end.</summary>
    void GoToPage(int pageIndex)
    {
        if (_doc != null) PageList.SelectedIndex = Math.Clamp(pageIndex, 0, _doc.Pdf.PageCount - 1); // shows it
    }

    void OnFirstPage(object sender, RoutedEventArgs e) => GoToPage(0);

    void OnPreviousPage(object sender, RoutedEventArgs e) => GoToPage(CurrentPageIndex - 1);

    void OnNextPage(object sender, RoutedEventArgs e) => GoToPage(CurrentPageIndex + 1);

    void OnLastPage(object sender, RoutedEventArgs e) => GoToPage(int.MaxValue);

    /// <summary>"3 of 40" in the page box, and the arrows on only where they lead somewhere.</summary>
    void ShowPageNumber()
    {
        int pageCount = _doc?.Pdf.PageCount ?? 0;
        if (!PageBox.IsKeyboardFocused) PageBox.Text = $"{CurrentPageIndex + 1} of {pageCount}";
        FirstPageButton.IsEnabled = PreviousPageButton.IsEnabled = CurrentPageIndex > 0;
        NextPageButton.IsEnabled = LastPageButton.IsEnabled = CurrentPageIndex < pageCount - 1;
    }

    /// <summary>The first click into a number box (the page, the CAD sizes) selects its text, so a number typed replaces it.</summary>
    void OnSelectAllMouseDown(object sender, MouseButtonEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.IsKeyboardFocused)
        {
            box.Tag = null; // a click in the box places the caret to edit the value, see OnCadInputPreviewTextInput
            return;
        }
        box.Focus();
        e.Handled = true;
    }

    void OnSelectAllGotFocus(object sender, KeyboardFocusChangedEventArgs e) => ((TextBox)sender).SelectAll();

    void OnPageBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        if (e.Key == Key.Escape) PageBox.Text = ""; // LostKeyboardFocus then stays on the page
        Focus(); // the window, not Keyboard.ClearFocus(): with nothing focused, keys reach no handler
        e.Handled = true;
    }

    /// <summary>Goes to the number typed (the first number in the box, so "3 of 40" left as it is stays on page 3).</summary>
    void OnPageBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (int.TryParse(PageBox.Text.Trim().Split(' ')[0], NumberStyles.None, CultureInfo.CurrentCulture, out int pageNumber)) GoToPage(pageNumber - 1);
        ShowPageNumber();
    }

    /// <summary>Reads what the current tool needs from the page (its text, or its line work for snapping), in the background.</summary>
    void LoadToolData()
    {
        LoadPageText();
        LoadLineWork();
        FindScaleForMeasure();
    }

    /// <summary>Measuring on a sheet with no scale set takes the scale written on it: the "1:###" furthest bottom right.</summary>
    async void FindScaleForMeasure()
    {
        if (MarkupLayer.Tool != MarkupTool.Measure || _doc is not { ComparePdf: null } doc || CurrentPageIndex < 0
            || doc.Loaded.MmPerPointByPage.ContainsKey(CurrentPageIndex)) return;
        var pdf = doc.Pdf;
        int pageIndex = CurrentPageIndex;
        int? ratio;
        try
        {
            ratio = await Task.Run(() => DrawingScale.FindScaleOnSheet(pdf.GetText(pageIndex)));
        }
        catch (Exception ex)
        {
            NoticeStatus.Text = $"Couldn't read the text on this page: {ex.Message}";
            return;
        }
        if (doc != _doc || pageIndex != CurrentPageIndex || MarkupLayer.Tool != MarkupTool.Measure || doc.Loaded.MmPerPointByPage.ContainsKey(pageIndex)) return; // the user moved on meanwhile
        if (ratio is not { } found)
        {
            NoticeStatus.Text = "No scale ref found on the sheet: measuring at 1:1. Set the scale in the status bar, or calibrate";
            return;
        }
        SetScale(DrawingScale.FromRatio(found));
        NoticeStatus.Text = $"Scale 1:{found} found on the sheet, so measuring at 1:{found}. Change it in the status bar if it's wrong";
    }

    /// <summary>Reads the page's line work for snapping. Only while a tool that snaps is on: big CAD sheets take a moment.</summary>
    async void LoadLineWork()
    {
        if (!MarkupLayer.UsesSnapping || _doc?.Pdf is not { } pdf || _doc.Loaded is not { } loaded || CurrentPageIndex < 0 || MarkupLayer.Snaps != null) return;
        int pageIndex = CurrentPageIndex;
        var page = loaded.Pages[pageIndex];
        SnapIndex snaps;
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            snaps = await Task.Run(() => new SnapIndex(pdf.GetLineWork(pageIndex).Select(segment => (page.ToView(segment.A), page.ToView(segment.B)))));
        }
        catch (Exception ex)
        {
            NoticeStatus.Text = $"Couldn't read the drawing's lines on this page: {ex.Message}";
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        if (pageIndex != CurrentPageIndex || !ReferenceEquals(loaded, _doc?.Loaded) || !MarkupLayer.UsesSnapping) return; // the user moved on meanwhile
        MarkupLayer.Snaps = snaps;
        if (snaps.Count == 0) NoticeStatus.Text = "No line work to snap to on this page (a scan?)";
        else if (snaps.Count >= VectorExtractor.MaxSegments) NoticeStatus.Text = "Very dense page: snapping covers only part of it";
    }

    // ---- Scale --------------------------------------------------------------------------------

    double CurrentMmPerPoint => _doc?.Loaded.MmPerPointByPage.GetValueOrDefault(CurrentPageIndex, DrawingScale.PaperMmPerPoint) ?? DrawingScale.PaperMmPerPoint;

    void ShowScale()
    {
        MarkupLayer.MmPerPoint = CurrentMmPerPoint;
        bool isSet = _doc?.Loaded.MmPerPointByPage.ContainsKey(CurrentPageIndex) == true;
        ScaleButton.Content = isSet ? $"Scale {DrawingScale.Describe(CurrentMmPerPoint)}" : "Scale not set (1:1)";
        UpdateCadTools();
    }

    // ---- CAD tools (experimental) -------------------------------------------------------------

    /// <summary>Shows the size boxes in the status bar that fit the CAD tool: a length, width × length, a circle's size, or the offset.</summary>
    void ShowCadInput(MarkupTool tool)
    {
        bool line = tool is MarkupTool.CadLine or MarkupTool.CadPolyline, rectangle = tool == MarkupTool.CadRectangle;
        bool circle = tool == MarkupTool.CadCircle, offset = tool == MarkupTool.CadOffset;
        if (CadInputBar.IsKeyboardFocusWithin) Focus();
        CadInputBar.Visibility = line || rectangle || circle || offset ? Visibility.Visible : Visibility.Collapsed;
        CadCircleSizeKind.Visibility = circle ? Visibility.Visible : Visibility.Collapsed;
        CadInputLabel.Visibility = circle ? Visibility.Collapsed : Visibility.Visible;
        CadInputLabel.Text = line ? "Length" : rectangle ? "Width" : "Offset";
        CadInputSecondLabel.Visibility = CadInputSecond.Visibility = rectangle ? Visibility.Visible : Visibility.Collapsed;
        CadInputFirst.Text = offset ? FormatMm(MarkupLayer.OffsetDistanceMm) : "";
        CadInputSecond.Text = "";
        CadInputFirst.Tag = CadInputSecond.Tag = true; // shown, not typed: the first key typed replaces it
        _offsetBeforeTyping = null;
        string typing = "or type a size in the status bar and press Enter (it goes towards the cursor)";
        MeasureStatus.Text = tool switch
        {
            MarkupTool.CadLine => "Line: click the start, then the end. Or type a length, then click or Enter: it goes towards the cursor. Shift: 0, 30, 45, 60, 90°…",
            MarkupTool.CadPolyline => "Polyline: click its points, or type a length, then click or Enter. Shift: 0, 30, 45, 60, 90°…. Double-click or Enter ends it",
            MarkupTool.CadRectangle => "Rectangle: click a corner, then the opposite one. Or type the width, Enter or Tab, the height, then click or Enter: it goes towards the cursor. Shift: whole mm (to 1:20) or tens",
            MarkupTool.CadCircle => $"Circle: click the centre, then a point on it, {typing}",
            MarkupTool.CadOffset => OffsetHint(),
            MarkupTool.CadTrim => "Trim: click the piece to cut away, or drag a window across lines to trim them all where its edge crosses. Shift: extend instead, to the next line or the page edge",
            MarkupTool.CadHatch => "Hatch: click inside a closed region",
            MarkupTool.CadDimension => "Dimension: click two points, then where the dimension line goes. Double-click one with the Select tool to type a new length: the shape it ends on changes to fit",
            _ => "",
        };
    }

    string OffsetHint() =>
        $"Offset {DrawingScale.FormatLength(MarkupLayer.OffsetDistanceMm)}: click a CAD or drawing line, or a circle, then the side. Type a distance to change it";

    /// <summary>The offset distance before the user started typing a new one, for Esc to go back to. Null when not typing.</summary>
    double? _offsetBeforeTyping;

    static string FormatMm(double mm) => mm.ToString("0.#", CultureInfo.CurrentCulture);

    /// <summary>The size so far follows the cursor, unless the user is typing one.</summary>
    void ShowCadSize(double firstMm, double secondMm)
    {
        if (CadInputBar.IsKeyboardFocusWithin || MarkupLayer.Tool == MarkupTool.CadOffset) return;
        bool diameter = MarkupLayer.Tool == MarkupTool.CadCircle && CadCircleSizeKind.SelectedIndex == 0;
        CadInputFirst.Text = FormatMm(diameter ? 2 * firstMm : firstMm);
        CadInputSecond.Text = FormatMm(secondMm);
        CadInputFirst.Tag = CadInputSecond.Tag = true;
    }

    void OnCadCircleSizeKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CadInputFirst != null) CadInputFirst.Text = ""; // the next move shows it the new way
    }

    /// <summary>A number typed while a CAD tool is on goes straight into the size box.</summary>
    void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (CadInputBar.Visibility != Visibility.Visible || Keyboard.FocusedElement is TextBox) return;
        if (e.Text is not [var typed] || !(char.IsDigit(typed) || typed is '.' or ',')) return;
        CadInputFirst.Focus();
        CadInputFirst.Text = e.Text;
        CadInputFirst.Tag = null;
        CadInputFirst.CaretIndex = 1;
        e.Handled = true;
    }

    /// <summary>
    /// A size the app put in a box (the offset distance, the size under the cursor) is replaced by the first key typed,
    /// however the box got focus. Once the user has typed, or placed the caret, keys edit the value as usual.
    /// </summary>
    void OnCadInputPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.Tag is true) box.SelectAll();
        box.Tag = null;
    }

    void OnCadInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.Back or Key.Delete) ((TextBox)sender).Tag = null;
    }

    /// <summary>
    /// A CAD line's length, a rectangle's width or height, or the offset distance, as typed, shows in the preview at once;
    /// the next click (or Enter) uses it. Only typing counts: the boxes also show the size under the cursor.
    /// </summary>
    void OnCadInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { IsKeyboardFocused: true } box) return;
        if (MarkupLayer.Tool == MarkupTool.CadOffset)
        {
            if (DrawingScale.ParseLengthMm(box.Text) is not (> 0 and var distance)) return; // half typed: keeps the last distance
            _offsetBeforeTyping ??= MarkupLayer.OffsetDistanceMm;
            MarkupLayer.OffsetDistanceMm = distance;
            MeasureStatus.Text = OffsetHint(); // the preview copy follows on the next mouse move
            return;
        }
        if (MarkupLayer.Tool is not (MarkupTool.CadLine or MarkupTool.CadPolyline or MarkupTool.CadRectangle)) return;
        var typed = DrawingScale.ParseLengthMm(box.Text);
        if (box == CadInputFirst) MarkupLayer.TypedFirstMm = typed;
        else MarkupLayer.TypedSecondMm = typed;
    }

    /// <summary>
    /// Shift (angle steps) or Ctrl (ortho) let go: the preview follows the cursor freely again. After Shift, typing replaces
    /// the length instead of adding to it.
    /// </summary>
    void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl)) return;
        MarkupLayer.RefreshCursor();
        if (e.Key is Key.LeftShift or Key.RightShift && CadInputBar.IsKeyboardFocusWithin && Keyboard.FocusedElement is TextBox box) box.SelectAll();
    }

    /// <summary>Enter applies the typed size (in a rectangle's width, Enter or Tab moves on to an empty height first); Esc drops it.</summary>
    void OnCadInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (MarkupLayer.Tool == MarkupTool.CadOffset)
            {
                if (_offsetBeforeTyping is { } before) MarkupLayer.OffsetDistanceMm = before;
                ShowCadInput(MarkupTool.CadOffset); // the distance as it was
            }
            (MarkupLayer.TypedFirstMm, MarkupLayer.TypedSecondMm) = (null, null);
            Focus();
            e.Handled = true;
            return;
        }
        var tool = MarkupLayer.Tool;
        bool toHeight = sender == CadInputFirst && tool == MarkupTool.CadRectangle;
        if (e.Key != Key.Enter && !(e.Key == Key.Tab && toHeight)) return;
        e.Handled = true;
        if (toHeight) // Enter or Tab
        {
            CadInputSecond.Focus();
            CadInputSecond.Text = ""; // the height follows the cursor until one is typed
            return;
        }
        if (tool == MarkupTool.CadRectangle) // the width and height typed so far are already with the markup layer
        {
            Focus();
            if (!MarkupLayer.ApplyTypedSize(0)) MeasureStatus.Text = "Click the first corner, then type the size";
            return;
        }
        double? first = DrawingScale.ParseLengthMm(CadInputFirst.Text);
        if (first == null)
        {
            MeasureStatus.Text = "Type a length, for example 600, 60 cm or 0.6 m";
            return;
        }
        Focus();
        if (tool == MarkupTool.CadOffset)
        {
            MarkupLayer.OffsetDistanceMm = first.Value;
            ShowCadInput(tool); // shows the new distance
        }
        else if (!MarkupLayer.ApplyTypedSize(tool == MarkupTool.CadCircle && CadCircleSizeKind.SelectedIndex == 0 ? first.Value / 2 : first.Value))
            MeasureStatus.Text = "Click the first point, then type the size";
    }

    /// <summary>The CAD tools take real lengths, so they're on only once the sheet has a scale.</summary>
    void UpdateCadTools()
    {
        bool on = _doc?.ComparePdf == null && _doc?.Loaded.MmPerPointByPage.ContainsKey(CurrentPageIndex) == true;
        foreach (var button in new System.Windows.Controls.Primitives.ButtonBase[] { CadLineTool, CadPolylineTool, CadRectangleTool, CadCircleTool, CadOffsetTool, CadTrimTool, CadHatchTool, CadDimensionTool, CadJoinButton })
            button.IsEnabled = on;
        if (!on && MarkupCanvas.IsCadTool(MarkupLayer.Tool)) SelectTool.IsChecked = true;
    }

    void OnCadJoin(object sender, RoutedEventArgs e) => MarkupLayer.JoinSelectedCadLines();

    void OnCadLayerToggled(object sender, RoutedEventArgs e)
    {
        MarkupLayer.ShowCadLayer = CadLayerMenuItem.IsChecked;
        if (!CadLayerMenuItem.IsChecked && MarkupCanvas.IsCadTool(MarkupLayer.Tool)) SelectTool.IsChecked = true; // nothing to draw on
    }

    void OnScale(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        var menu = new ContextMenu { PlacementTarget = ScaleButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        var calibrate = new MenuItem { Header = "Calibrate… (click two points of a known length)" };
        calibrate.Click += (_, _) => StartCalibration();
        menu.Items.Add(calibrate);
        bool isCommon = DrawingScale.CommonRatios.Any(ratio => Math.Abs(DrawingScale.FromRatio(ratio) - CurrentMmPerPoint) < 1e-9);
        var custom = new MenuItem
        {
            Header = isCommon ? "Custom… (type the scale)" : $"Custom… ({DrawingScale.Describe(CurrentMmPerPoint)})", IsCheckable = true, IsChecked = !isCommon,
        };
        custom.Click += (_, _) =>
        {
            custom.IsChecked = !isCommon; // the dialog decides, not the click
            if (AskForRatio() is not var (ratio, allSheets)) return;
            SetScale(DrawingScale.FromRatio(ratio), allSheets);
            NoticeStatus.Text = $"Scale set to {DrawingScale.Describe(CurrentMmPerPoint)} on {(allSheets ? "all sheets" : "this sheet")}";
        };
        menu.Items.Add(custom);
        menu.Items.Add(new Separator());
        foreach (int ratio in DrawingScale.CommonRatios)
        {
            double mmPerPoint = DrawingScale.FromRatio(ratio);
            var item = new MenuItem { Header = $"1:{ratio}", IsCheckable = true, IsChecked = Math.Abs(mmPerPoint - CurrentMmPerPoint) < 1e-9 };
            item.Click += (_, _) => SetScale(mmPerPoint);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    void SetScale(double mmPerPoint, bool allSheets = false)
    {
        if (_doc == null || CurrentPageIndex < 0) return;
        List<int> pages = allSheets ? [.. Enumerable.Range(0, _doc.Loaded.Pages.Count)] : [CurrentPageIndex];
        foreach (int pageIndex in pages)
            _doc.Loaded.MmPerPointByPage[pageIndex] = mmPerPoint;
        if (!IsViewOnly) _doc.ScaleChanged = true;
        UpdateTitle();
        ShowScale();
        // Dimensions keep the scale they were drawn at, unless the user says to update them.
        var dimensions = pages.SelectMany(pageIndex => _doc.Loaded.MarkupsByPage[pageIndex].OfType<CadDimensionMarkup>().Select(dimension => (pageIndex, dimension)))
            .Where(item => Math.Abs(item.dimension.MmPerPoint / mmPerPoint - 1) > 1e-4).ToList(); // saved scales come back rounded
        if (dimensions.Count == 0 || !CanEditMarkups || MessageBox.Show(this,
                $"Update {(dimensions.Count == 1 ? "the dimension" : $"the {dimensions.Count} dimensions")} to the new scale?\n\n"
                + "Yes: they show their length at the new scale. No: they keep their values, and only new dimensions use the new scale.",
                "Update dims", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var (_, dimension) in dimensions) dimension.MmPerPoint = mmPerPoint;
        CommitPages(dimensions.Select(item => item.pageIndex));
    }

    void OnCalibrate(object sender, RoutedEventArgs e) => StartCalibration();

    void StartCalibration()
    {
        if (_doc == null || _doc.ComparePdf != null) return;
        MarkupLayer.StartCalibration(lengthInPoints =>
        {
            NoticeStatus.Text = "";
            if (AskForLength(lengthInPoints) is not var (realMm, allSheets)) return;
            SetScale(DrawingScale.FromCalibration(lengthInPoints, realMm), allSheets);
            NoticeStatus.Text = $"Scale set to {DrawingScale.Describe(CurrentMmPerPoint)} on {(allSheets ? "all sheets" : "this sheet")}";
        });
        NoticeStatus.Text = "Calibrate: click both ends of a known length (a dimension or grid spacing). Esc cancels";
        LoadLineWork(); // calibration snaps too
    }

    static readonly string[] CalibrationUnits = ["mm", "cm", "m"];

    /// <summary>
    /// Asks for the real length of the dimension just picked (shown on the sheet meanwhile), and which sheets the scale is for.
    /// Null if cancelled.
    /// </summary>
    (double RealMm, bool AllSheets)? AskForLength(double lengthInPoints)
    {
        double measuredMm = lengthInPoints * CurrentMmPerPoint; // what it measures at the current scale: a starting value to correct
        var box = new TextBox { Text = measuredMm.ToString("0.##", CultureInfo.CurrentCulture), MinWidth = 140 };
        var unit = new ComboBox { ItemsSource = CalibrationUnits, SelectedIndex = 0, Width = 80, Margin = new Thickness(8, 0, 0, 0) };
        double realMm = 0;
        bool? allSheets = AskForScale("Calibrate", "Real length of the dimension you picked (in blue on the sheet):", box,
            new StackPanel { Orientation = Orientation.Horizontal, Children = { box, unit } }, () =>
            {
                if (!double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out double length) || length <= 0)
                    return "Type the length as a number, for example 6000.";
                realMm = length * DrawingScale.MmPerUnit((string)unit.SelectedItem)!.Value;
                return null;
            });
        return allSheets is { } all ? (realMm, all) : null;
    }

    /// <summary>A scale typed as its ratio: "75" or "1:75" for 1:75. Null if cancelled.</summary>
    (double Ratio, bool AllSheets)? AskForRatio()
    {
        double current = CurrentMmPerPoint / DrawingScale.PaperMmPerPoint;
        var box = new TextBox { Text = Math.Round(current, 2).ToString(CultureInfo.CurrentCulture), MinWidth = 120 };
        double ratio = 0;
        bool? allSheets = AskForScale("Custom scale", "The drawing's scale:", box,
            new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = "1 :", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }, box } }, () =>
            {
                string typed = box.Text.Trim();
                if (typed.StartsWith("1:") || typed.StartsWith("1/")) typed = typed[2..].Trim(); // "1:75" typed out in full
                if (!double.TryParse(typed, NumberStyles.Float, CultureInfo.CurrentCulture, out ratio) || ratio <= 0)
                    return "Type the scale as a number, for example 75 for 1:75.";
                return null;
            });
        return allSheets is { } all ? (ratio, all) : null;
    }

    /// <summary>
    /// The scale dialogs: a prompt, the <paramref name="input"/> row, which sheets, and Apply or Cancel. <paramref name="accept"/>
    /// reads the input and returns what is wrong with it, or null. Returns whether it is for all sheets, or null if cancelled.
    /// </summary>
    bool? AskForScale(string title, string prompt, TextBox box, UIElement input, Func<string?> accept)
    {
        var allSheets = new RadioButton { Content = "Apply to all sheets", IsChecked = true, Margin = new Thickness(0, 14, 0, 4) };
        var thisSheet = new RadioButton { Content = "Apply to this sheet only" };
        var ok = new Button { Content = "Apply scale", IsDefault = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0), Style = TryFindResource("AccentButtonStyle") as Style };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 4, 12, 4) };
        var dialog = new Window
        {
            Title = title, Owner = this, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) },
                    input,
                    allSheets,
                    thisSheet,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } },
                },
            },
        };
        ok.Click += (_, _) =>
        {
            if (accept() is { } problem) MessageBox.Show(dialog, problem, title);
            else dialog.DialogResult = true;
        };
        dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dialog.ShowDialog() == true ? allSheets.IsChecked == true : null;
    }

    /// <summary>Reads the page's text for the Text-select tool, in the background. Only while that tool is on: most drawings never need it.</summary>
    async void LoadPageText()
    {
        NoticeStatus.Text = "";
        if (MarkupLayer.Tool != MarkupTool.SelectText || _doc is not { } doc || CurrentPageIndex < 0 || MarkupLayer.PageText != null) return;
        var pdf = doc.Pdf;
        int pageIndex = CurrentPageIndex;
        PageText text;
        try
        {
            text = await Task.Run(() => pdf.GetText(pageIndex));
        }
        catch (Exception ex)
        {
            NoticeStatus.Text = $"Couldn't read the text on this page: {ex.Message}";
            return;
        }
        if (doc != _doc || pageIndex != CurrentPageIndex || MarkupLayer.Tool != MarkupTool.SelectText) return; // the user moved on meanwhile
        MarkupLayer.PageText = text;
        if (text.IsEmpty) NoticeStatus.Text = "No selectable text on this page (the text may be drawn as lines)";
    }

    RadioButton? ToolButtonForKey(Key key) => key switch
    {
        Key.V => SelectTool,
        Key.T => TextTool,
        Key.C => CalloutTool,
        Key.G => TagTool,
        Key.K => CloudTool,
        Key.P => PenTool,
        Key.H => HighlightTool,
        Key.L => LineTool,
        Key.R => RectangleTool,
        Key.W => MaskTool,
        Key.S => SelectTextTool,
        Key.M => MeasureTool,
        _ => null,
    };

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleTricksKey(e.Key))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F1)
        {
            ShowTricks();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Save();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            SaveAs();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Print();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShowOpenDialog();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShowNewDialog();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control && _doc != null)
        {
            CloseDocument(_doc);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Tab && (Keyboard.Modifiers & ~ModifierKeys.Shift) == ModifierKeys.Control)
        {
            StepTab(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            e.Handled = true;
            return;
        }
        if (e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl && !e.IsRepeat) MarkupLayer.RefreshCursor(); // the preview turns without a mouse move
        if (e.Key == Key.Escape && _alignPoints != null)
        {
            EndAlign();
            e.Handled = true;
            return;
        }
        if (_doc == null || Keyboard.FocusedElement is TextBox) return; // a TextBox has its own Ctrl+Z
        if ((e.Key is Key.D0 or Key.NumPad0) && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FitPage();
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.None && ToolButtonForKey(e.Key) is { IsEnabled: true } toolButton)
        {
            toolButton.IsChecked = true; // OnToolChecked switches the tool
            e.Handled = true;
            return;
        }
        bool isRedo = (e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control)
            || (e.Key == Key.Z && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift));
        if (isRedo || (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control))
        {
            UndoOrRedo(isRedo);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None && _documentSelected)
        {
            DeleteSelection();
            e.Handled = true;
            return;
        }
        if (MarkupLayer.HandleKey(e.Key)) // first: with the Select text tool, Ctrl+A and Ctrl+C are for the page text
        {
            e.Handled = true;
            return;
        }
        // As in CAD: Esc first drops what is being drawn (above), then goes back to the select arrow.
        if (e.Key == Key.Escape && MarkupLayer.Tool != MarkupTool.Select && SelectTool.IsEnabled)
        {
            SelectTool.IsChecked = true; // OnToolChecked switches the tool
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.A or Key.C or Key.V)
        {
            if (e.Key == Key.A && MarkupLayer.Tool != MarkupTool.SelectText) SelectAll();
            else if (e.Key == Key.C) Copy();
            else if (e.Key == Key.V) Paste(inPlace: false);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.V)
        {
            Paste(inPlace: true);
            e.Handled = true;
            return;
        }
        int? pageIndex = e.Key switch
        {
            Key.PageDown => CurrentPageIndex + 1, Key.PageUp => CurrentPageIndex - 1, Key.Home => 0, Key.End => int.MaxValue, _ => null,
        };
        if (pageIndex == null) return;
        GoToPage(pageIndex.Value);
        e.Handled = true;
    }

    /// <summary>
    /// Markups from the file are drawn by the page image until the user changes or deletes one; from then on the markup
    /// layer draws it, so the page must be redrawn without the file's version.
    /// </summary>
    /// <param name="turnedPage">A page just turned: it is redrawn even if no markup changed, and so is its thumbnail.</param>
    async void HideChangedFileMarkups(int? turnedPage = null)
    {
        if (_doc is not { } doc) return;
        var loaded = doc.Loaded;
        var annotationsToHide = MarkupFile.AnnotationsToHide(loaded);
        if (turnedPage == null && annotationsToHide.SetEquals(doc.HiddenAnnotations)) return;
        doc.HiddenAnnotations = annotationsToHide;
        try
        {
            var renderBytes = await Task.Run(() => MarkupFile.RenderBytes(loaded, annotationsToHide));
            if (!ReferenceEquals(annotationsToHide, doc.HiddenAnnotations)) return; // another change came in meanwhile
            doc.Pdf = new PdfRenderer(renderBytes); // the document's, even when another tab is in view by now
            if (doc == _doc) RenderPage();
            if (turnedPage is { } pageIndex) await RenderThumbnail(doc.Thumbnails[pageIndex], doc.Pdf);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not redraw the page.\n\n{ex.Message}", "PocketPDF");
        }
    }

    void OnLineWeightsToggled(object sender, RoutedEventArgs e) => RenderPage();

    async void RenderPage()
    {
        if (_doc == null || CurrentPageIndex < 0) return;
        var pdf = _doc.Pdf;
        var comparePdf = _doc.ComparePdf;
        var alignment = _doc.Alignment;
        bool showOld = CompareView.SelectedIndex != 2, showNew = CompareView.SelectedIndex != 1;
        int pageIndex = CurrentPageIndex, version = ++_renderVersion;
        bool hairlines = !LineWeightsMenuItem.IsChecked;
        var page = _doc.Loaded.Pages[pageIndex];
        double scale = DipPerPoint * Zoom.ScaleX * VisualTreeHelper.GetDpi(this).DpiScaleX;
        scale = Math.Min(scale, Math.Sqrt(MaxPixels / (page.ViewWidth * page.ViewHeight)));
        await _renderGate.WaitAsync();
        try
        {
            if (version != _renderVersion) return; // a newer render was asked for while this one waited: skip it
            var (pixels, width, height) = await Task.Run(() => comparePdf == null
                ? pdf.Render(pageIndex, scale, hairlines: hairlines)
                : RenderOverlay(pdf, comparePdf, pageIndex, scale, alignment, showOld, showNew));
            if (version != _renderVersion) return; // a newer render was requested meanwhile
            // scale is pixels per point, so 72 × scale is the bitmap's DPI: it then shows at exactly the page size,
            // and at the current zoom each bitmap pixel lands on one screen pixel.
            double bitmapDpi = 72 * scale;
            PageImage.Source = BitmapSource.Create(width, height, bitmapDpi, bitmapDpi, PixelFormats.Bgra32, null, pixels, width * 4);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not render page {pageIndex + 1}.\n\n{ex.Message}", "PocketPDF");
        }
        finally
        {
            _renderGate.Release();
        }
    }

    // ---- Compare revisions --------------------------------------------------------------------

    /// <summary>Renders the new and old revision of a page and mixes them. Safe to run off the UI thread.</summary>
    static (byte[] BgraPixels, int Width, int Height) RenderOverlay(PdfRenderer newPdf, PdfRenderer oldPdf, int pageIndex, double scale,
        PageAlignment alignment, bool showOld, bool showNew)
    {
        // Compare the drawings only: markups in either file would otherwise show up as changes.
        var newPage = newPdf.Render(pageIndex, scale, includeAnnotations: false);
        var oldPage = oldPdf.Render(Math.Min(pageIndex, oldPdf.PageCount - 1), scale, includeAnnotations: false);
        var overlay = OverlayCompare.Combine(new PageBitmap(oldPage.BgraPixels, oldPage.Width, oldPage.Height),
            new PageBitmap(newPage.BgraPixels, newPage.Width, newPage.Height), alignment.InPixels(scale), showOld, showNew);
        return (overlay, newPage.Width, newPage.Height);
    }

    /// <summary>A page's overlay as a PNG for <see cref="OverlayExport"/>. Safe to run off the UI thread.</summary>
    static OverlayPage RenderOverlayPage(PdfRenderer newPdf, PdfRenderer oldPdf, PageGeometry page, int pageIndex,
        PageAlignment alignment, bool showOld, bool showNew)
    {
        // 150 dpi on the drawing's own sheet size is 300 dpi when an A1 is printed on A3.
        var (pixels, width, height) = RenderOverlay(newPdf, oldPdf, pageIndex, ScaleForDpi(page, 150), alignment, showOld, showNew);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var png = new MemoryStream();
        encoder.Save(png);
        return new OverlayPage(png.ToArray(), page.ViewWidth, page.ViewHeight, pageIndex + 1);
    }

    /// <summary>Pixels per point for a fixed resolution, independent of the zoom, within the render size cap.</summary>
    static double ScaleForDpi(PageGeometry page, double dpi) =>
        Math.Min(dpi / 72, Math.Sqrt(MaxPixels / (page.ViewWidth * page.ViewHeight)));

    void UpdateMarkupLayerVisibility() =>
        // Hidden, not collapsed, while comparing: the canvas still gives page coordinates for aligning and zooming.
        MarkupLayer.Visibility = _doc?.ComparePdf == null || (_doc.ShowMarkupsWhileComparing && _alignPoints == null) ? Visibility.Visible : Visibility.Hidden;

    void OnCompare(object sender, RoutedEventArgs e)
    {
        if (_doc == null) return;
        var dialog = new OpenFileDialog
        {
            Title = "Choose the old revision to compare with",
            Filter = "PDF files (*.pdf)|*.pdf",
            InitialDirectory = Path.GetDirectoryName(_doc.FilePath),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _doc.ComparePdf = new PdfRenderer(File.ReadAllBytes(dialog.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open {Path.GetFileName(dialog.FileName)}.\n\n{ex.Message}", "PocketPDF",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        MarkupLayer.FinishTextEditing();
        EndAlign();
        _doc.Alignment = PageAlignment.None;
        _doc.CompareFileName = Path.GetFileName(dialog.FileName);
        CompareLegend.Text = _doc.CompareLegendText = $"Red: {_doc.CompareFileName}   Blue: {Path.GetFileName(_doc.FilePath)}";
        CompareControls.Visibility = Visibility.Visible;
        _doc.ShowMarkupsWhileComparing = false; // markups belong to one revision, not the comparison
        MarkupLayer.Tool = MarkupTool.Select; // drops the selection, so the colour picker (for auto-clouds) can't restyle a hidden markup
        SelectTool.IsChecked = true;
        UpdateEditingState();
        UpdateMarkupLayerVisibility();
        RenderPage();
    }

    /// <summary>
    /// While comparing, the page shows the overlay rather than the drawing, so the markup tools are off until the
    /// comparison is accepted (Accept) or dropped (Stop compare). Colour and line weight stay on: auto-clouds use them.
    /// </summary>
    void UpdateEditingState()
    {
        bool comparing = _doc?.ComparePdf != null;
        MarkupLayer.IsEnabled = !comparing && !IsViewOnly;
        foreach (var tool in MainToolBar.Items.OfType<RadioButton>().Concat(ToolsBar.Items.OfType<RadioButton>())) tool.IsEnabled = !comparing;
        LineEndPicker.IsEnabled = FontSizePicker.IsEnabled = ScaleButton.IsEnabled = CalibrateButton.IsEnabled = CompareMenuItem.IsEnabled = !comparing;
        RotateClockwiseButton.IsEnabled = RotateCounterClockwiseButton.IsEnabled = Rotate180Button.IsEnabled = OverlapsButton.IsEnabled = PlaceSheetButton.IsEnabled = !comparing && !IsViewOnly;
        UpdateCadTools();
    }

    /// <summary>
    /// Carries on in the comparison: the overlay of every page becomes the open document, in memory, with the drawing's
    /// markups (auto-clouds included) and scales carried over, so it can be marked up like any drawing. Nothing is
    /// written until Save As (or Export, for the overlay alone).
    /// </summary>
    async void OnAcceptCompare(object sender, RoutedEventArgs e)
    {
        if (_doc?.ComparePdf is not { } comparePdf || _doc.Pdf is not { } pdf || _doc.Loaded is not { } loaded || CurrentPageIndex < 0) return;
        MarkupLayer.FinishTextEditing();
        EndAlign();
        string newFileName = Path.GetFileName(_doc.FilePath), oldFileName = _doc.CompareFileName;
        string suggestedPath = Path.Combine(Path.GetDirectoryName(_doc.FilePath)!,
            $"{Path.GetFileNameWithoutExtension(newFileName)} vs {Path.GetFileNameWithoutExtension(oldFileName)} overlay.pdf");
        var pages = loaded.Pages;
        var alignment = _doc.Alignment;
        bool showOld = CompareView.SelectedIndex != 2, showNew = CompareView.SelectedIndex != 1;
        LoadedPdf overlay;
        PdfRenderer renderer;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            (overlay, renderer) = await Task.Run(() =>
            {
                var bytes = OverlayExport.Create(
                    pages.Select((page, pageIndex) => RenderOverlayPage(pdf, comparePdf, page, pageIndex, alignment, showOld, showNew)).ToList(),
                    oldFileName, newFileName);
                return (MarkupFile.Load(bytes), new PdfRenderer(bytes));
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not make the overlay.\n\n{ex.Message}", "PocketPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        if (!ReferenceEquals(loaded, _doc?.Loaded) || _doc?.ComparePdf == null) return; // the user moved on meanwhile

        // Each overlay page shows the sheet at its drawn size, so a markup keeps its place on the sheet.
        foreach (var (pageIndex, markups) in loaded.MarkupsByPage.Where(page => page.Key < overlay.Pages.Count))
            overlay.MarkupsByPage[pageIndex].AddRange(markups.Select(markup => markup.CopyMapped(point => overlay.Pages[pageIndex].ToPdf(pages[pageIndex].ToView(point)))));
        foreach (var (pageIndex, mmPerPoint) in loaded.MmPerPointByPage.Where(scale => scale.Key < overlay.Pages.Count))
            overlay.MmPerPointByPage[pageIndex] = mmPerPoint;
        // The drawing's unsaved markups now live on the overlay, which counts as unsaved until Save As, so nothing is lost.
        // The overlay takes the drawing's place in its tab.
        var overlayDoc = new DocumentTab(overlay, renderer, suggestedPath, isOnlyInMemory: true) { PageIndex = CurrentPageIndex, Tab = _doc.Tab };
        overlayDoc.Tab.Tag = overlayDoc;
        ShowDocument(overlayDoc);
    }

    void OnStopCompare(object sender, RoutedEventArgs e) => StopCompare();

    void StopCompare()
    {
        if (_doc?.ComparePdf == null) return;
        EndAlign();
        _doc.ComparePdf = null;
        _doc.Alignment = PageAlignment.None;
        CompareControls.Visibility = Visibility.Collapsed;
        UpdateEditingState();
        UpdateMarkupLayerVisibility();
        RenderPage();
    }

    void OnCompareViewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_doc?.ComparePdf != null) RenderPage();
    }

    void OnAlign(object sender, RoutedEventArgs e)
    {
        if (_doc?.ComparePdf == null) return;
        _doc.Alignment = PageAlignment.None; // pick on the revisions as exported
        _alignPoints = [];
        Scroller.Cursor = Cursors.Cross;
        UpdateMarkupLayerVisibility();
        ShowAlignStep();
    }

    /// <summary>Shows the revision to click on next and says which point.</summary>
    void ShowAlignStep()
    {
        if (_alignPoints == null) return;
        bool onOld = _alignPoints.Count < 2;
        CompareLegend.Text = $"Click point {_alignPoints.Count % 2 + 1} of 2 on the {(onOld ? "OLD" : "NEW")} revision{(onOld ? "" : " (the same spot)")}. Zoom in for precision; Esc cancels";
        int view = onOld ? 1 : 2;
        if (CompareView.SelectedIndex != view) CompareView.SelectedIndex = view; // re-renders
        else RenderPage();
    }

    void AddAlignPoint(Point pointOnPage)
    {
        if (_alignPoints == null || _doc == null) return;
        _alignPoints.Add(pointOnPage);
        if (_alignPoints.Count % 2 == 0 && (_alignPoints[^1] - _alignPoints[^2]).Length < 20)
        {
            _alignPoints.RemoveAt(_alignPoints.Count - 1);
            MessageBox.Show(this, "The two points are too close together to line up the sheets. Pick a second point further away, ideally across the sheet.", "PocketPDF");
            return;
        }
        if (_alignPoints.Count < 4)
        {
            ShowAlignStep();
            return;
        }
        var points = _alignPoints.Select(point => (point.X, point.Y)).ToList();
        var alignment = PageAlignment.FromPoints(old1: points[0], old2: points[1], new1: points[2], new2: points[3]);
        EndAlign();
        _doc.Alignment = alignment;
        CompareLegend.Text = $"{_doc.CompareLegendText}   (aligned: scale {alignment.Scale:0.###}, moved {alignment.OffsetX:0.#}, {alignment.OffsetY:0.#} pt)";
        if (CompareView.SelectedIndex != 0) CompareView.SelectedIndex = 0; // re-renders
        else RenderPage();
    }

    void EndAlign()
    {
        if (_alignPoints == null) return;
        _alignPoints = null;
        Scroller.Cursor = null;
        CompareLegend.Text = _doc?.CompareLegendText ?? "";
        UpdateMarkupLayerVisibility();
    }

    async void OnAutoCloud(object sender, RoutedEventArgs e)
    {
        if (_doc?.ComparePdf is not { } comparePdf || _doc.Pdf is not { } pdf || _doc.Loaded is not { } loaded || CurrentPageIndex < 0) return;
        if (IsViewOnly)
        {
            MessageBox.Show(this, "This file opened view only, so markups can't be added to it.", "PocketPDF");
            return;
        }
        EndAlign();
        int pageIndex = CurrentPageIndex;
        var page = loaded.Pages[pageIndex];
        var alignment = _doc.Alignment;
        // A fixed resolution, so the clouds don't depend on the zoom. 150 dpi catches a changed digit in 2.5 mm text; 100 dpi missed it.
        double scale = ScaleForDpi(page, 150);
        List<ViewRect> changedAreas;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            changedAreas = await Task.Run(() =>
            {
                var (pixels, width, height) = RenderOverlay(pdf, comparePdf, pageIndex, scale, alignment, showOld: true, showNew: true);
                return ChangeFinder.FindChangedAreas(new PageBitmap(pixels, width, height), scale);
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not look for changes.\n\n{ex.Message}", "PocketPDF");
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        if (!ReferenceEquals(loaded, _doc?.Loaded) || pageIndex != CurrentPageIndex || _doc?.ComparePdf == null) return; // the user moved on meanwhile
        if (changedAreas.Count == 0)
        {
            MessageBox.Show(this, $"No changes found on page {pageIndex + 1}.", "PocketPDF");
            return;
        }
        var markups = loaded.MarkupsByPage[pageIndex];
        foreach (var area in changedAreas)
            markups.Add(new CloudMarkup
            {
                Vertices = area.Corners().Select(page.ToPdf).ToList(),
                Color = MarkupLayer.CurrentColor,
                LineWidth = MarkupLayer.CurrentLineWidth,
            });
        MarkupLayer.ShowPage(page, markups);
        LoadToolData(); // ShowPage drops the page text and line work
        _doc.History.Commit(pageIndex); // all clouds of one click are one undo step
        UpdateUndoState();
        _doc.ShowMarkupsWhileComparing = true;
        UpdateMarkupLayerVisibility();
        CompareLegend.Text = _doc.CompareLegendText + $"   Added {changedAreas.Count} cloud{(changedAreas.Count == 1 ? "" : "s")}";
    }

    /// <summary>Clouds the text on this page that lines or fills run through, like auto-cloud does for changes.</summary>
    async void OnFindOverlaps(object sender, RoutedEventArgs e)
    {
        if (_doc?.Pdf is not { } pdf || _doc.Loaded is not { } loaded || CurrentPageIndex < 0 || _doc.ComparePdf != null || IsViewOnly) return;
        MarkupLayer.FinishTextEditing();
        int pageIndex = CurrentPageIndex;
        var page = loaded.Pages[pageIndex];
        double scale = ScaleForDpi(page, 150); // like auto-cloud: enough pixels for 2.5 mm text
        PageText text;
        List<ViewRect> overlaps;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            (text, overlaps) = await Task.Run(() =>
            {
                var pageText = pdf.GetText(pageIndex);
                if (pageText.IsEmpty) return (pageText, new List<ViewRect>());
                var (withText, withoutText) = pdf.RenderWithAndWithoutText(pageIndex, scale);
                return (pageText, OverlapFinder.FindOverlaps(pageText, withText, withoutText, scale));
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not check the text.\n\n{ex.Message}", "PocketPDF");
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        if (!ReferenceEquals(loaded, _doc?.Loaded) || pageIndex != CurrentPageIndex) return; // the user moved on meanwhile
        if (text.IsEmpty)
        {
            MessageBox.Show(this, $"Page {pageIndex + 1} has no text PocketPDF can read (it may be drawn as lines), so overlaps can't be checked.", "PocketPDF");
            return;
        }
        if (overlaps.Count == 0)
        {
            MessageBox.Show(this, $"No text overlaps found on page {pageIndex + 1}.", "PocketPDF");
            return;
        }
        var markups = loaded.MarkupsByPage[pageIndex];
        foreach (var area in overlaps)
            markups.Add(new CloudMarkup
            {
                Vertices = area.Corners().Select(page.ToPdf).ToList(),
                Color = MarkupLayer.CurrentColor,
                LineWidth = MarkupLayer.CurrentLineWidth,
            });
        MarkupLayer.ShowPage(page, markups);
        LoadToolData(); // ShowPage drops the page text and line work
        _doc.History.Commit(pageIndex); // all clouds of one click are one undo step
        UpdateUndoState();
        NoticeStatus.Text = $"Clouded {overlaps.Count} text overlap{(overlaps.Count == 1 ? "" : "s")}: delete any you don't need, or undo them all";
    }

    async void OnExportOverlay(object sender, RoutedEventArgs e)
    {
        if (_doc?.ComparePdf is not { } comparePdf || _doc.Pdf is not { } pdf || _doc.Loaded is not { } loaded || CurrentPageIndex < 0) return;
        int pageIndex = CurrentPageIndex;
        string newFileName = Path.GetFileName(_doc.FilePath), oldFileName = _doc.CompareFileName;
        var dialog = new SaveFileDialog
        {
            Title = "Save the overlay as a PDF",
            Filter = "PDF files (*.pdf)|*.pdf",
            InitialDirectory = Path.GetDirectoryName(_doc.FilePath),
            FileName = $"{Path.GetFileNameWithoutExtension(newFileName)} vs {Path.GetFileNameWithoutExtension(oldFileName)} overlay p{pageIndex + 1}.pdf",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        var page = loaded.Pages[pageIndex];
        var alignment = _doc.Alignment;
        bool showOld = CompareView.SelectedIndex != 2, showNew = CompareView.SelectedIndex != 1;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await Task.Run(() => OverlayExport.Save(dialog.FileName,
                [RenderOverlayPage(pdf, comparePdf, page, pageIndex, alignment, showOld, showNew)], oldFileName, newFileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not export the overlay.\n\n{ex.Message}", "PocketPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        // Printing goes through the PDF viewer: it already knows the printers, paper sizes and fit-to-page.
        if (MessageBox.Show(this, $"Saved {Path.GetFileName(dialog.FileName)}.\n\nOpen it now, to print?", "PocketPDF",
                MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
    }

    // ---- Selection, copy and paste ------------------------------------------------------------

    void OnSelectAll(object sender, RoutedEventArgs e) => SelectAll();

    void OnCopy(object sender, RoutedEventArgs e) => Copy();

    void OnPaste(object sender, RoutedEventArgs e) => Paste(inPlace: false);

    void OnPasteInPlace(object sender, RoutedEventArgs e) => Paste(inPlace: true);

    void OnDelete(object sender, RoutedEventArgs e) => DeleteSelection();

    bool CanEditMarkups => _doc != null && CurrentPageIndex >= 0 && _doc.ComparePdf == null && !IsViewOnly;

    /// <summary>Every markup on the page; when they already are, every markup in the document.</summary>
    void SelectAll()
    {
        if (_doc?.Loaded is not { } loaded || !CanEditMarkups) return;
        MarkupLayer.FinishTextEditing();
        bool wholeDocument = MarkupLayer.IsAllSelected || loaded.MarkupsByPage[CurrentPageIndex].Count == 0;
        if (MarkupLayer.Tool != MarkupTool.Select) SelectTool.IsChecked = true; // OnToolChecked switches the tool
        MarkupLayer.SelectAll();
        int count = loaded.MarkupsByPage.Values.Sum(markups => markups.Count);
        if (!wholeDocument || count == 0) return;
        _documentSelected = true;
        NoticeStatus.Text = $"All {count} markups in the document selected: Delete removes them, Copy copies them. Esc to clear";
    }

    void Copy()
    {
        if (_doc?.Loaded is not { } loaded || CurrentPageIndex < 0) return;
        MarkupLayer.FinishTextEditing();
        try
        {
            Clipboard.SetDataObject(new DataObject("PocketPDF.Markups", "")); // Ctrl+V then pastes these, not an image copied earlier
        }
        catch (System.Runtime.InteropServices.ExternalException) { } // another app holds the clipboard: the markups still paste
        IEnumerable<(int PageIndex, IReadOnlyList<Markup> Markups)> pages = _documentSelected
            ? loaded.MarkupsByPage.Select(page => (page.Key, (IReadOnlyList<Markup>)page.Value))
            : [(CurrentPageIndex, MarkupLayer.Selection)];
        var copied = pages.SelectMany(page => page.Markups.Select(markup => (loaded.Pages[page.PageIndex], page.PageIndex, markup.CopyMapped(point => point)))).ToList();
        if (copied.Count == 0) return;
        _clipboard = copied;
        if (!_documentSelected) NoticeStatus.Text = $"Copied {copied.Count} markup{(copied.Count == 1 ? "" : "s")}";
    }

    /// <summary>
    /// Pastes the copied markups onto this page, centred on the cursor (or a little off the originals when the cursor
    /// isn't on the page). In place, they keep their spot on the sheet; markups copied from several pages go back onto
    /// pages with those numbers, as when copying every markup from one revision to the next.
    /// </summary>
    void Paste(bool inPlace)
    {
        if (!inPlace && PasteSheet()) return;
        if (_doc?.Loaded is not { } loaded || !CanEditMarkups || _clipboard.Count == 0) return;
        MarkupLayer.FinishTextEditing();
        bool toOwnPages = inPlace && _clipboard.Select(copy => copy.PageIndex).Distinct().Count() > 1;
        double moveX = 0, moveY = 0; // in view points
        if (!inPlace)
        {
            var points = new List<ViewPoint>();
            foreach (var (page, _, markup) in _clipboard) markup.Clone().Map(point => { points.Add(page.ToView(point)); return point; });
            var box = ViewRect.Around(points);
            var center = new ViewPoint((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2);
            var target = MarkupLayer.MousePosition ?? new ViewPoint(center.X + 20, center.Y + 20);
            (moveX, moveY) = (target.X - center.X, target.Y - center.Y);
        }
        var pasted = new List<(int PageIndex, Markup Markup)>();
        foreach (var (page, pageIndex, markup) in _clipboard)
        {
            int targetIndex = toOwnPages ? pageIndex : CurrentPageIndex;
            if (targetIndex >= loaded.Pages.Count) continue; // this document has fewer pages
            var targetPage = loaded.Pages[targetIndex];
            var copy = markup.CopyMapped(point =>
            {
                var onSheet = page.ToView(point);
                return targetPage.ToPdf(new ViewPoint(onSheet.X + moveX, onSheet.Y + moveY));
            });
            loaded.MarkupsByPage[targetIndex].Add(copy);
            pasted.Add((targetIndex, copy));
        }
        CommitPages(pasted.Select(item => item.PageIndex));
        if (MarkupLayer.Tool != MarkupTool.Select) SelectTool.IsChecked = true;
        MarkupLayer.Select(pasted.Where(item => item.PageIndex == CurrentPageIndex).Select(item => item.Markup));
    }

    // ---- Placed sheets ---------------------------------------------------------------------------

    static readonly string[] SheetImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];

    static bool IsSheetFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is var extension && (extension == ".pdf" || SheetImageExtensions.Contains(extension));

    void OnPlaceSheet(object sender, RoutedEventArgs e)
    {
        if (!CanEditMarkups) return;
        var dialog = new OpenFileDialog
        {
            Title = "Place a PDF page or an image on this page",
            Filter = "PDF or image|*.pdf;" + string.Join(";", SheetImageExtensions.Select(extension => "*" + extension)) + "|All files|*.*",
            InitialDirectory = _doc != null ? Path.GetDirectoryName(_doc.FilePath) : _settings.LastFolder,
        };
        if (dialog.ShowDialog(this) == true) PlaceSheetFile(dialog.FileName, at: null);
    }

    /// <summary>Ctrl+V with an image, or a PDF or image file copied in Explorer, on the clipboard: places it. False if there is none.</summary>
    bool PasteSheet()
    {
        if (!CanEditMarkups) return false;
        try
        {
            if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(IsSheetFile) is { } file)
            {
                PlaceSheetFile(file, MarkupLayer.MousePosition);
                return true;
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                PlaceSheet(() => PlacedSheet.FromImage(ToPng(image)), isPdf: false, MarkupLayer.MousePosition, "the copied image");
                return true;
            }
        }
        catch (System.Runtime.InteropServices.ExternalException) { } // the clipboard is busy: paste markups as usual
        return false;
    }

    void PlaceSheetFile(string path, ViewPoint? at)
    {
        string name = Path.GetFileName(path);
        if (!Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            PlaceSheet(() => PlacedSheet.FromImage(ToPng(BitmapFrame.Create(new MemoryStream(File.ReadAllBytes(path)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad))),
                isPdf: false, at, name);
            return;
        }
        byte[] pdf;
        int pageCount;
        try
        {
            pdf = File.ReadAllBytes(path);
            pageCount = new PdfRenderer(pdf).PageCount;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open {name}.\n\n{ex.Message}", "PocketPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        int pageNumber = 1;
        if (pageCount > 1)
        {
            if (AskForPageNumber(name, pageCount) is not { } chosen) return;
            pageNumber = chosen;
        }
        PlaceSheet(() => PlacedSheet.FromPdfPage(pdf, pageNumber - 1), isPdf: true, at, pageCount > 1 ? $"page {pageNumber} of {name}" : name);
    }

    /// <summary>
    /// Places the sheet <paramref name="makeSource"/> makes. A PDF page goes at its own size in the same spot on the sheet,
    /// so a drawing from the same set lines up near enough to finish with Align; an image goes at the cursor. Either shrinks to fit the page.
    /// </summary>
    void PlaceSheet(Func<byte[]> makeSource, bool isPdf, ViewPoint? at, string name)
    {
        if (_doc?.Loaded is not { } loaded || !CanEditMarkups) return;
        var page = loaded.Pages[CurrentPageIndex];
        PlacedSheetMarkup sheet;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var source = makeSource();
            var (width, height) = new PdfRenderer(source).PageSize(0);
            double scale = Math.Min(1, Math.Min(page.ViewWidth / width, page.ViewHeight / height) * (isPdf ? 1 : 0.8));
            var center = at ?? new ViewPoint(page.ViewWidth / 2, page.ViewHeight / 2);
            var topLeft = isPdf && scale == 1 ? new ViewPoint(0, 0) : new ViewPoint(center.X - width * scale / 2, center.Y - height * scale / 2);
            sheet = PlacedSheet.Create(source, topLeft, page, scale);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not place {name}.\n\n{ex.Message}", "PocketPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        MarkupLayer.FinishTextEditing();
        if (MarkupLayer.Tool != MarkupTool.Select) SelectTool.IsChecked = true; // OnToolChecked switches the tool
        MarkupLayer.PlaceSheet(sheet);
        NoticeStatus.Text = $"Placed {name}. Set its opacity, colour, clip and alignment in the bar above; drag its edge to move it, a corner to resize it";
    }

    static byte[] ToPng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Which page of a PDF to place, 1 to <paramref name="pageCount"/>. Null if cancelled.</summary>
    int? AskForPageNumber(string name, int pageCount)
    {
        var box = new TextBox { Text = "1", MinWidth = 80 };
        var ok = new Button { Content = "Place", IsDefault = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0), Style = TryFindResource("AccentButtonStyle") as Style };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 4, 12, 4) };
        var dialog = new Window
        {
            Title = "Place PDF", Owner = this, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = $"{name} has {pageCount} pages. Which one?", Margin = new Thickness(0, 0, 0, 8) },
                    box,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } },
                },
            },
        };
        int pageNumber = 0;
        ok.Click += (_, _) =>
        {
            if (int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out pageNumber) && pageNumber >= 1 && pageNumber <= pageCount) dialog.DialogResult = true;
            else MessageBox.Show(dialog, $"Type a page number from 1 to {pageCount}.", "Place PDF");
        };
        dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dialog.ShowDialog() == true ? pageNumber : null;
    }

    bool _showingSheet; // while the bar shows the selected sheet's settings, its controls' change events aren't edits
    bool _sheetOpacityDragged; // the slider moved under the mouse: one undo step when it's let go

    void ShowSheetControls()
    {
        var sheet = CanEditMarkups ? MarkupLayer.SelectedSheet : null;
        SheetControls.Visibility = sheet != null ? Visibility.Visible : Visibility.Collapsed;
        if (sheet == null) return;
        _showingSheet = true;
        SheetOpacity.Value = Math.Round(sheet.Opacity * 100);
        SheetTint.IsChecked = sheet.Tinted;
        SheetShowAll.IsEnabled = sheet.IsClipped;
        _showingSheet = false;
    }

    void OnSheetOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SheetOpacityText == null) return; // fires while the window is still loading
        SheetOpacityText.Text = $"{e.NewValue:0} %";
        if (_showingSheet) return;
        _sheetOpacityDragged = Mouse.LeftButton == MouseButtonState.Pressed;
        MarkupLayer.SetSheetOpacity(e.NewValue / 100, commit: !_sheetOpacityDragged); // keys: one undo step each
    }

    void OnSheetOpacityReleased(object sender, MouseButtonEventArgs e)
    {
        if (!_sheetOpacityDragged) return;
        _sheetOpacityDragged = false;
        MarkupLayer.SetSheetOpacity(SheetOpacity.Value / 100, commit: true);
    }

    void OnSheetTint(object sender, RoutedEventArgs e)
    {
        if (!_showingSheet) MarkupLayer.SetSheetTinted(SheetTint.IsChecked == true);
    }

    void OnSheetClip(object sender, RoutedEventArgs e) => MarkupLayer.StartSheetClip(text => NoticeStatus.Text = text);

    void OnSheetShowAll(object sender, RoutedEventArgs e) => MarkupLayer.ShowWholeSheet();

    async void OnSheetAlign(object sender, RoutedEventArgs e)
    {
        if (MarkupLayer.SelectedSheet is not { } sheet || _doc?.Loaded is not { } loaded || CurrentPageIndex < 0) return;
        var page = loaded.Pages[CurrentPageIndex];
        SnapIndex? sheetLines = null;
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            sheetLines = await Task.Run(() => new SnapIndex(PlacedSheet.Lines(sheet, page)));
        }
        catch (Exception ex)
        {
            NoticeStatus.Text = $"Couldn't read the sheet's lines, so clicks on it won't snap: {ex.Message}";
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        if (MarkupLayer.SelectedSheet != sheet) return; // the user moved on meanwhile
        MarkupLayer.StartSheetAlign(sheetLines, text => NoticeStatus.Text = text);
        LoadLineWork(); // the second two clicks snap to the drawing
    }

    void DeleteSelection()
    {
        if (_doc?.Loaded is not { } loaded || !CanEditMarkups) return;
        if (!_documentSelected)
        {
            MarkupLayer.HandleKey(Key.Delete);
            return;
        }
        var pages = loaded.MarkupsByPage.Where(page => page.Value.Count > 0).Select(page => page.Key).ToList();
        foreach (int pageIndex in pages) loaded.MarkupsByPage[pageIndex].Clear();
        CommitPages(pages);
    }

    /// <summary>After markups changed on these pages outside the markup layer: one undo step, and the current page shown again.</summary>
    void CommitPages(IEnumerable<int> pageIndexes)
    {
        var pages = pageIndexes.Distinct().OrderBy(pageIndex => pageIndex != CurrentPageIndex).ToList(); // this page first: undo stays on it
        if (_doc?.Loaded is not { } loaded || pages.Count == 0) return;
        _doc.History.Commit(pages);
        UpdateUndoState();
        MarkupLayer.ShowPage(loaded.Pages[CurrentPageIndex], loaded.MarkupsByPage[CurrentPageIndex]);
        LoadToolData(); // ShowPage drops the page text and line work
        HideChangedFileMarkups(); // deleted file markups must leave the page image too
    }

    // ---- Print --------------------------------------------------------------------------------

    void OnPrint(object sender, RoutedEventArgs e) => Print();

    /// <summary>Prints the drawing with its markups as shown, unsaved ones included, each sheet fitted (and turned if that fits better) to the paper.</summary>
    void Print()
    {
        if (_doc?.Loaded is not { } loaded || _doc.Pdf is not { } shownPdf || CurrentPageIndex < 0) return;
        MarkupLayer.FinishTextEditing();
        int pageCount = loaded.Pages.Count;
        var dialog = new PrintDialog { UserPageRangeEnabled = true, CurrentPageEnabled = true, MinPage = 1, MaxPage = (uint)pageCount };
        if (dialog.ShowDialog() != true) return;
        List<int> pageIndexes = dialog.PageRangeSelection switch
        {
            PageRangeSelection.CurrentPage => [CurrentPageIndex],
            PageRangeSelection.UserPages => Enumerable.Range(0, pageCount).Where(pageIndex => pageIndex + 1 >= dialog.PageRange.PageFrom && pageIndex + 1 <= dialog.PageRange.PageTo).ToList(),
            _ => Enumerable.Range(0, pageCount).ToList(),
        };
        var paper = new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
        var document = new System.Windows.Documents.FixedDocument();
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var pdf = shownPdf;
            if (!IsViewOnly)
            {
                // The page image leaves out markups not saved yet: print from a copy saved with them.
                string tempPath = Path.GetTempFileName();
                try
                {
                    MarkupFile.Save(loaded, loaded.MarkupsByPage, tempPath);
                    pdf = new PdfRenderer(File.ReadAllBytes(tempPath));
                }
                finally
                {
                    File.Delete(tempPath);
                }
            }
            foreach (int pageIndex in pageIndexes)
            {
                var page = loaded.Pages[pageIndex];
                bool turn = page.ViewWidth > page.ViewHeight != paper.Width > paper.Height; // a landscape sheet on portrait paper
                double fit = Math.Min(paper.Width / ((turn ? page.ViewHeight : page.ViewWidth) * DipPerPoint),
                    paper.Height / ((turn ? page.ViewWidth : page.ViewHeight) * DipPerPoint));
                // shortcut: every page is rendered up front at 150 dpi on paper and held until printing ends, so a long set takes a lot of memory; render on demand in a DocumentPaginator if that bites.
                double scale = Math.Min(fit * DipPerPoint * 150 / 96, Math.Sqrt(MaxPixels / (page.ViewWidth * page.ViewHeight)));
                var (pixels, pixelWidth, pixelHeight) = pdf.Render(pageIndex, scale);
                var bitmap = BitmapSource.Create(pixelWidth, pixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, pixelWidth * 4);
                bitmap.Freeze();
                var image = new Image
                {
                    Source = bitmap, Stretch = Stretch.Fill,
                    Width = page.ViewWidth * DipPerPoint * fit, Height = page.ViewHeight * DipPerPoint * fit,
                    LayoutTransform = turn ? new RotateTransform(90) : Transform.Identity,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                var sheet = new System.Windows.Documents.FixedPage { Width = paper.Width, Height = paper.Height };
                sheet.Children.Add(new Grid { Width = paper.Width, Height = paper.Height, Children = { image } });
                sheet.Measure(paper);
                sheet.Arrange(new Rect(paper));
                document.Pages.Add(new System.Windows.Documents.PageContent { Child = sheet });
            }
            dialog.PrintDocument(document.DocumentPaginator, Path.GetFileName(_doc.FilePath));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not print.\n\n{ex.Message}", "PocketPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    // ---- Markup toolbar -----------------------------------------------------------------------

    void OnToolMenu(object sender, RoutedEventArgs e)
    {
        var button = (RadioButton)FindName((string)((FrameworkElement)sender).Tag);
        if (button.IsChecked == true) OnToolChecked(button, e); // picked again: starts over
        else button.IsChecked = true;
    }

    void OnToolChecked(object sender, RoutedEventArgs e)
    {
        if (MarkupLayer == null) return; // fires while the window is still loading
        var tool = Enum.Parse<MarkupTool>((string)((FrameworkElement)sender).Tag);
        if (MarkupCanvas.IsCadTool(tool) && !CadLayerMenuItem.IsChecked)
        {
            CadLayerMenuItem.IsChecked = true;
            MarkupLayer.ShowCadLayer = true;
        }
        MarkupLayer.Tool = tool;
        ShowCadInput(tool); // also the hint in the status bar
        LoadToolData();
        if (MarkupLayer.Tool == MarkupTool.Highlight)
            ColorPicker.SelectedItem = ColorPicker.Items.Cast<FrameworkElement>().First(item => (string)item.Tag == HighlightMarkup.DefaultColor);
        // A rectangle starts as an outline, a mask as a white box; the fill picker changes either.
        if (MarkupLayer.Tool is MarkupTool.Rectangle or MarkupTool.Mask)
            SelectByTag(FillPicker, tag => tag == (MarkupLayer.Tool == MarkupTool.Mask ? RectangleMarkup.MaskFill : "None"));
        // A tag points at a spot, so it has no area; a cloud is always clouded. The outline picked is the callout's.
        ShapePicker.IsEnabled = MarkupLayer.Tool != MarkupTool.Tag;
        UpdateFontPicker();
        LineStylePicker.IsEnabled = MarkupLayer.Tool is not (MarkupTool.Tag or MarkupTool.Cloud);
    }

    void OnShapeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MarkupLayer != null && ShapePicker.SelectedItem is FrameworkElement { Tag: string shape }) MarkupLayer.ApplyShape(Enum.Parse<AreaShape>(shape));
    }

    void OnLineStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MarkupLayer != null && LineStylePicker.SelectedItem is FrameworkElement { Tag: string style }) MarkupLayer.ApplyLineStyle(Enum.Parse<AreaLineStyle>(style));
    }

    void OnStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MarkupLayer == null) return; // fires while the window is still loading
        if (sender == ColorPicker && ColorPicker.SelectedItem == CustomColorItem)
        {
            var previous = e.RemovedItems.Count > 0 ? e.RemovedItems[0]! : ColorPicker.Items[0];
            Dispatcher.BeginInvoke(() => PickCustomColor(ColorPicker, CustomColorItem, MarkupLayer.CurrentColor, previous)); // after the drop-down has closed
            return;
        }
        if (((ComboBox)sender).SelectedItem is not FrameworkElement { Tag: string value }) return;
        if (sender == ColorPicker) MarkupLayer.ApplyColor(value);
        else if (sender == LineWidthPicker) MarkupLayer.ApplyLineWidth(double.Parse(value, CultureInfo.InvariantCulture));
        else if (sender == FontSizePicker) MarkupLayer.ApplyFontSize(double.Parse(value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Shows the selected markup's style in the pickers. Applying a markup's own value back to it changes nothing,
    /// so this only updates the style for new markups. A value the picker doesn't list (a Bluebeam colour) leaves it as it is.
    /// </summary>
    void ShowSelectedMarkupStyle()
    {
        if (MarkupLayer.SelectedMarkup is not { } markup) return;
        if (markup is CadHatchMarkup filledHatch) SelectByTag(FillPicker, tag => string.Equals(tag, filledHatch.Color, StringComparison.OrdinalIgnoreCase));
        else SelectByTag(ColorPicker, tag => string.Equals(tag, markup.Color, StringComparison.OrdinalIgnoreCase));
        SelectByTag(LineWidthPicker, tag => double.Parse(tag, CultureInfo.InvariantCulture) == markup.LineWidth);
        double? fontSize = markup switch
        {
            TextMarkup text => text.FontSize, CalloutMarkup callout => callout.FontSize, CloudMarkup { TextPosition: not null } cloud => cloud.FontSize, _ => null,
        };
        if (fontSize != null) SelectByTag(FontSizePicker, tag => double.Parse(tag, CultureInfo.InvariantCulture) == fontSize);
        LineEnding? ending = markup switch
        {
            CalloutMarkup callout => callout.Ending, LineMarkup line => line.Ending, CloudMarkup { TextPosition: not null } cloud => cloud.Ending, _ => null,
        };
        if (markup is CloudMarkup area) SelectByTag(LineStylePicker, tag => tag == area.Style.ToString());
        if (markup is CadHatchMarkup hatch) SelectByTag(HatchPicker, tag => tag == hatch.Pattern.ToString());
        if (markup is IFillable { CanFill: true } shape)
        {
            SelectByTag(FillPicker, tag => string.Equals(tag, shape.FillColor ?? "None", StringComparison.OrdinalIgnoreCase));
            if (shape.FillColor != null) SelectByTag(HatchPicker, tag => tag == shape.FillPattern.ToString());
        }
        if (MarkupCanvas.TakesFont(markup)) FontPicker.SelectedItem = markup.Font ?? TextLayout.ScreenFontFamily;
        if (ending is { } end) SelectByTag(LineEndPicker, tag => tag == end.ToString());
    }

    void OnFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MarkupLayer != null && FontPicker.SelectedItem is string font) MarkupLayer.ApplyFont(font);
    }

    /// <summary>The font picker is on for the tools that write text or measurements, and while one is selected.</summary>
    void UpdateFontPicker() =>
        FontPicker.IsEnabled = MarkupLayer.Tool is MarkupTool.Text or MarkupTool.Callout or MarkupTool.Tag or MarkupTool.Measure
            || MarkupLayer.Selection.Any(MarkupCanvas.TakesFont);

    void OnFillChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MarkupLayer == null) return; // fires while the window is still loading
        if (FillPicker.SelectedItem == CustomFillItem)
        {
            var previous = e.RemovedItems.Count > 0 ? e.RemovedItems[0]! : FillPicker.Items[0];
            Dispatcher.BeginInvoke(() => PickCustomColor(FillPicker, CustomFillItem, MarkupLayer.CurrentFill ?? RectangleMarkup.MaskFill, previous));
            return;
        }
        if (FillPicker.SelectedItem is FrameworkElement { Tag: string fill }) MarkupLayer.ApplyFill(fill == "None" ? null : fill);
    }

    void OnHatchPatternChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MarkupLayer != null && HatchPicker.SelectedItem is FrameworkElement { Tag: string pattern }) MarkupLayer.ApplyHatchPattern(Enum.Parse<HatchPattern>(pattern));
    }

    void OnLineEndChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MarkupLayer != null && LineEndPicker.SelectedItem is FrameworkElement { Tag: string ending }) MarkupLayer.ApplyEnding(Enum.Parse<LineEnding>(ending));
    }

    /// <summary>Asks for any colour and adds it to <paramref name="picker"/> (once). Cancel goes back to <paramref name="previous"/>.</summary>
    static void PickCustomColor(ComboBox picker, ComboBoxItem customItem, string current, object previous)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.ColorTranslator.FromHtml(current) };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            picker.SelectedItem = previous;
            return;
        }
        string hex = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        var item = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag as string, hex, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            item = new ComboBoxItem { Tag = hex, Content = ColorSwatch(hex, hex) };
            picker.Items.Insert(picker.Items.IndexOf(customItem), item);
        }
        picker.SelectedItem = item; // the picker's SelectionChanged applies it
    }

    static StackPanel ColorSwatch(string hex, string name) => new()
    {
        Orientation = Orientation.Horizontal,
        Children =
        {
            new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = (Brush)new BrushConverter().ConvertFromString(hex)!, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            },
            new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center },
        },
    };

    static void SelectByTag(ComboBox picker, Func<string, bool> matches)
    {
        if (picker.Items.Cast<FrameworkElement>().FirstOrDefault(item => matches((string)item.Tag)) is { } item) picker.SelectedItem = item;
    }

    // ---- Zoom and pan -------------------------------------------------------------------------

    void SetZoom(double zoom)
    {
        zoom = Math.Clamp(zoom, 0.05, 32);
        Zoom.ScaleX = Zoom.ScaleY = zoom;
        MarkupLayer.ScreenUnitsPerPoint = zoom * DipPerPoint;
        if (!ZoomBox.IsKeyboardFocused) ShowZoom();
        _rerenderTimer.Stop();
        _rerenderTimer.Start();
    }

    void FitPage()
    {
        if (_doc == null || CurrentPageIndex < 0) return;
        var page = _doc.Loaded.Pages[CurrentPageIndex];
        UpdateLayout();
        SetZoom(Math.Min((Scroller.ViewportWidth - 40) / (page.ViewWidth * DipPerPoint), (Scroller.ViewportHeight - 40) / (page.ViewHeight * DipPerPoint)));
    }

    void OnFit(object sender, RoutedEventArgs e) => FitPage();

    void ShowZoom() => ZoomBox.Text = $"{Zoom.ScaleX * 100:0}";

    void OnZoomBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        if (e.Key == Key.Escape) ShowZoom(); // LostKeyboardFocus then applies the unchanged value
        Focus();
        e.Handled = true;
    }

    void OnZoomBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        bool isPercentage = double.TryParse(ZoomBox.Text.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.CurrentCulture, out double percent);
        if (_doc != null && isPercentage && percent > 0) SetZoom(percent / 100);
        else ShowZoom();
    }

    /// <summary>Cursor position on the sheet as it's shown, in mm from the bottom-left corner.</summary>
    void ShowCursorPosition(MouseEventArgs e)
    {
        const double MmPerPoint = 25.4 / 72;
        if (_doc == null || CurrentPageIndex < 0 || StartPanel.Visibility == Visibility.Visible) return;
        var page = _doc.Loaded.Pages[CurrentPageIndex];
        var position = e.GetPosition(MarkupLayer); // canvas units are view points
        bool isOnSheet = position.X >= 0 && position.Y >= 0 && position.X <= page.ViewWidth && position.Y <= page.ViewHeight;
        CursorStatus.Text = isOnSheet ? $"x {position.X * MmPerPoint:0} mm   y {(page.ViewHeight - position.Y) * MmPerPoint:0} mm" : "";
    }

    void OnScrollerMouseLeave(object sender, MouseEventArgs e) => CursorStatus.Text = "";

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_doc == null) return;
        e.Handled = true;
        // Keep the drawing point under the cursor fixed while zooming.
        var mouseInViewport = e.GetPosition(Scroller);
        var pointOnPage = e.GetPosition(MarkupLayer); // the canvas always covers the whole page, even before the first render
        SetZoom(Zoom.ScaleX * (e.Delta > 0 ? 1.2 : 1 / 1.2));
        Scroller.UpdateLayout();
        var pointAfterZoom = MarkupLayer.TranslatePoint(pointOnPage, Scroller);
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset + pointAfterZoom.X - mouseInViewport.X);
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + pointAfterZoom.Y - mouseInViewport.Y);
    }

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _alignPoints != null && PageImage.IsMouseOver)
        {
            AddAlignPoint(e.GetPosition(MarkupLayer)); // canvas units are view points
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Middle) return;
        _panPreviousPosition = e.GetPosition(Scroller);
        Scroller.CaptureMouse();
        e.Handled = true;
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        ShowCursorPosition(e);
        if (_panPreviousPosition is not { } previousPosition) return;
        var currentPosition = e.GetPosition(Scroller);
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - (currentPosition.X - previousPosition.X));
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - (currentPosition.Y - previousPosition.Y));
        _panPreviousPosition = currentPosition;
    }

    void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _panPreviousPosition == null) return;
        _panPreviousPosition = null;
        Scroller.ReleaseMouseCapture();
    }

    /// <summary>Windows took the mouse mid-pan (Alt+Tab, a pop-up): stop, or the page follows the mouse with no button held.</summary>
    void OnScrollerLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (e.OriginalSource == Scroller) _panPreviousPosition = null; // not the canvas's capture, bubbling up
    }
}
