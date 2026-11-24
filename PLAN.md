# PocketPDF – Build Plan

A desktop PDF viewer for drawings, built in WPF on .NET 10. It does markups (text, labels with leaders, clouds) saved as standard PDF annotations, and compares two versions of a drawing as a colour overlay.

**Folders**
- `src/PocketPDF`: the WPF app (window, markup canvas).
- `src/PocketPDF.Core`: everything with no WPF in it: rendering, page geometry, the markup model. Reusable from IssueBuddy or Revit later.
- `tests/PocketPDF.Core.Tests`: xUnit tests. Run them with `dotnet test`.
- `img`: icons and screenshots.
- `data`: sample drawings for manual testing. PDFs in it are git-ignored, because they're client data.

**Stack**
- **Rendering:** Docnet.Core (MIT; it ships with PDFium inside it).
- **Writing annotations:** PDFsharp **6.2.x** (MIT). Don't upgrade: 6.5 and later are AGPL and refuse to run without a licence.
- **Image diff:** our own per-pixel code. Add OpenCvSharp only if flood fill turns out too slow.

Every phase ends in something you can run and check. Don't start the next phase until the current one's **Done when** is true.

---

## Phase 0 – Setup (½ hour)
1. `git init`, add a .NET `.gitignore`, make a first commit.
2. Add NuGet packages `Docnet.Core` and `PDFsharp`.
3. Add a `samples/` folder with 3–4 real drawings: A1 and A3, one heavy CAD export, and two revisions of the same sheet for later.

**Done when:** the solution builds and `samples/` holds real project PDFs.

## Phase 1 – Viewer (1 day)
1. **Open a file:** File → Open, plus drag-and-drop onto the window.
2. **Render a page:** Docnet renders the page to BGRA bytes, which go into a `WriteableBitmap` shown in an `Image`.
3. **Zoom and pan:** put the page in a `Grid` inside a `ScrollViewer` and scale it with a `ScaleTransform`. The mouse wheel zooms around the cursor; dragging with the middle mouse button pans.
4. **Re-render sharply:** after zooming, re-render at the new scale (debounced, with a size cap). That keeps A1 sheets crisp without running out of memory.
5. **Pages:** a page list down the left side (thumbnails can come later), plus PgUp and PgDn.

**Done when:** the heaviest sample opens in under 2 s and stays sharp at 400% zoom.

> PDFium is not thread-safe. Make every Docnet call from one place (one class, one lock).

## Phase 2 – Markup layer, kept in memory (1–2 days)
1. **Overlay canvas:** a transparent `Canvas` sits over the page image and sizes and scales with it.
2. **One coordinate helper:** screen pixels ↔ PDF points (72 per inch, origin bottom-left, so the Y axis flips). It must also handle the page's `/Rotate` value, because many CAD exports use rotated pages. **Write a unit test for this helper first.**
3. **Tool palette:** Select, Text, Callout, Cloud.
   - **Text:** click to place, then type in a TextBox on the canvas.
   - **Callout:** click the arrow tip, then click where the text goes, then type.
   - **Cloud:** click polygon points, double-click to close. Draw arcs along each edge (arc size about 3% of the page width).
4. **Editing:** select, move, delete, and change the colour and line weight.
5. **Model:** a plain list of markup objects per page, each holding its points in PDF coordinates. The canvas is only a view of that list.

**Done when:** you can draw every type, zoom in and out, and the markups stay in the right place on the drawing.

## Phase 3 – Save and reload as PDF annotations (1–2 days)
1. **Save** with PDFsharp. Write each markup as a raw annotation dictionary:
   - **Text:** `/Subtype /FreeText`, with `/DA` (font and colour) and `/Contents`.
   - **Callout:** `/FreeText` with `/IT /FreeTextCallout`, `/CL` (callout line points) and `/RD`.
   - **Cloud:** `/Polygon` with `/Vertices` and `/BE << /S /C /I 1 >>`.
2. **Write an appearance stream (`/AP`) for every annotation.** Without one, some viewers (Bluebeam, browsers, printers) show nothing. This is the step most likely to bite.
3. **Load:** read annotations of these three subtypes back into the markup model so they can be edited. Render the page **without** annotations so they don't appear twice.
4. **Save As versus Save:** use Save As by default. Only overwrite the original after the user confirms, because these are issued drawings.
5. **Leave unknown annotations alone:** annotations we don't edit (Bluebeam stamps and so on) must pass through untouched.
6. **View-only fallback:** if PDFsharp can't read a file that PDFium can, it opens view-only, with markups and Save switched off. One of 60 PDFs on the dev machine needed this.

