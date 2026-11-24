# Phase 9 – Undo / redo (½ day)

## Plan

**Approach: page snapshots, not command objects.** The markup model is a plain `List<Markup>` per page, and the canvas already raises `MarkupsChanged` after every add, move, restyle and delete. So instead of writing an undo command for each action:

1. Add `Markup Clone()` in Core (`MemberwiseClone` plus copying the point lists). The clone must keep the internal `Origin`, so undoing a change on a Bluebeam markup makes it "unchanged from file" again and the page goes back to showing Bluebeam's own appearance.
2. Add a small `UndoHistory` class in Core: it keeps the last committed snapshot of each page, plus an undo stack and a redo stack of `(pageIndex, List<Markup> snapshot)`.
   - `Commit(pageIndex, markups)`: push the previous snapshot of that page onto undo, store the new one, clear redo.
   - `Undo(...)` / `Redo(...)`: swap snapshots and return the page index to show.
   - Restore by changing the existing list in place (`Clear` + `AddRange` of clones), because `LoadedPdf.MarkupsByPage`, the canvas and `MarkupFile.Save` all hold that same list.
   - Cap at 100 steps.
3. Hook it up in `MainWindow`:
   - Call `Commit` from the `MarkupsChanged` handler.
   - **Auto-cloud doesn't raise `MarkupsChanged`** (`OnAutoCloud` adds to the list directly). Call `Commit` there too, so one auto-cloud click is one undo step.
   - Ctrl+Z = undo, Ctrl+Y and Ctrl+Shift+Z = redo. Add Undo and Redo toolbar buttons, greyed out when there's nothing to undo or redo.
   - Undo jumps to the page it changed, if that isn't the current page.
   - Finish any open text editor (`FinishTextEditing`) before undoing.
   - Don't steal Ctrl+Z while a TextBox has focus: it has its own undo.
   - Opening a file clears the history.
   - Unsaved-changes flag: remember the history position at the last save. Undoing back to that point clears the `*` in the title.
   - After undo/redo, call `HideChangedFileMarkups()` so the page re-renders with or without the file's own appearance.

**Skipped:** undo for zoom, page changes or compare state. Only markups.

**Done when:** for each markup type, add → move → restyle → delete, then Ctrl+Z four times gets back to an empty page and Ctrl+Y four times redoes it all. Undoing an edit on a Bluebeam markup shows Bluebeam's appearance again, and saving after that leaves the annotation byte-for-byte untouched (the existing round-trip test pattern in `MarkupFileTests` checks this).

## Prompt

```
Implement undo/redo for markups in PocketPDF (WPF, .NET 10). Read PLAN.md first for context and conventions.

Current state:
- Markups live in LoadedPdf.MarkupsByPage (src/PocketPDF.Core/MarkupFile.cs): a mutable List<Markup> per page. The same list instance is shared by MarkupCanvas, MainWindow and MarkupFile.Save, so it must be changed in place, never replaced.
- Markup types are in src/PocketPDF.Core/Markups.cs. Each has an internal `Origin` (the annotation it was read from). `IsUnchangedFromFile` compares a JSON fingerprint with Origin.Fingerprint. Unchanged file markups are drawn by PDFium with their own appearance; changed ones are hidden from the render and drawn by us (MainWindow.HideChangedFileMarkups).
- MarkupCanvas (src/PocketPDF/MarkupCanvas.cs) raises MarkupsChanged after every add, move, restyle and delete. MainWindow.OnAutoCloud adds clouds directly to the list WITHOUT raising it.

Build:
1. Core: `public Markup Clone()` on Markup. Deep-copy the point lists (Vertices, Points) and keep Origin. Unit test: a clone of a file-loaded markup is still IsUnchangedFromFile, and moving the clone doesn't move the original.
2. Core: `UndoHistory` with Reset(markupsByPage), Commit(pageIndex, markups), CanUndo/CanRedo, Undo/Redo that restore the list in place and return the page index, IsAtSavePoint/MarkSaved. Snapshot approach: keep the last committed snapshot per page. On Commit, push the old snapshot to undo, store the new one, clear redo. Cap at 100 steps. Unit tests for: undo/redo order across two pages, redo cleared by a new commit, save point after undo then redo.
3. MainWindow: call Commit from the MarkupsChanged handler and at the end of OnAutoCloud (one step for all its clouds). Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z in OnPreviewKeyDown, but not while a TextBox has keyboard focus. Undo/Redo toolbar buttons in the existing toolbar style (Segoe Fluent glyphs E7A7 undo, E7A6 redo), enabled only when possible. Before undo: MarkupLayer.FinishTextEditing(). After undo/redo: switch to the affected page if needed, MarkupLayer.ShowPage, HideChangedFileMarkups(), and set _hasUnsavedChanges from !IsAtSavePoint, then UpdateTitle(). Reset on file open; MarkSaved after a successful save.

Keep it minimal: no command pattern, no undo for zoom/page/compare. Run `dotnet build` and `dotnet test`. Then launch the app with a sample from data/ and check by hand: add, move, restyle and delete each markup type, undo all, redo all; undo an edit to a Bluebeam markup and confirm its original look comes back. Finally add a "## Phase 9 – Undo / redo … **Done.**" section to PLAN.md in the style of phases 4–8, and remove undo/redo from the "Later" list.
```
