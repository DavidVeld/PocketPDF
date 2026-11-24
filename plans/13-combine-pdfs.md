# Phase 13 – Combine PDFs from Explorer (1 day)

## Plan

**How Explorer hands over the files.** A plain registry verb ("static verb") under `HKCU\Software\Classes\SystemFileAssociations\.pdf\shell\PocketPDF.Combine` needs no admin rights and no COM code. With several files selected, Explorer starts **one PocketPDF process per file**, one after another. So:

1. **Gather the files:** `PocketPDF.exe --combine "<file>"`. The first process takes a named mutex and listens on a named pipe. Later processes send their path down the pipe and exit at once. The first one keeps collecting until no new path has arrived for ~500 ms, then combines.
2. **Order.** Explorer doesn't tell programs which file you clicked first. It launches them in its own order (the right-clicked file first, then the rest roughly in view order). So "order of selection" can't be guaranteed. Instead, show a small **Combine dialog** before writing:
   - The files in the order received, with drag-to-reorder plus Move up/Move down buttons.
   - Name box, prefilled with `Binder_2026-11-20.pdf` (today's date, ISO so they sort). If that name exists, use `Binder_2026-11-20 (2).pdf`.
   - Saved in the folder of the first file.
   - Enter = Combine. So if the order is already right, it's one keypress.
   - After combining: "Open" / "Show in folder" / "Close".
3. **Combining (Core, PDFsharp 6.2):** `PdfReader.Open(path, PdfDocumentOpenMode.Import)` and `output.AddPage(page)` for every page, in order. Keep page rotation and annotations (markups). Bookmarks/outlines are dropped; that's acceptable for drawing sets and gets a note in the plan. A file that's password protected or damaged: skip it, and list it in the result message. Don't fail the whole binder. Write to a temp file, then move it into place, so a crash never leaves half a PDF.
4. **Install the menu entry:** a "Add 'Combine PDFs' to Explorer" toggle in the app (settings or first-run), which writes and removes the HKCU keys. Use `MultiSelectModel = Player` so the verb appears for any number of selected files (the default limit is 15).
5. **Windows 11 note:** classic verbs appear under **"Show more options"** (Shift+F10), not in the short new menu. Getting into the short menu needs an `IExplorerCommand` COM handler plus a signed sparse MSIX package. That's a lot more work. Do the classic verb now, and list the modern menu as a follow-up.

**Done when:** selecting 3 PDFs in Explorer → right-click → Show more options → Combine PDFs opens the dialog with all 3, reordering works, Enter writes `Binder_<date>.pdf` next to them with all pages in order, and markups and rotated pages survive. 30 files at once also works, and a protected file in the selection is skipped with a message.

## Prompt

```
Add "Combine PDFs" to the Windows Explorer right-click menu for PocketPDF (WPF, .NET 10, PDFsharp 6.2.4: do not upgrade, 6.5+ is AGPL). Read PLAN.md first.

1. Core: `PdfCombiner.Combine(IReadOnlyList<string> inputPaths, string outputPath)` returns a result listing the files that were skipped and why. Use PdfReader.Open(path, PdfDocumentOpenMode.Import) and AddPage for every page, in order. Keep /Rotate and annotations. Skip password-protected or unreadable files (reuse the error handling and messages from MarkupFile.Load) instead of failing. Write to a temp file in the target folder, then File.Move with overwrite: false. `DefaultBinderName(folder, DateTime today)`: "Binder_yyyy-MM-dd.pdf", then "Binder_yyyy-MM-dd (2).pdf" and so on if it exists. Unit tests: page count and order, rotation kept, an annotation kept, protected file skipped, name collision. Use TestPdf.cs helpers.
2. App startup (App.xaml.cs; remove StartupUri and open MainWindow in code for the normal case): handle `--combine <path>`. Explorer starts one process per selected file. The first process to get a named Mutex ("PocketPDF.Combine.<user SID>") becomes the collector. It listens on a NamedPipeServerStream, adds its own path, and gathers paths until 500 ms pass with no new one. Others connect with NamedPipeClientStream, send their path, and exit. If connecting fails, they retry briefly (to avoid a race), then become the collector. Normal file opening (one path, no flag) must keep working.
3. CombineWindow: a list of file names (full path in the tooltip) in the order received, drag-to-reorder plus Move up/down and Remove buttons. A name TextBox prefilled with DefaultBinderName in the first file's folder. Combine is the default button (Enter). Run the combine off the UI thread with a progress text. Afterwards: Open (in PocketPDF), Show in folder (explorer /select,), Close. List skipped files if any.
4. Registration: a menu or settings item "Add 'Combine PDFs' to Explorer menu" / "Remove…". It writes to HKCU\Software\Classes\SystemFileAssociations\.pdf\shell\PocketPDF.Combine: (Default)="Combine PDFs", Icon="<exe>,0", MultiSelectModel="Player", and command\(Default)="\"<exe>\" --combine \"%1\"". Use Environment.ProcessPath for <exe>. HKCU only, no admin.

Explorer gives no selection order; say this in the dialog's hint text ("Drag to change the order"). Windows 11 shows classic verbs under "Show more options". Note the IExplorerCommand + sparse package route in PLAN.md "Later"; don't build it.

Run `dotnet build` and `dotnet test`. Test for real: register, select 3 sample PDFs in Explorer, run the verb, reorder, combine. Check the output in PocketPDF. Then try 30 files, and a selection that includes a protected PDF. Add a "Phase 13 … **Done.**" section to PLAN.md.
```
