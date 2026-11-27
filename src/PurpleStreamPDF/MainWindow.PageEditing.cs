using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using PurpleStreamPDF.Core;

namespace PurpleStreamPDF;

/// <summary>
/// Editing the pages from the page list's right-click menu: insert a blank sheet, delete, export, move. Each saves the
/// markups into a copy of the file, changes its pages and shows that copy in the tab; Save writes it over the file.
/// </summary>
public partial class MainWindow
{
    /// <summary>The pages picked in the page list.</summary>
    HashSet<int> PickedPages() => PageList.SelectedItems.Cast<PageThumbnail>().Select(thumbnail => thumbnail.Number - 1).ToHashSet();

    void OnPageMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // A right-click on a page that isn't picked picks just that page, as in Explorer.
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(PageList, source) is ListBoxItem { IsSelected: false } item)
            PageList.SelectedItem = item.DataContext;
        var picked = PickedPages();
        if (_doc == null || picked.Count == 0)
        {
            e.Handled = true; // no menu
            return;
        }
        int count = _doc.Thumbnails.Count;
        bool canEdit = CanEditMarkups; // not while comparing, nor for a file that can only be viewed
        bool Moves(int[] order) => canEdit && !order.SequenceEqual(Enumerable.Range(0, count));
        InsertPageItem.IsEnabled = ExportPagesItem.IsEnabled = canEdit;
        DeletePagesItem.IsEnabled = canEdit && picked.Count < count; // a PDF needs a page
        DeletePagesItem.Header = picked.Count == 1 ? "_Delete" : $"_Delete {picked.Count} Pages";
        MovePagesUpItem.IsEnabled = Moves(PageOrder.Up(count, picked));
        MovePagesDownItem.IsEnabled = Moves(PageOrder.Down(count, picked));
        MovePagesToTopItem.IsEnabled = Moves(PageOrder.ToTop(count, picked));
        MovePagesToEndItem.IsEnabled = Moves(PageOrder.ToEnd(count, picked));
    }

    void OnInsertPage(object sender, RoutedEventArgs e)
    {
        int at = PickedPages().DefaultIfEmpty(CurrentPageIndex).Max() + 1;
        if (AskForSheetSize("Insert sheet", $"A blank sheet, put in after page {at}:") is not { } size) return;
        EditPages(bytes => PageEditor.InsertBlank(bytes, at, size.WidthMm, size.HeightMm), [at]);
    }

    void OnDeletePages(object sender, RoutedEventArgs e)
    {
        if (_doc is not { } doc) return;
        var picked = PickedPages();
        string pages = picked.Count == 1 ? $"page {picked.Single() + 1}" : $"{picked.Count} pages";
        if (MessageBox.Show(this, $"Delete {pages}, with the markups on them?\n\nThis can't be undone, but the file only changes when you save it.",
                "Delete pages", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        var order = PageOrder.Without(doc.Thumbnails.Count, picked);
        EditPages(bytes => PageEditor.Rearrange(bytes, order), [Math.Min(picked.Min(), order.Length - 1)]); // the page that took the first one's place
    }

    void OnMovePagesUp(object sender, RoutedEventArgs e) => MovePages(PageOrder.Up);

    void OnMovePagesDown(object sender, RoutedEventArgs e) => MovePages(PageOrder.Down);

    void OnMovePagesToTop(object sender, RoutedEventArgs e) => MovePages(PageOrder.ToTop);

    void OnMovePagesToEnd(object sender, RoutedEventArgs e) => MovePages(PageOrder.ToEnd);

    /// <summary>Puts the picked pages in a new order; they stay picked in their new places.</summary>
    void MovePages(Func<int, IReadOnlySet<int>, int[]> move)
    {
        if (_doc is not { } doc) return;
        var picked = PickedPages();
        var order = move(doc.Thumbnails.Count, picked);
        EditPages(bytes => PageEditor.Rearrange(bytes, order), picked.Select(page => Array.IndexOf(order, page)));
    }

    /// <summary>Writes the picked pages, with their markups as they are now, to a new PDF.</summary>
    void OnExportPages(object sender, RoutedEventArgs e)
    {
        if (!CanEditMarkups || _doc is not { } doc) return;
        MarkupLayer.FinishTextEditing();
        var picked = PickedPages().Order().ToList();
        string pages = picked.Count == 1 ? $"page {picked[0] + 1}" : $"pages {string.Join(", ", picked.Select(page => page + 1))}";
        var dialog = new SaveFileDialog
        {
            Title = $"Export {pages}",
            Filter = "PDF files (*.pdf)|*.pdf",
            InitialDirectory = Path.GetDirectoryName(doc.FilePath),
            FileName = $"{Path.GetFileNameWithoutExtension(doc.FilePath)} - {(picked.Count == 1 ? "page" : "pages")} {CompactList(picked)}.pdf",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        // Write next to the target and swap at the end, so a failed export never damages an existing file.
        string temporaryPath = dialog.FileName + ".saving";
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            File.WriteAllBytes(temporaryPath, PageEditor.Rearrange(MarkupFile.SaveToBytes(doc.Loaded), picked));
            File.Move(temporaryPath, dialog.FileName, overwrite: true);
        }
        catch (Exception ex)
        {
            File.Delete(temporaryPath);
            Mouse.OverrideCursor = null;
            MessageBox.Show(this, $"Could not export {pages}.\n\n{ex.Message}", "PurpleStreamPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>"1-3, 5" for pages 0, 1, 2 and 4.</summary>
    static string CompactList(IReadOnlyList<int> sortedPages)
    {
        var runs = new List<string>();
        for (int i = 0; i < sortedPages.Count;)
        {
            int start = i;
            while (i + 1 < sortedPages.Count && sortedPages[i + 1] == sortedPages[i] + 1) i++;
            runs.Add(i == start ? $"{sortedPages[i] + 1}" : $"{sortedPages[start] + 1}-{sortedPages[i] + 1}");
            i++;
        }
        return string.Join(", ", runs);
    }

    /// <summary>
    /// Changes the pages of the document in view: <paramref name="change"/> gets the file with the markups saved into it.
    /// The result takes the document's place in its tab, as unsaved work, with <paramref name="pagesToPick"/> picked.
    /// </summary>
    void EditPages(Func<byte[], byte[]> change, IEnumerable<int> pagesToPick)
    {
        if (!CanEditMarkups || _doc is not { } doc) return;
        MarkupLayer.FinishTextEditing();
        LoadedPdf loaded;
        PdfRenderer renderer;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var bytes = change(MarkupFile.SaveToBytes(doc.Loaded));
            loaded = MarkupFile.Load(bytes);
            renderer = new PdfRenderer(bytes);
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            MessageBox.Show(this, $"Could not change the pages.\n\n{ex.Message}", "PurpleStreamPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        // shortcut: the markups are now part of the copy, so undo starts afresh and can't take back a page change; keep a history of whole files if that's wanted.
        var picks = pagesToPick.Order().ToList();
        var edited = new DocumentTab(loaded, renderer, doc.FilePath, doc.IsOnlyInMemory)
        {
            FileTime = doc.FileTime, PagesChanged = true, Tab = doc.Tab,
            PageIndex = picks[0], Zoom = Zoom.ScaleX, ScrollX = Scroller.HorizontalOffset, ScrollY = Scroller.VerticalOffset,
        };
        edited.Tab.Tag = edited;
        ShowDocument(edited);
        foreach (int page in picks.Skip(1)) PageList.SelectedItems.Add(edited.Thumbnails[page]);
    }
}
