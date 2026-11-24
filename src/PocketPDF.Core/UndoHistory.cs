namespace PocketPDF.Core;

/// <summary>
/// Undo and redo for markups. Keeps a copy of each page's markups as last committed; every change pushes the copy from
/// before it. Undo and redo change the page lists in place, because the canvas and the saving code share those lists.
/// </summary>
public sealed class UndoHistory
{
    const int MaxSteps = 100;

    IReadOnlyDictionary<int, List<Markup>> _markupsByPage = new Dictionary<int, List<Markup>>();
    readonly Dictionary<int, List<Markup>> _committed = [];
    // Each step is the markups of the pages it changed, from before it.
    readonly List<List<(int PageIndex, List<Markup> Markups)>> _undo = [];
    readonly List<List<(int PageIndex, List<Markup> Markups)>> _redo = [];
    int _position, _savedPosition; // number of changes since opening; where the last save was

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>True when the markups are as they were at the last save (or at opening).</summary>
    public bool IsAtSavePoint => _position == _savedPosition;

    /// <summary>Starts over for a newly opened file.</summary>
    public void Reset(IReadOnlyDictionary<int, List<Markup>> markupsByPage)
    {
        _markupsByPage = markupsByPage;
        _committed.Clear();
        foreach (var (pageIndex, markups) in markupsByPage) _committed[pageIndex] = Copy(markups);
        _undo.Clear();
        _redo.Clear();
        _position = _savedPosition = 0;
    }

    /// <summary>Records the page's markups after a change, as one undo step.</summary>
    public void Commit(int pageIndex) => Commit([pageIndex]);

    /// <summary>Records a change to several pages as one undo step. Undo and redo return the first page.</summary>
    public void Commit(IReadOnlyList<int> pageIndexes)
    {
        _undo.Add(pageIndexes.Select(pageIndex => (pageIndex, _committed[pageIndex])).ToList());
        if (_undo.Count > MaxSteps) _undo.RemoveAt(0);
        foreach (int pageIndex in pageIndexes) _committed[pageIndex] = Copy(_markupsByPage[pageIndex]);
        if (_savedPosition > _position) _savedPosition = -1; // the save was in the redo steps being dropped: unreachable now
        _redo.Clear();
        _position++;
    }

    /// <summary>Undoes the last change. Returns the page it was on.</summary>
    public int Undo()
    {
        int pageIndex = Swap(_undo, _redo);
        _position--;
        return pageIndex;
    }

    /// <summary>Redoes the last undone change. Returns the page it was on.</summary>
    public int Redo()
    {
        int pageIndex = Swap(_redo, _undo);
        _position++;
        return pageIndex;
    }

    public void MarkSaved() => _savedPosition = _position;

    int Swap(List<List<(int PageIndex, List<Markup> Markups)>> from, List<List<(int PageIndex, List<Markup> Markups)>> to)
    {
        var step = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(step.Select(entry => (entry.PageIndex, _committed[entry.PageIndex])).ToList());
        foreach (var (pageIndex, markups) in step)
        {
            _committed[pageIndex] = markups;
            var page = _markupsByPage[pageIndex];
            page.Clear();
            page.AddRange(Copy(markups)); // copies, so later edits never change the stored step
        }
        return step[0].PageIndex;
    }

    static List<Markup> Copy(List<Markup> markups) => markups.Select(markup => markup.Clone()).ToList();
}
