using System.IO;
using System.Windows.Controls;
using PurpleStreamPDF.Core;

namespace PurpleStreamPDF;

/// <summary>
/// One open document, with everything that belongs to it rather than to the window: the file and its markups, the
/// undo history, the scales, the compare overlay, and where the user was in it. The window shows one at a time.
/// </summary>
public sealed class DocumentTab(LoadedPdf loaded, PdfRenderer pdf, string filePath, bool isOnlyInMemory)
{
    public LoadedPdf Loaded { get; } = loaded;

    /// <summary>Draws the pages: the file, or once file markups were changed, a copy without them (see <see cref="HiddenAnnotations"/>).</summary>
    public PdfRenderer Pdf { get; set; } = pdf;

    /// <summary>Where the document is, or for one only in memory, the name Save As suggests.</summary>
    public string FilePath { get; set; } = filePath;

    /// <summary>True for a document not saved anywhere yet (a new sheet, an accepted comparison): it counts as unsaved work.</summary>
    public bool IsOnlyInMemory { get; set; } = isOnlyInMemory;

    /// <summary>The file's last write time when it was read or last saved here, so Save can tell if someone else changed it since.</summary>
    public DateTime? FileTime { get; set; }

    /// <summary>A page scale set since opening or saving: not a markup, so not in the undo history.</summary>
    public bool ScaleChanged { get; set; }

    /// <summary>
    /// The scale written on each page looked at so far (null: none found), in mm per point. Used until the user sets one,
    /// and never saved: a scale only goes into the file once the user confirms it.
    /// </summary>
    public Dictionary<int, double?> FoundScaleByPage { get; } = [];

    /// <summary>A page turned since opening or saving: not a markup either.</summary>
    public bool PageTurned { get; set; }

    /// <summary>Pages put in, deleted or moved since opening or saving: <see cref="Loaded"/> is then a changed copy of the file.</summary>
    public bool PagesChanged { get; set; }

    public UndoHistory History { get; } = StartHistory(loaded);

    /// <summary>File annotations the pages are currently drawn without.</summary>
    public IReadOnlySet<string> HiddenAnnotations { get; set; } = new HashSet<string>();

    public bool IsViewOnly => Loaded.ViewOnlyReason != null;

    public bool HasUnsavedWork => !History.IsAtSavePoint || ScaleChanged || PageTurned || PagesChanged || IsOnlyInMemory;

    /// <summary>The file's name, marked * while there is unsaved work, for the tab and the window title.</summary>
    public string Title => $"{(HasUnsavedWork ? "* " : "")}{Path.GetFileName(FilePath)}";

    // ---- Compare: the old revision while comparing, otherwise null ----

    public PdfRenderer? ComparePdf { get; set; }

    /// <summary>Where the old revision sits relative to the new one, in view points.</summary>
    public PageAlignment Alignment { get; set; } = PageAlignment.None;

    public string CompareFileName { get; set; } = "";

    public string CompareLegendText { get; set; } = "";

    /// <summary>After auto-clouding, so the clouds can be checked against the overlay.</summary>
    public bool ShowMarkupsWhileComparing { get; set; }

    /// <summary>Both, old only or new only, as the index in the window's picker.</summary>
    public int CompareView { get; set; }

    // ---- Where the user was, for coming back to the tab ----

    public List<PageThumbnail> Thumbnails { get; } = Enumerable.Range(1, pdf.PageCount).Select(pageNumber => new PageThumbnail(pageNumber)).ToList();

    public int PageIndex { get; set; }

    /// <summary>Null until the document has been shown: it opens fitted to the window.</summary>
    public double? Zoom { get; set; }

    public double ScrollX { get; set; }

    public double ScrollY { get; set; }

    /// <summary>The tab in the window's tab strip; the window sets it when it adds the document.</summary>
    public TabItem Tab { get; set; } = null!;

    static UndoHistory StartHistory(LoadedPdf loaded)
    {
        var history = new UndoHistory();
        history.Reset(loaded.MarkupsByPage);
        return history;
    }
}