**Done when:** a file saved here opens in **Bluebeam and Acrobat** with every markup visible and editable, and a Bluebeam-marked file opens here without losing its markups.

## Phase 4 – Round-trip hardening (½ day)
Test with real files: drawings exported from Revit, drawings already marked up in Bluebeam, scanned sheets, and password-protected PDFs (show a clear error message, don't crash). Fix whatever breaks.

**Done.** A survey opened, saved and reopened 988 local PDFs, 50 of which carry 3,509 Bluebeam markups. It found and fixed:
- **Saving rewrote every markup.** It dropped Bluebeam's rich text, subject, fill, columns and replies. Now unchanged markups are saved exactly as they were. Changed ones are updated in place, keeping their ID, author, replies and unknown keys and dropping only the stale rich text. Deleted ones are removed together with their pop-ups and replies.
- **Bluebeam markups looked different on screen.** No text wrapping, fills or fonts. Now unchanged markups show their own appearance from the file. PocketPDF draws a markup itself only once it's changed, and the page is then redrawn without the file's version.
- **Password-protected files.** A file that needs a password to open gives a clear message. A file only protected against changes opens view-only.
- **Damaged files** give "isn't a valid PDF, or it is damaged" instead of an internal error.

Still open: a Bluebeam text box you edit is redrawn in PocketPDF's simpler style, with no word wrap or fill, until Bluebeam redraws it.

## Phase 5 – Overlay compare, same page size (1 day)
1. Choose "Compare with…" and pick the old revision.
2. Render both pages at the same DPI (default 150).
3. **Mix pixel by pixel:** for each pixel, `dark in old only` → red, `dark in new only` → blue, `dark in both` → black, otherwise white. Use a darkness threshold rather than exact pixel values, because anti-aliasing blurs the edges of lines.
4. Show the result in the same viewer, with zoom and pan, and toggles for the old layer, new layer and combined view.

**Done when:** two revisions of a sample sheet show clean red and blue changes, and there's no red-blue fringing on lines that haven't changed.

**Done.** Two decisions differ from the plan above:
- **No darkness threshold.** Each pixel's lightness feeds straight into the colour channels: red = new, blue = old, green = the darker of the two. Line edges stay smooth, and there's nothing to tune.
- **Markups are left out.** Both pages are drawn without annotations, so markups never show up as changes. The markup layer is hidden while comparing.

Compare renders at the current zoom, through the same pipeline as normal viewing, so it stays sharp. On a generated A1 pair, the moved, added and removed lines came out red and blue, with zero coloured pixels where nothing changed, at both 26% and 100%.

## Phase 6 – Point alignment (½–1 day)
1. Click 2 matching points on each version (grid intersections work well).
2. Work out the scale and offset. Add rotation only if a real file needs it.
3. Shift the old bitmap before mixing.

**Done when:** an old revision exported at a different page offset lines up.

**Done.** While comparing, click **Align…**. The app shows the old revision: click two matching points. Then it shows the new one: click the same two. Esc cancels.
- **Scale and offset only, no rotation.** The scale comes from the distance between the points. The offset matches their midpoints, so a small click error is split between both points.
- **Smooth shifting.** The old page is shifted with bilinear sampling. Rounding to whole pixels would put red/blue fringes back along every line.
- **Results on generated pairs.** One old sheet was exported on a larger page and shifted by (37, 22) pt. Another was shifted and scaled to 97%. Before aligning, 3,000–13,000 pixels in the unchanged area showed strong red or blue. After aligning, none did, at 26% and 100% zoom. Thin lines keep a faint navy tint, because a resampled old line is slightly softer than the new one.
- **Limits.** One alignment covers all pages. Starting a new compare clears it.

## Phase 7 – Auto-clouds (1 day)
1. Build a diff mask from the red and blue pixels.
2. Dilate the mask so nearby changes join up, then flood fill to find connected regions and take their bounding boxes. Drop tiny regions as noise.
3. Turn each box into a Cloud markup in the existing Phase 2/3 model, so it saves for free.

**Done when:** auto-clouds land on the real changes and you can edit or delete them like hand-drawn clouds.

**Done.** While comparing, click **Auto-cloud**. It clouds every change on the current page, in the colour picked in the toolbar. The clouds are ordinary Cloud markups, so you edit, delete and save them as usual. They stay visible over the overlay so you can check them.
- **How changes are found:** the overlay is rendered at a fixed 150 dpi, whatever the zoom. A pixel counts as changed when its red and blue differ by more than 100 out of 255.
- **Why not dilate and flood fill:** instead, the page is cut into 12 pt cells, and touching cells with changes are grouped. Changes up to about 12–24 pt apart get one cloud. This does the same job much faster.
- **Noise and padding:** groups with less than 1 square point of changed ink are dropped. Clouds sit 8 pt outside the changed lines, and clouds that overlap are merged.
- **Why these numbers:** at 100 dpi, or with a colour difference of 80, the result was either a missed digit change or clouds running the full length of thin grid lines (from the faint tint left after aligning). The numbers above caught all 5 changes on the generated pair with no false clouds: a moved beam (two clouds, old and new position), a removed box, an added circle, and 3500 → 3600 in 7 pt text. A shifted, 97% scaled pair also gave no false clouds.
- **Known gaps:** clicking Auto-cloud twice adds the clouds twice. It covers only the current page.

## Phase 8 – Overlay print and export (½ day)
1. Export the overlay as a new one-page PDF: the overlay image plus a legend ("Red = Rev P1, Blue = Rev P2").
2. Print by sending that PDF to the printer, rather than writing WPF print code.

**Done when:** the printed overlay is readable at A3.

**Done.** While comparing, click **Export…**. It saves the current view (Both, Old only or New only, aligned) as a one-page PDF at the drawing's own sheet size, with a legend strip below it. The legend shows Old in red, New in blue, Unchanged in black, the file names, the page and the date.
- **Image and file size:** the overlay is a 150 dpi image, so an A1 printed on A3 comes out at 300 dpi. A generated A1 export was about 200 KB.
- **Legend size:** the legend scales with the sheet, at 22 pt on A1, so it's still readable at half size.
- **Printing:** after saving, PocketPDF offers to open the PDF in the default viewer, and you print from there, which already handles printers and fit-to-page. That's simpler than writing WPF print code.
- **Not included:** markups and auto-clouds aren't in the export, only the overlay.

## Phase 9 – Undo / redo (½ day)
Plan and prompt: [plans/09-undo-redo.md](plans/09-undo-redo.md).

**Done.** Ctrl+Z undoes, and Ctrl+Y or Ctrl+Shift+Z redoes. There are also Undo and Redo buttons next to Save As.
- **How it works:** after every change, the app keeps a copy of that page's markups. Undo puts the previous copy back. No action needs its own undo code, so new tools are undoable for free as long as they raise `MarkupsChanged`.
- **What counts as one step:** each add, move, restyle and delete. One Auto-cloud click is one step, however many clouds it adds. The app keeps up to 100 steps.
- **Other pages:** undoing a change on another page jumps to that page first.
- **Markups from other apps:** undoing a change to a Bluebeam markup makes it count as unchanged again. The page then shows Bluebeam's own appearance, and saving leaves the annotation untouched. This needed a fix: the code that works out which file markups to hide now matches them by annotation, as saving already did, instead of by object.
- **Unsaved changes:** undoing back to the last save (or to the opened file) clears the `*` in the title.
- **Text being typed:** while the text box has focus, Ctrl+Z is the text box's own undo. Clicking Undo first finishes the text, as its own step.
- **Checked:** 6 new unit tests. In the app, a highlighter stroke undid and redid, and moving a file note then undoing put it back in the file's appearance with the `*` cleared.
- **Limits:** Ctrl+Z while placing cloud corners cancels the whole cloud instead of removing the last corner. Zoom, page changes and compare state aren't undoable.

## Phase 10 – Edit markups after placing them (1 day)
Plan and prompt: [plans/10-edit-markups.md](plans/10-edit-markups.md).

**Built; app check still to finish.** With the Select tool:
- **Edit text:** double-click a text or callout, or select it and press F2. The text box opens with its text. Enter keeps the change, Esc leaves it as it was, and clearing all the text deletes the markup. Confirming without changing anything leaves a Bluebeam markup untouched.
- **Grips:** a selected cloud shows a square on each corner, and a selected callout shows one on its arrow tip and one on its label. Drag a grip to move just that point. On a selected cloud, Ctrl+click an edge to add a corner there (it can be dragged straight away), and Ctrl+click a corner to remove it (a cloud keeps at least 3). The grips stay 8 px on screen at any zoom.
- **Text size:** a new picker (8–24 pt) next to the line weight. It sets the size for new text and callouts, and for the selected one.
- **Pickers show the selection:** selecting a markup sets the colour, weight and size pickers to its values. A colour the picker doesn't list (a Bluebeam red, for example) leaves the picker as it was. Each picker now changes only its own property. Before, changing the line weight also set the colour, which recoloured Bluebeam markups.
- **Each edit is one undo step:** a drag counts once, on mouse-up.
- **Bugs fixed along the way:**
  - Clicking on the letters of a label didn't select it, because only the box around the text was marked as part of the markup.
  - A plain click on a markup counted as a move of zero distance, because WPF sends a mouse move when the click starts. That added an empty undo step and marked the file as changed.
- **Checked:** 62 unit tests pass, including the new edge-point helper. In the app, on a file with markups made elsewhere:
  - Clicking the letters of a note selects it, and the colour picker switches to its colour.
  - Selecting the callout shows its two grips, and dragging the arrow-tip grip moves only the tip.
  - On a cloud from the file: dragging a corner works, Ctrl+drag on an edge adds a corner, and Ctrl+click removes it again. The file's own drawing of the cloud is hidden once it's changed.
  - Undoing all of it brought back the file as opened, with no `*`.
- **Still to check by hand:** F2 and double-click text editing (the scripted run lost the window to another app at that point), the text size picker, and a saved edit opening in Bluebeam.
- **Not included:** rotating, resizing text boxes, rich text, selecting more than one markup.

## Phase 11 – Modern interface (1 day)
Plan and prompt: [plans/11-modern-ui.md](plans/11-modern-ui.md).

**Done.** The app uses WPF's built-in Windows 11 (Fluent) look, with no UI library added. It follows Windows' light or dark setting. One line in `App.xaml` (`ThemeMode="System"`) turns it on, and warning WPF0001 is switched off in the project, because Microsoft still marks the theme experimental.
- **Toolbar in groups:** File (Open, Save As) | Undo, Redo | the five tools | colour, line weight, text size | page list, Fit | Compare… The tools are icon-only toggle buttons with the active one in the accent colour, and their tooltips give the shortcut.
- **Shortcuts:** V Select, T Text, C Callout, K Cloud, H Highlight. Also Ctrl+O Open and Ctrl+0 Fit. They don't fire while you're typing in a text box.
- **Compare bar:** while comparing, a tinted strip under the toolbar says "Comparing", with the legend, the Both/Old/New view, Align…, Auto-cloud, Export… and Stop compare. Before, these were squeezed into the toolbar.
- **Page thumbnails** replace the list of page numbers. A thumbnail is drawn only when it scrolls into view, one at a time, so a long set opens as fast as before. The list follows PgUp/PgDn, and the page list button hides it.
- **Status bar:** the page ("Page 3 / 40"), the zoom (type a percentage and press Enter), and the cursor position on the sheet in mm from its bottom-left corner.
- **Start screen:** "Open a PDF or drop one here", with the last 10 files. A file that has since been moved says so and drops off the list.
- **Remembered** in `%AppData%\PocketPDF\settings.json`: window size and position (only if still on a screen), maximised, page list shown, last folder, recent files. A missing or damaged file just means defaults.
- **The drawing stays white** in dark mode. The area around it, the borders and the text use theme colours.
- **App icon:** a drawing sheet with a red revision cloud on a blue tile. `img/make-icon.ps1` draws it at 16–256 px and writes `src/PocketPDF/app.ico` and `img/icon.png`. Below 32 px it leaves out the grid lines, so it still reads.
- **Checked:** 62 unit tests pass. Screenshots of the running app (captured without clicking or typing) showed the start screen, the recent list, the window size restored from settings, and an open file with thumbnails, status bar and grouped toolbar, all in light mode.
- **Not checked:** dark mode (it would have meant changing your Windows setting), the compare bar, the zoom box and shortcuts in use, and speed on a 100-page set (the sample has 40).
- **Known limit:** below about 1100 px wide, the last toolbar buttons (Fit, Compare…) move into the toolbar's overflow menu (»).

## Phase 12 – Select and copy PDF text (1 day)
Plan and prompt: [plans/12-text-selection.md](plans/12-text-selection.md).

**Built; app check still to do by hand.** A new **Select text** tool (shortcut S, I-beam cursor):
- Drag over text to select it, in reading order. Double-click selects a word, and Ctrl+A selects the whole page.
- Ctrl+C copies, or right-click → **Copy**. Right-click → **Highlight selection** turns each selected line into a yellow highlighter stroke, as one undo step, and saves like any highlight.
- If a page has no selectable text (CAD text drawn as lines, or a scan), the status bar says so.
- **How:** the text comes from PDFium through Docnet (`PdfRenderer.GetText`), read only while the tool is on, in the background, once per page. Docnet gives character boxes in whole pixels, so it reads at 4× to get boxes to a quarter point, fine enough for 2 mm drawing text. PDFium adds its own spaces between words drawn apart and line breaks at line ends, so copied text needs no gap guessing.
- **Checked:**
  - 9 new unit tests on generated PDFs: reading order, line breaks, a word drawn apart from the next, the nearest character, words, line rectangles, a page without text, and the character boxes landing where `PageGeometry` puts the text at /Rotate 0, 90, 180 and 270.
  - On `data/phase3-sample-marked.pdf`, the cover page reads as clean lines of text (2,281 characters in 26 ms), and the contents page in 16 ms.
- **Not checked in the app:** dragging a selection, copying, and highlighting. Scripted clicks are off while the machine is in use.
- **Limits:**
  - The tool doesn't work on files that opened view only, because the markup layer is switched off there.
  - Highlighting a line that runs across a gap (two text blocks on one line) covers the gap too.
- **Next on the same data:** Ctrl+F find.

## Phase 13 – Combine PDFs from Explorer (1 day)
Plan and prompt: [plans/13-combine-pdfs.md](plans/13-combine-pdfs.md).

**Built; the Explorer menu itself still to try by hand.** Select PDFs in Explorer, right-click → **Combine PDFs** (on Windows 11 under "Show more options"). A window lists the files. Order them, check the name (`Binder_2026-11-20.pdf`, or `(2)`, `(3)`… if taken) and press Enter. The binder goes in the first file's folder. Afterwards: **Open**, **Show in folder** or **Close**.
- **Turning it on:** the new **⋯** button at the end of the toolbar → "Add “Combine PDFs” to the Explorer menu" (and "Remove" once it's there). It writes a classic menu entry under `HKCU\Software\Classes\SystemFileAssociations\.pdf\shell\PocketPDF.Combine`: no admin rights, no shell extension. The entry starts the PocketPDF it was added from, so add it again after moving the app.
- **How the files get together:** Explorer starts one PocketPDF per selected file. The first one holds a named mutex and listens on a named pipe (both named with the user's SID). The others send it their path and exit. It keeps listening while its window is open, so files from a slow Explorer still arrive, and Combine only becomes available once no file has arrived for 0.6 s. A path sent down the pipe is used only if it's an existing `.pdf`.
- **Order:** Explorer doesn't tell programs which file was clicked first. It starts the processes all at once, so they arrive in random order. The list therefore starts in Explorer's own name order (`StrCmpLogicalW`, so S-2 comes before S-10) until you move a file. You can drag, use Move up/down (Alt+↑/↓), Sort by name, or Remove (Delete).
- **Combining (`PdfCombiner`, PDFsharp):** all pages in order, keeping page rotation and markups. Bookmarks are dropped. The file is written next to the target and then moved into place, never over an existing file.
- **Files left out, with the reason shown:** damaged files; files that need a password to open; and files **protected against changes**. PDFsharp would copy those without their protection, which would hand out an unlocked copy of an issued drawing. They already open view-only in PocketPDF for the same reason. If none of the files can be read, nothing is written.
- **Checked:**
  - 6 new unit tests: page order, rotation and markups kept (and read back by PocketPDF), damaged, password and change-protected files skipped, nothing written when nothing is readable, an existing file never overwritten, and the binder name counting up.
  - Starting 3 and then 30 `--combine` processes at once, as Explorer does, gave one window with all the files, in name order.
  - Combining 30 copies of the 40-page sample gave 1,200 pages in 1.3 s, kept all 90 markups, and reopens in PocketPDF.
- **Not checked:** the registry entry and the real Explorer menu (that's your registry to change), and clicking through the window.
- **Later:** the short Windows 11 menu (without "Show more options") needs an `IExplorerCommand` shell extension and a signed app package.

## Phase 14 – Vector snapping and measuring (2–3 days)
Plan and prompt: [plans/14-vector-snap-measure.md](plans/14-vector-snap-measure.md).

**Built; clicking through it in the app still to do by hand.** The page is still shown as PDFium's bitmap. The drawing's line work is read alongside it into a snap index; it isn't redrawn as WPF shapes.
- **Measure tool (M):** click points on the drawing.
  - Double-click or Enter ends it: 2 points make a distance, more make a length along the points.
  - Clicking the first point again closes it as an area.
  - While drawing, the status bar shows the distance or length so far, and what the area would be if closed now.
  - A placed measurement shows its value on a white label. You can select, move, drag each point by its grip, restyle, undo and save it like any markup.
- **Snapping:** with the Measure, Cloud and Callout tools, and while calibrating, points snap within 10 screen pixels. A green symbol shows what's caught: square = end point, X = crossing, triangle = midpoint, hourglass = on a line. Shift locks to straight across or up from the last point, and Alt turns snapping off. Grip drags don't snap.
- **Scale:** the status bar's **Scale** button lists 1:1 to 1:2500, plus **Calibrate…**: click both ends of a known length, then type its real length in mm. Each page keeps its own scale. A measurement keeps the scale it was made at, as in Bluebeam, so changing the page scale later doesn't change it.
- **In the file:**
  - Measurements are saved as Bluebeam's types, each with a `/Measure` scale and a drawn appearance: `/Line /LineDimension` (distance), `/PolyLine /PolyLineDimension` (length) and `/Polygon /PolygonDimension` (area). Ones from other apps whose units PocketPDF knows (mm, cm, m, in, ft…) can be edited. Others are left untouched.
  - The page scale is saved as a page viewport (`/VP`) and read from one, Bluebeam's included. A Bluebeam 1/8" = 1'-0" reads as 1:96.
- **How the line work is read (`VectorExtractor`):** Docnet doesn't wrap PDFium's page-object functions, so they're called directly in the same `pdfium.dll`, under the existing PDFium lock. It walks every path, including those in nested form XObjects, multiplying their matrices. Curves are split into 8 straight pieces. Text and images are left out. It stops at 1,000,000 segments and says so.
- **Snap index (`SnapIndex`):** a 16 pt grid of cells. A lookup tries end points first, then crossings (of at most the 150 nearest lines, so hatching can't stall it), then midpoints, then the nearest point on a line.
- **Checked:**
  - 14 new unit tests. Lines, rectangles, curves, `cm` transforms and scaled form XObjects come out in PDF space at /Rotate 0 and 90. Each snap kind, its priority, and long diagonal lines. Scales from ratios, calibration and feet. Measurement values. Measurements and the page scale saved and read back unchanged.
  - On a real A1 sheet (30,154 segments): reading the line work 36 ms, building the index 12 ms, one snap lookup 0.02 ms (0.2 ms with the wide radius you get at 10% zoom). The targets were 1 s and 1 ms.
  - Measurements saved into a sample file show in the app as saved, with "Scale 1:50" read back in the status bar.
- **Not checked:** clicking, snapping and calibrating in the app (no scripted input while the machine is in use), Bluebeam reading the saved measurements and scale (no Bluebeam here), and very dense sheets near the segment limit.
- **Limits:**
  - A page that already has viewports (Bluebeam's) keeps them as they are, so a scale set on it in PocketPDF isn't saved to the file. Its measurements still carry their own scale.
  - Units are mm and m² only.
  - Grip drags don't snap.

---

## Later, only if needed
- Find text (Ctrl+F), using the page text from phase 12.
- "Combine PDFs" in the short Windows 11 right-click menu: needs an `IExplorerCommand` shell extension and a signed sparse app package.
- More markup types (line, freehand ink, rectangle, stamp).
- Linking to IssueBuddy or Revit. Keep the markup model and the PDF read/write code free of WPF types, so they can move into a shared library when that day comes.
- Undo/redo, editing placed markups, a modern interface, text selection, Combine PDFs from Explorer, and vector snapping with measuring are now planned as phases 9–14. See [plans/README.md](plans/README.md): each has a plan and a ready-to-paste build prompt.

## Known risks
| Risk | Mitigation |
|---|---|
| Missing `/AP` streams make markups invisible in Bluebeam or print | Write `/AP` in Phase 3; test in Bluebeam before moving on |
| Rotated pages put markups in the wrong place | Unit-test the coordinate helper with `/Rotate` 0/90/180/270 |
| Large A0 renders use too much memory | Cap the render size and render only the visible pages |
| PDFium crashes if called from several threads | One renderer class with a lock |
