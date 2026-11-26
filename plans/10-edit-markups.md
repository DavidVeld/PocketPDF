# Phase 10 – Edit markups after placing them (1 day)

## Plan

Today the Select tool can select, move, restyle and delete a markup, but not change its shape or text. Add:

1. **Edit text:** double-click a Text or Callout markup (Select tool) to reopen the same on-canvas TextBox, filled with the current text and at the same font size. Enter commits, Esc cancels, and an empty result deletes the markup. Reuse `OpenTextEditor`: give it an optional existing markup to update instead of a factory that creates a new one.
2. **Grips (handles) on the selected markup.** Small squares, kept at a constant screen size whatever the zoom:
   - **Cloud:** one grip per vertex; drag to move it. Ctrl+click on an edge inserts a vertex. Ctrl+click a grip removes it (minimum 3).
   - **Callout:** a grip on the arrow tip (moves only the tip) and one on the label (moves only the label).
   - **Text:** no grips (move already works). Font size comes from the toolbar, see 4.
   - **Highlight:** no grips; move and restyle only.
3. **One undo step per edit.** Raise `MarkupsChanged` once on mouse-up or text commit, not on every mouse move. Phase 9 then makes it undoable for free.
4. **Font size picker** in the toolbar (8, 10, 12, 14, 18, 24 pt). It applies to the selected Text or Callout, and to new ones, the same way `ApplyStyle` works for colour. Selecting a markup sets the colour, weight and size pickers to its values, so the user can see them.
5. **Markups from other viewers:** editing one turns it into "changed". The existing logic already hides the file's appearance and draws ours, and saving updates the annotation in place. Nothing new is needed, but test it.

Hit-testing for grips: draw them as `Rectangle`s tagged with `(markup, gripIndex)`, so the existing `OriginalSource.Tag` hit-test keeps working.

**Skipped:** rotation, resizing text boxes, rich text, multi-select. Name them as later options.

**Done when:** you can retype the text of a Text and a Callout, move a callout's arrow tip alone, drag, add and remove cloud vertices, and change font size, all at 25% and 400% zoom. Every one of these is a single Ctrl+Z, and saved edits open correctly in other viewers.

## Prompt

```
Add editing of placed markups to PurpleStreamPDF (WPF, .NET 10). Read PLAN.md and plans/09-undo-redo.md first. Undo/redo should already exist: every edit must come out as exactly one undo step.

Current state (src/PurpleStreamPDF/MarkupCanvas.cs): the Select tool hit-tests via `(e.OriginalSource as FrameworkElement)?.Tag as Markup`, drags to MoveBy, and raises MarkupsChanged on mouse-up if something moved. Delete removes. ApplyStyle restyles. Text entry uses OpenTextEditor(position, Func<string, Markup> createMarkup) with an on-canvas TextBox (Enter commits, Shift+Enter for a new line). Canvas units are view points (1 unit = 1 pt, top-left origin); PageGeometry converts between ViewPoint and PdfPoint (handles /Rotate). Markup types: src/PurpleStreamPDF.Core/Markups.cs. Geometry for drawing: CloudGeometry, LabelGeometry, TextLayout in Core.

Build:
1. Double-click (ClickCount == 2) on a TextMarkup or CalloutMarkup with the Select tool reopens the TextBox at its position, prefilled, same font size. Commit updates the existing markup (empty text deletes it), Esc restores it. Extend OpenTextEditor instead of copying it.
2. Grips on the selected markup: constant ~8 px on screen whatever the zoom (divide by the current zoom). Tag each grip so hit-testing can tell it apart from the markup body.
   - CloudMarkup: a grip per vertex, drag to move. Ctrl+click on an edge inserts a vertex at the nearest point; Ctrl+click on a grip removes it (keep at least 3).
   - CalloutMarkup: separate grips for ArrowTip and TextPosition.
   Raise MarkupsChanged once per drag, on mouse-up.
3. A font size ComboBox (8, 10, 12, 14, 18, 24) next to the line weight picker in MainWindow.xaml, in the same style. It applies to the selected Text/Callout and to new ones (extend ApplyStyle or add ApplyFontSize). When a markup is selected, set the colour, weight and font pickers to its values without firing a restyle.
4. Put any pure geometry (nearest point on a polygon edge, insert/remove vertex) in Core with unit tests.

Check that editing a markup loaded from a file another viewer marked up still saves in place (keeps its ID, author, replies). Add a test to MarkupFileTests if it isn't already covered.

Run `dotnet build` and `dotnet test`. Launch the app on a sample from data/ and try each edit at 25% and 400% zoom, with Ctrl+Z after each. Add a "Phase 10 … **Done.**" section to PLAN.md. Out of scope: rotation, text box resizing, multi-select.
```
