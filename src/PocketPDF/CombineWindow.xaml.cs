using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PocketPDF.Core;

namespace PocketPDF;

/// <summary>Lists the files Explorer handed over, lets the user order them, and combines them into one PDF.</summary>
public partial class CombineWindow : Window
{
    readonly ObservableCollection<string> _files = [];
    // Combine is offered once no file has arrived for a moment, so a quick Enter doesn't leave out files still on their way.
    readonly DispatcherTimer _settleTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    Point? _dragStart;
    // Explorer starts its processes all at once, so files arrive in random order. Until the user orders them,
    // they go in Explorer's name order: predictable, and right for numbered sheets.
    bool _orderedByUser;
    string? _combinedPath;

    public CombineWindow(FileCollector collector, string firstFile)
    {
        InitializeComponent();
        FileList.ItemsSource = _files;
        _settleTimer.Tick += (_, _) =>
        {
            _settleTimer.Stop();
            CombineButton.IsEnabled = _files.Count > 0 && _combinedPath == null;
        };
        AddFile(firstFile);
        NameBox.Text = Path.GetFileName(PdfCombiner.DefaultBinderPath(Path.GetDirectoryName(firstFile)!, DateTime.Today));
        _ = collector.ListenAsync(file => Dispatcher.BeginInvoke(() => AddFile(file)));
        Show();
        Activate();
        NameBox.Focus();
    }

    /// <summary>Adds a file sent by another process. Only existing PDFs, once each: the pipe is a trust boundary.</summary>
    void AddFile(string filePath)
    {
        if (_combinedPath != null || !Path.IsPathFullyQualified(filePath) || !File.Exists(filePath)
            || !string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase)
            || _files.Contains(filePath, StringComparer.OrdinalIgnoreCase)) return;
        int position = _orderedByUser ? _files.Count : _files.TakeWhile(file => CompareNames(file, filePath) <= 0).Count();
        _files.Insert(position, filePath);
        UpdateFolder();
        StatusText.Text = $"{_files.Count} file{(_files.Count == 1 ? "" : "s")}";
        CombineButton.IsEnabled = false;
        _settleTimer.Stop();
        _settleTimer.Start();
    }

    /// <summary>The binder goes in the folder of the first file.</summary>
    string? TargetFolder => _files.Count > 0 ? Path.GetDirectoryName(_files[0]) : null;

    void UpdateFolder()
    {
        FolderText.Text = TargetFolder is { } folder ? $"in {folder}" : "";
        FolderText.ToolTip = TargetFolder;
    }

    // ---- Ordering -----------------------------------------------------------------------------

    void Move(int step)
    {
        int index = FileList.SelectedIndex;
        if (index < 0 || index + step < 0 || index + step >= _files.Count) return;
        _files.Move(index, index + step);
        _orderedByUser = true;
        FileList.SelectedIndex = index + step;
        FileList.ScrollIntoView(FileList.SelectedItem);
        UpdateFolder();
    }

    void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);

    void OnMoveDown(object sender, RoutedEventArgs e) => Move(1);

    void OnRemove(object sender, RoutedEventArgs e)
    {
        int index = FileList.SelectedIndex;
        if (index < 0) return;
        _files.RemoveAt(index);
        FileList.SelectedIndex = Math.Min(index, _files.Count - 1);
        CombineButton.IsEnabled = _files.Count > 0 && _combinedPath == null;
        StatusText.Text = $"{_files.Count} file{(_files.Count == 1 ? "" : "s")}";
        UpdateFolder();
    }

    // Explorer's own name order: numbers compare as numbers, so S-2 comes before S-10.
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int StrCmpLogicalW(string first, string second);

    static int CompareNames(string firstPath, string secondPath) => StrCmpLogicalW(Path.GetFileName(firstPath), Path.GetFileName(secondPath));

    void OnSortByName(object sender, RoutedEventArgs e)
    {
        var sorted = _files.Order(Comparer<string>.Create(CompareNames)).ToList();
        for (int i = 0; i < sorted.Count; i++) _files.Move(_files.IndexOf(sorted[i]), i);
        UpdateFolder();
    }

    void OnFileListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) OnRemove(sender, e);
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Up or Key.Down) Move(e.SystemKey == Key.Up ? -1 : 1);
        else return;
        e.Handled = true;
    }

    void OnFileMouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(FileList);

    void OnFileMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start || FileList.SelectedItem is not string file) return;
        var offset = e.GetPosition(FileList) - start;
        if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        DragDrop.DoDragDrop(FileList, file, DragDropEffects.Move);
    }

    void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(string)) is not string file || !_files.Contains(file)) return;
        // Drop onto the item under the mouse; below the last item means "to the end".
        var target = (e.OriginalSource as DependencyObject) is { } source ? ItemsControl.ContainerFromElement(FileList, source) as ListBoxItem : null;
        int to = target != null ? FileList.ItemContainerGenerator.IndexFromContainer(target) : _files.Count - 1;
        _files.Move(_files.IndexOf(file), to);
        _orderedByUser = true;
        FileList.SelectedItem = file;
        UpdateFolder();
    }

    // ---- Combining ----------------------------------------------------------------------------

    async void OnCombine(object sender, RoutedEventArgs e)
    {
        if (TargetFolder is not { } folder) return;
        string name = NameBox.Text.Trim();
        if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";
        if (name.Length <= 4 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "That isn't a valid file name.", "Combine PDFs");
            return;
        }
        string target = Path.Combine(folder, name);
        if (File.Exists(target))
        {
            MessageBox.Show(this, $"{name} already exists in that folder. Pick another name.", "Combine PDFs");
            return;
        }

        var files = _files.ToList();
        CombineButton.IsEnabled = false;
        StatusText.Text = $"Combining {files.Count} files…";
        Mouse.OverrideCursor = Cursors.Wait;
        CombineResult result;
        try
        {
            result = await Task.Run(() => PdfCombiner.Combine(files, target));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't combine: {ex.Message}";
            CombineButton.IsEnabled = true;
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        _combinedPath = target;
        StatusText.Text = $"Saved {result.PageCount} page{(result.PageCount == 1 ? "" : "s")} to {name}."
            + string.Concat(result.SkippedFiles.Select(skipped => $"\nLeft out {Path.GetFileName(skipped.FilePath)}: {skipped.Reason}."));
        OpenButton.Visibility = ShowInFolderButton.Visibility = Visibility.Visible;
        CombineButton.Visibility = Visibility.Collapsed;
        CloseButton.Content = "Close";
        OpenButton.Focus();
    }

    void OnOpenResult(object sender, RoutedEventArgs e)
    {
        if (_combinedPath == null || Environment.ProcessPath is not { } exe) return;
        Process.Start(new ProcessStartInfo(exe) { ArgumentList = { _combinedPath } });
        Close();
    }

    void OnShowInFolder(object sender, RoutedEventArgs e)
    {
        if (_combinedPath != null) Process.Start("explorer.exe", $"/select,\"{_combinedPath}\"");
    }

    void OnClose(object sender, RoutedEventArgs e) => Close();
}
