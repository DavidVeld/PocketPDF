# Phase 12 – Select and copy PDF text (1 day)

## Plan

**Use the text PDFium already extracts.** Docnet's `IPageReader.GetCharacters()` returns each character with its box in rendered-page pixels. At `PageDimensions(1)` those pixels are view points, which is exactly the canvas's coordinate system. So no new library is needed.

1. **Core: `PageText`.** For one page, the list of characters (char + `ViewRect`) in PDFium's reading order, read once and cached per page through `PdfRenderer`'s lock. Add `PdfRenderer.GetText(pageIndex)`.
2. **Selection logic in Core (pure, testable):**
   - `CharIndexAt(point)`: the nearest character, within a small tolerance.
   - `Select(fromIndex, toIndex)`: the characters between, like any text editor (reading order).
   - `TextOf(range)`: the string. Insert a line break where the next character starts a new line (its box's top jumps by more than half a character height) and a space where there's a horizontal gap bigger than about 0.3 of a character width. PDFs often have no real space characters.
   - `HighlightRects(range)`: merge boxes on the same line into one rectangle per line.
3. **Text-select tool** (I-beam cursor, shortcut S):
   - Drag to select, double-click to select a word, Ctrl+A for all text on the page.
   - Show the selection as see-through blue rectangles on the markup canvas. They're not markups, so they're never saved.
   - Ctrl+C or right-click → Copy puts the text on the clipboard.
   - Also offer **"Highlight selection"** on right-click. It turns the selected line rectangles into a `HighlightMarkup` (Highlight already exists).
4. **Ctrl+F find** is the natural next step on the same data. Note it as a follow-up; don't build it now.

**Limitation to state up front:** many CAD exports (SHX fonts, for one) draw text as line strokes, not as text. There is nothing to select on those. The tool should say "No selectable text on this page" in the status bar, not just do nothing. Scanned sheets would need OCR, which is out of scope.

**Done when:** on a Word-exported PDF and on a CAD-exported sheet with TrueType text, dragging selects in reading order, Ctrl+C pastes the right text with line breaks into Notepad, and rotated pages (/Rotate 90) select correctly.

## Prompt

```
Add text selection and copy to PurpleStreamPDF (WPF, .NET 10). Read PLAN.md and src/PurpleStreamPDF.Core/PdfRenderer.cs first.

Use Docnet (already referenced; it wraps PDFium): `IPageReader.GetCharacters()` returns Character { Char, Box { Left, Top, Right, Bottom } } in rendered-page pixels. Open the reader with PageDimensions(1) so the boxes are in view points, the same units as MarkupCanvas. Every PDFium call must go through PdfRenderer's static PdfiumLock. First write a quick test confirming the boxes match PageGeometry view coordinates, including a page with /Rotate 90 (TestPdf.cs has helpers for generating PDFs). If they don't match, convert them.

Core:
1. `PdfRenderer.GetText(int pageIndex)` returns a `PageText` (cache per page in the renderer).
2. `PageText`: IReadOnlyList of (char, ViewRect) in PDFium order. CharIndexAt(ViewPoint, tolerance). WordAt(index). TextOf(start, end): insert '\n' when the next char starts a new line (its top differs by more than half the char height) and ' ' for a horizontal gap bigger than ~0.3 × char width, unless a space is already there. HighlightRects(start, end): one merged ViewRect per line.
3. Unit tests with generated PDFs: two lines of text, text without space characters, a rotated page.

UI (src/PurpleStreamPDF):
4. MarkupTool.SelectText with an I-beam cursor, in the tool group (glyph E8C1, shortcut S if single-key shortcuts exist). Drag selects from the char under mouse-down to the char under the mouse. Double-click selects a word. Ctrl+A selects the whole page while the tool is active.
5. Draw the selection as semi-transparent accent rectangles on MarkupCanvas. They're not in the markup list and are cleared on page change or tool change.
6. Ctrl+C and a right-click ContextMenu "Copy" call Clipboard.SetText. The context menu also has "Highlight selection", which adds one HighlightMarkup per line rectangle through the normal MarkupsChanged path, so it's undoable and saved.
7. If the page has no characters, show "No selectable text on this page (the text may be drawn as lines)" in the status bar or as a tooltip, once.

Load text lazily (on first use of the tool, not at file open) and off the UI thread. Run `dotnet build` and `dotnet test`. Test by hand on a text-heavy PDF and a CAD sheet from data/. Add a "Phase 12 … **Done.**" section to PLAN.md, and list Ctrl+F find as a follow-up in "Later".
```
