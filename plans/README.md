# Next features – plans and prompts

Each file holds a **plan** (what to build, why this way, done-when) and a **prompt** you can paste into a fresh Claude Code session to build it. The prompts are self-contained: they name the files, the existing pieces to reuse, and the checks to run.

Suggested order. Each step builds on the one before, so later prompts assume earlier ones are done.

| # | Feature | Size | Depends on |
|---|---|---|---|
| 9 | [Undo / redo](09-undo-redo.md) **(done)** | ½ day | – |
| 10 | [Edit markups after placing them](10-edit-markups.md) **(built, app check pending)** | 1 day | 9 (so edits are undoable) |
| 11 | [Modern interface](11-modern-ui.md) **(done)** | 1 day | – (do before 12–14 so their buttons land in the new layout) |
| 12 | [Select and copy PDF text](12-text-selection.md) **(built, app check pending)** | 1 day | – |
| 13 | [Combine PDFs from Explorer](13-combine-pdfs.md) **(built, Explorer menu check pending)** | 1 day | – (fully independent; can run in parallel with anything) |
| 14 | [Vector snapping and measuring](14-vector-snap-measure.md) **(built, app check pending)** | 2–3 days | 9, 11 |

Rules that apply to every prompt (they're repeated inside each one):
- Core logic goes in `src/PurpleStreamPDF.Core` (no WPF types), the UI in `src/PurpleStreamPDF`.
- Every PDFium call goes through the one lock in `PdfRenderer` (PDFium isn't thread-safe).
- PDFsharp stays at 6.2.x (6.5+ is AGPL).
- New logic gets xUnit tests in `tests/PurpleStreamPDF.Core.Tests`; run `dotnet test`.
- When a phase is done, add a "Done." note to `PLAN.md` in the same style as phases 4–8.
