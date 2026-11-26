# Phase 11 – Modern interface (1 day)

## Plan

**Use the Fluent theme built into WPF on .NET 9+, not a third-party UI library.** Setting `ThemeMode="System"` on the `Application` gives Windows 11 styling for every standard control: rounded corners, Mica backdrop, accent colour, and light/dark following Windows. It's one attribute, and there's no new dependency. (It's flagged as experimental: suppress warning `WPF0001` in the csproj.)

Then tidy the layout, which matters more than styling:

1. **Command bar in groups.** File (Open, Save As) | Edit (Undo, Redo) | Tools (Select, Text, Callout, Cloud, Highlight, then later Text-select and Measure) | Style (colour, weight, font size) | Compare. Icon-only buttons for tools, with tooltips that include the shortcut. Icons with labels for File and Compare. Keep the Segoe Fluent glyphs already used.
2. **Keyboard shortcuts for tools:** V Select, T Text, C Callout, K Cloud, H Highlight. Show them in tooltips.
3. **Compare as its own bar.** When comparing, show the compare controls in a coloured strip under the command bar (an "info bar"), not squeezed into the toolbar. This makes it obvious the app is in compare mode.
4. **Page panel with thumbnails.** Replace the `ListBox` of numbers with small page thumbnails (render at ~0.1 scale, off the UI thread, through the existing `PdfRenderer` lock). Show the page number under each. Let the user collapse the panel.
5. **Status bar** at the bottom: page "3 / 12", zoom (click to type a value), cursor position, and later the measure readout and calibrated scale.
6. **Colours from the theme.** The canvas background (`#505050` today) and borders use theme brushes so dark mode looks right. The page itself stays white. A drawing is a drawing.
7. **Window remembers** its size, position and the last folder (in `%AppData%\PurpleStreamPDF\settings.json`).
8. **Recent files** on the empty start screen: "Open a PDF or drop one here", plus the last 10 files.

**Skipped:** custom theming, a ribbon, tabs for several documents. Name these as later options.

**Done when:** the app looks native next to Windows 11 Explorer in both light and dark mode. Every tool can be reached by keyboard. Thumbnails don't slow down opening a 100-page set. Nothing that worked before is broken (re-run the manual checks from phases 1–8).

## Prompt

```
Modernise the PurpleStreamPDF interface (WPF, .NET 10, Windows 11). Read PLAN.md, src/PurpleStreamPDF/MainWindow.xaml and MainWindow.xaml.cs first.

Use WPF's built-in Fluent theme: ThemeMode="System" on App.xaml's Application. Suppress WPF0001 in PurpleStreamPDF.csproj. Do not add a UI library (no ModernWpf, WPF-UI, MahApps). Use theme brushes (DynamicResource) instead of hard-coded colours, except the page itself, which stays white.

Layout changes in MainWindow.xaml:
1. Group the toolbar: File (Open, Save As) | Edit (Undo, Redo, if they exist) | Tools (the MarkupTool radio buttons, icon-only with tooltips that show shortcuts) | Style (colour, line weight, font size pickers) | Compare… Keep the existing Segoe Fluent glyphs and the Icon style. Keep every existing x:Name and event handler working.
2. Single-key tool shortcuts when no TextBox has focus: V Select, T Text, C Callout, K Cloud, H Highlight. Handle them in OnPreviewKeyDown.
3. Move CompareControls into an info-bar strip below the toolbar, visible only while comparing (accent-tinted background, the legend text, then Both/Old/New, Align…, Auto-cloud, Export…, Stop compare).
4. Replace the PageList numbers with thumbnails: render each page at about 120 px wide via PdfRenderer.Render (it already locks PDFium), in a background task, lazily as items scroll into view (VirtualizingStackPanel). Show the page number under each. Add a button to collapse the panel. A 100-page file must open as fast as before.
5. A StatusBar: "Page 3 / 12", zoom percentage (an editable box: typing 150 sets 150%), and the cursor position in mm on the sheet (use PageGeometry.ToPdf, 1 pt = 25.4/72 mm).
6. An empty-state panel when no file is open: "Open a PDF or drop it here", plus up to 10 recent files (click to open).
7. Save window size, position, last folder, recent files and panel state in %AppData%\PurpleStreamPDF\settings.json with System.Text.Json. Ignore a missing or bad file.

Keep logic out of XAML code-behind where Core can hold it (settings and the recent-files list are fine in the app project, as they're UI concerns). Run `dotnet build` and `dotnet test`. Launch the app in light and dark Windows mode, take screenshots, and check every phase 1–8 feature still works: open, zoom/pan, every markup tool, save, compare, align, auto-cloud, export. Add a "Phase 11 … **Done.**" section to PLAN.md.
```
