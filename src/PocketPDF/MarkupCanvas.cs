using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PocketPDF.Core;

namespace PocketPDF;

public enum MarkupTool
{
    Select, Text, Callout, Tag, Cloud, Pen, Highlight, Line, Rectangle, Mask, SelectText, Measure,
    CadLine, CadPolyline, CadRectangle, CadCircle, CadOffset, CadTrim, CadHatch, CadDimension,
}

/// <summary>
/// Transparent drawing layer over one page. Canvas units are view points (top-left origin, 1 unit = 1 pt),
/// so it zooms together with the page. The markup list is the truth; the canvas is redrawn from it.
/// Shapes come from the same Core geometry the PDF writer uses, so the screen matches the saved file.
/// </summary>
public sealed class MarkupCanvas : Canvas
{
    /// <summary>Size of the grips on the selected markup, in screen units, whatever the zoom.</summary>
    const double GripSize = 8;

    /// <summary>
    /// A grip on the selected markup: a cloud's vertex (or its tag, after the vertices), a callout's arrow tip (0) or label (1),
    /// a line's ends or a rectangle's corners (0 and 1).
    /// </summary>
    sealed record Grip(Markup Markup, int Index);

    static readonly Effect SelectionGlow = new DropShadowEffect { Color = Colors.DodgerBlue, ShadowDepth = 0, BlurRadius = 10, Opacity = 1 };
    static readonly FontFamily LabelFont = new(TextLayout.ScreenFontFamily);

    PageGeometry? _page;
    List<Markup> _markups = [];
    MarkupTool _tool;

    // Drawing in progress.
    readonly List<ViewPoint> _cloudVertices = [];
    ViewPoint? _firstPoint; // a line's start, or a rectangle's, box's or circle's first corner
    CloudMarkup? _areaAwaitingTag; // a finished area whose tag hasn't been placed yet
    Markup? _markupBeingTyped; // a callout or tag whose text is being typed; shown meanwhile, added once the text is in
    ViewPoint? _rawMousePosition; // where the mouse really is, before snapping
    List<ViewPoint>? _freehandStroke; // points of the pen or highlighter stroke while the mouse button is held
    ViewPoint _mousePosition;
    ViewPoint? _dragPreviousPosition;
    Grip? _draggedGrip; // while dragging a grip instead of the whole markup
    bool _dragMovedMarkup;
    ViewPoint _dragStart;
    (double X, double Y) _dragOffset; // how far the drag has moved the markups so far, in view space
    bool _dragCopies; // Ctrl: the first move drops copies, and the copies are dragged
    Markup? _deselectIfClicked; // Shift on a selected markup: a click takes it out of the selection, a drag moves the selection
    TextBox? _textEditor;
    Action<string>? _onTextCommitted; // what Enter does with the typed text; null after Esc
    Markup? _editingMarkup; // the markup whose text is being retyped; hidden meanwhile
    readonly List<Markup> _selection = [];
    ViewPoint? _windowStart; // where a selection window was started, while dragging it
    bool _windowToggles; // Shift was held: the window adds what it touches to the selection, or takes it out if already in
    PageText? _pageText;
    (int Start, int End)? _textSelection; // character indexes; Start is where the drag began
    bool _isSelectingText;
    SnapIndex? _snaps;
    SnapPoint? _snapMarker; // what the cursor snaps to right now, shown as a small symbol
    readonly List<ViewPoint> _measurePoints = [];
    readonly List<MeasureMarkup> _measures = []; // the Measure tool's results: shown until Esc or another tool, never saved (CAD dimensions stay)
    // Picking points for the window: calibrating, and clipping or lining up a placed sheet.
    List<ViewPoint>? _picks;
    int _pickCount;
    PickPreview _pickPreview;
    Action<List<ViewPoint>>? _onPicked;
    SnapIndex? _pickSnaps; // snap to these instead of the drawing and markups (a placed sheet's own lines)
    double _screenUnitsPerPoint = 1;
    readonly List<ViewPoint> _cadPoints = []; // a CAD line's start, a polyline's points so far, or a circle's centre
    CadMarkup? _offsetSource; // the entity the Offset tool copies, once picked
    bool _showCadLayer = true;

    public MarkupCanvas()
    {
        Background = Brushes.Transparent; // needed to receive clicks on empty areas
    }

    /// <summary>Raised whenever a markup is added, moved, restyled or deleted.</summary>
    public event Action? MarkupsChanged;

    /// <summary>Raised when a different markup (or none) is selected.</summary>
    public event Action? SelectionChanged;

    /// <summary>The selected markups on this page, in drawing order.</summary>
    public IReadOnlyList<Markup> Selection => _selection;

    /// <summary>The selected markup when exactly one is selected: it shows grips and can be edited. Null otherwise.</summary>
    public Markup? SelectedMarkup => _selection.Count == 1 ? _selection[0] : null;

    /// <summary>True when every markup shown on the page is selected (and there is at least one).</summary>
    public bool IsAllSelected => _selection.Count > 0 && _selection.Count == VisibleMarkups.Count();

    /// <summary>The page's markups, less the CAD entities while the CAD layer is switched off.</summary>
    IEnumerable<Markup> VisibleMarkups => _showCadLayer ? _markups : _markups.Where(markup => markup is not CadMarkup);

    /// <summary>The CAD lines and circles that trim, offset and hatch work with (not hatches or dimensions).</summary>
    IEnumerable<CadMarkup> CadEdges => VisibleMarkups.OfType<CadMarkup>().Where(markup => markup is not (CadHatchMarkup or CadDimensionMarkup));

    /// <summary>Whether the CAD entities are shown (and can be selected). The file keeps them either way.</summary>
    public bool ShowCadLayer
    {
        get => _showCadLayer;
        set
        {
            _showCadLayer = value;
            if (!value) Select(_selection.Where(markup => markup is not CadMarkup));
            Redraw();
        }
    }

    public static bool IsCadTool(MarkupTool tool) =>
        tool is MarkupTool.CadLine or MarkupTool.CadPolyline or MarkupTool.CadRectangle or MarkupTool.CadCircle or MarkupTool.CadOffset or MarkupTool.CadTrim or MarkupTool.CadHatch or MarkupTool.CadDimension;

    /// <summary>How far the Offset tool copies, in real mm, as typed in the status bar.</summary>
    public double OffsetDistanceMm { get; set; } = 200;

    /// <summary>
    /// The size of the CAD shape being drawn, up to the cursor, in real mm: a line's length (and 0), a rectangle's width
    /// and length, or a circle's radius (and 0). The window shows it where the user can type over it.
    /// </summary>
    public event Action<double, double>? CadSizeChanged;

    double? _typedFirstMm, _typedSecondMm;

    /// <summary>
    /// A size typed in the status bar, in real mm: a CAD line's length, or a rectangle's width. The preview and the next
    /// click use it, towards the cursor. Null follows the cursor.
    /// </summary>
    public double? TypedFirstMm
    {
        get => _typedFirstMm;
        set
        {
            _typedFirstMm = value;
            if (_cadPoints.Count > 0) Redraw();
        }
    }

    /// <summary>A CAD rectangle's height typed in the status bar, in real mm, as <see cref="TypedFirstMm"/>.</summary>
    public double? TypedSecondMm
    {
        get => _typedSecondMm;
        set
        {
            _typedSecondMm = value;
            if (_cadPoints.Count > 0) Redraw();
        }
    }

    /// <summary>Shift or Ctrl went down or up: the point under the cursor snaps differently, though the mouse didn't move.</summary>
    public void RefreshCursor()
    {
        if (_page == null || _rawMousePosition is not { } raw || _cadPoints.Count == 0) return;
        _mousePosition = UsesSnapping ? Snapped(raw, LastDrawnPoint) : raw;
        Redraw();
    }

    /// <summary>Selects these markups (of this page) instead of the current selection.</summary>
    public void Select(IEnumerable<Markup> markups)
    {
        var selected = markups.Distinct().ToList();
        if (selected.SequenceEqual(_selection)) return;
        _selection.Clear();
        _selection.AddRange(selected);
        SelectionChanged?.Invoke();
        Redraw();
    }

    public void SelectAll() => Select(VisibleMarkups);

    /// <summary>Where the mouse is on the page, or null when it isn't over it.</summary>
    public ViewPoint? MousePosition => IsMouseOver ? _rawMousePosition : null;

    public string CurrentColor { get; set; } = "#E53935";
    public double CurrentLineWidth { get; set; } = 1;
    public double CurrentFontSize { get; set; } = 12;
    /// <summary>How new lines, callouts and tags end: arrow, dot or nothing.</summary>
    public LineEnding CurrentEnding { get; set; } = LineEnding.Arrow;
    /// <summary>The shape new clouds, callouts and tags mark: two corners of a box or circle, or a free polygon.</summary>
    public AreaShape CurrentShape { get; set; } = AreaShape.Box;
    public AreaLineStyle CurrentLineStyle { get; set; } = AreaLineStyle.Cloud;
    /// <summary>Fill of new rectangles and masks, or null for none.</summary>
    public string? CurrentFill { get; set; }
    /// <summary>Font of new text, callouts, tags and measurements, or null for the default.</summary>
    public string? CurrentFont { get; set; }

    /// <summary>Markups whose font can change: text, callouts, tags (also a cloud's), measurements and dimensions.</summary>
    public static bool TakesFont(Markup markup) => markup is TextMarkup or CalloutMarkup or MeasureMarkup or CadDimensionMarkup or CloudMarkup { TextPosition: not null };

    /// <summary>Cloud and callout mark an area; the callout then asks where its text goes. A tag points at a spot instead.</summary>
    bool IsAreaTool => Tool is MarkupTool.Cloud or MarkupTool.Callout;

    /// <summary>Screen units per canvas unit at the current zoom. Keeps the grips the same size on screen.</summary>
    public double ScreenUnitsPerPoint
    {
        get => _screenUnitsPerPoint;
        set
        {
            _screenUnitsPerPoint = value;
            RedrawOverlay(); // grips, snap symbols and sizes keep their size on screen
        }
    }

    public MarkupTool Tool
    {
        get => _tool;
        set
        {
            CancelDrawing();
            _measures.Clear();
            _tool = value;
            Select([]);
            ClearTextSelection();
            Cursor = value == MarkupTool.SelectText ? Cursors.IBeam : value is MarkupTool.CadTrim or MarkupTool.CadOffset ? PickboxCursor : null;
            Redraw();
        }
    }

    public void ShowPage(PageGeometry page, List<Markup> markups)
    {
        CancelDrawing();
        _measures.Clear();
        _page = page;
        _markups = markups;
        Select([]);
        _pageText = null; // the window hands over the new page's text and line work when they're read
        _snaps = null;
        ClearTextSelection();
        Width = page.ViewWidth;
        Height = page.ViewHeight;
        Redraw();
    }

    /// <summary>The current page's text, for the Text-select tool. Null until it has been read.</summary>
    public PageText? PageText
    {
        get => _pageText;
        set
        {
            _pageText = value;
            ClearTextSelection();
            Redraw();
        }
    }

    /// <summary>The selected page text, or null if none is selected.</summary>
    public string? SelectedText =>
        _pageText != null && _textSelection is (int start, int end) ? _pageText.TextOf(start, end) : null;

    /// <summary>Finishes any text being typed, so it is part of the markups (for example before saving).</summary>
    public void FinishTextEditing() => CommitTextEditor();

    // Each style applies on its own, so changing the line weight never recolours a markup in a colour the picker doesn't list.

    /// <summary>Sets the colour for new markups, and for the selected ones.</summary>
    public void ApplyColor(string color)
    {
        CurrentColor = color;
        ChangeSelected(markup => markup is not CadHatchMarkup && markup.Color != color && Do(() => markup.Color = color)); // a hatch is in the fill colour
    }

    /// <summary>Sets the font for new text and measurements, and for the selected ones. The default font is stored as null.</summary>
    public void ApplyFont(string font)
    {
        string? stored = font == TextLayout.ScreenFontFamily ? null : font;
        CurrentFont = stored;
        ChangeSelected(markup => TakesFont(markup) && markup.Font != stored && Do(() => markup.Font = stored));
    }

    /// <summary>Sets the fill for new rectangles, masks and hatches, and for the selected closed shapes and hatches.</summary>
    public void ApplyFill(string? fill)
    {
        CurrentFill = fill;
        ChangeSelected(markup => markup switch
        {
            CadHatchMarkup hatch => fill != null && hatch.Color != fill && Do(() => hatch.Color = fill), // "No fill" leaves a hatch as it is
            IFillable { CanFill: true } shape => shape.FillColor != fill && Do(() =>
            {
                if (shape.FillColor == null) shape.FillPattern = CurrentHatchPattern; // filling an empty shape takes the fill type picked too
                shape.FillColor = fill;
            }),
            _ => false,
        });
    }

    /// <summary>Sets the line weight for new markups, and for the selected ones.</summary>
    public void ApplyLineWidth(double lineWidth)
    {
        CurrentLineWidth = lineWidth;
        ChangeSelected(markup => markup.LineWidth != lineWidth && Do(() => markup.LineWidth = lineWidth));
    }

    /// <summary>Sets the font size for new text and callouts, and for the selected ones.</summary>
    public void ApplyFontSize(double fontSize)
    {
        CurrentFontSize = fontSize;
        ChangeSelected(markup => markup switch
        {
            TextMarkup text => text.FontSize != fontSize && Do(() => text.FontSize = fontSize),
            CalloutMarkup callout => callout.FontSize != fontSize && Do(() => callout.FontSize = fontSize),
            CloudMarkup { TextPosition: not null } cloud => cloud.FontSize != fontSize && Do(() => cloud.FontSize = fontSize),
            _ => false,
        });
    }

    /// <summary>Sets how new leaders and lines end, and changes the selected ones.</summary>
    public void ApplyEnding(LineEnding ending)
    {
        CurrentEnding = ending;
        ChangeSelected(markup => markup switch
        {
            CalloutMarkup callout => callout.Ending != ending && Do(() => callout.Ending = ending),
            LineMarkup line => line.Ending != ending && Do(() => line.Ending = ending),
            CloudMarkup { TextPosition: not null } cloud => cloud.Ending != ending && Do(() => cloud.Ending = ending),
            _ => false,
        });
    }

    public HatchPattern CurrentHatchPattern { get; set; }

    /// <summary>Sets the pattern of new hatches and filled rectangles, and of the selected hatches and closed shapes.</summary>
    public void ApplyHatchPattern(HatchPattern pattern)
    {
        CurrentHatchPattern = pattern;
        ChangeSelected(markup => markup switch
        {
            CadHatchMarkup hatch => hatch.Pattern != pattern && Do(() => hatch.Pattern = pattern),
            IFillable { CanFill: true } shape => shape.FillPattern != pattern && Do(() => shape.FillPattern = pattern),
            _ => false,
        });
    }

    /// <summary>Sets the shape of new areas. An area half drawn in the old shape starts again.</summary>
    public void ApplyShape(AreaShape shape)
    {
        CurrentShape = shape;
        _firstPoint = null;
        _cloudVertices.Clear();
        Redraw();
    }

    /// <summary>Sets the outline of new areas, and of the selected one.</summary>
    public void ApplyLineStyle(AreaLineStyle style)
    {
        CurrentLineStyle = style;
        if (_selection.Count > 0) ChangeSelected(markup => markup is CloudMarkup area && area.Style != style && Do(() => area.Style = style));
        else if (_areaAwaitingTag is { } pending) pending.Style = style;
        Redraw();
    }

    /// <summary>Markups whose text F2 or a double-click edits; a dimension's is its length.</summary>
    static bool HasText(Markup? markup) => markup is TextMarkup or CalloutMarkup or CadDimensionMarkup or CloudMarkup { TextPosition: not null };

    /// <summary>Runs <paramref name="change"/> on each selected markup; it returns whether it changed that one. All of them are one undo step.</summary>
    void ChangeSelected(Func<Markup, bool> change)
    {
        bool changed = false;
        foreach (var markup in _selection) changed |= change(markup);
        if (!changed) return;
        MarkupsChanged?.Invoke();
        Redraw();
    }

    static bool Do(Action action)
    {
        action();
        return true;
    }

    /// <summary>Handles Escape, Delete and F2, and Ctrl+A and Ctrl+C for page text. Returns true if the key was used.</summary>
    public bool HandleKey(Key key)
    {
        if (Tool == MarkupTool.SelectText && Keyboard.Modifiers == ModifierKeys.Control && _pageText is { IsEmpty: false } text)
        {
            if (key == Key.A)
            {
                _textSelection = (0, text.Characters.Count - 1);
                Redraw();
                return true;
            }
            if (key == Key.C && SelectedText is { Length: > 0 })
            {
                CopySelectedText();
                return true;
            }
        }
        if (key == Key.F2 && HasText(SelectedMarkup))
        {
            EditText(SelectedMarkup!);
            return true;
        }
        if (key == Key.Enter && _measurePoints.Count >= 2)
        {
            FinishMeasure(closed: false);
            Redraw();
            return true;
        }
        if (key == Key.Enter && _cadPoints.Count > 0)
        {
            FinishCadLine();
            return true;
        }
        if (key == Key.Escape && IsDrawing)
        {
            CancelDrawing();
            Redraw();
            return true;
        }
        if (key == Key.Escape && _selection.Count > 0)
        {
            Select([]);
            return true;
        }
        if (key == Key.Delete && _selection.Count > 0)
        {
            _markups.RemoveAll(_selection.Contains);
            Select([]);
            MarkupsChanged?.Invoke();
            Redraw();
            return true;
        }
        return false;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_page == null) return;
        e.Handled = true;
        if (_textEditor != null)
        {
            CommitTextEditor(); // first click outside the editor only finishes the text
            return;
        }

        var clickPosition = ToViewPoint(e.GetPosition(this));
        var snapped = UsesSnapping ? Snapped(clickPosition, LastDrawnPoint) : clickPosition;
        if (_cadPoints.Count > 0 && (_typedFirstMm ?? _typedSecondMm) != null)
        {
            // The typed size, in the direction of the click.
            if (Tool is MarkupTool.CadLine or MarkupTool.CadPolyline) snapped = NextCadLinePoint(_cadPoints[^1], snapped, _typedFirstMm);
            else if (Tool == MarkupTool.CadRectangle) snapped = OppositeCorner(_cadPoints[0], snapped);
            (_typedFirstMm, _typedSecondMm) = (null, null);
        }
        if (_picks is { } picks)
        {
            picks.Add(snapped);
            if (picks.Count == _pickCount)
            {
                var onPicked = _onPicked;
                _onPicked = null;
                Redraw(); // what was picked stays on the sheet while the window asks about it
                onPicked?.Invoke(picks);
                if (_picks == picks) CancelPicking(); // unless the window went on to pick more
                Redraw();
                return;
            }
            Redraw();
            return;
        }
        switch (Tool)
        {
            case MarkupTool.Select:
                SelectOrEdit(TagOf(e.OriginalSource), clickPosition, e.ClickCount);
                break;

            case MarkupTool.CadLine when _cadPoints.Count > 0: // two points make the line; the polyline takes more
                if (CloudGeometry.Distance(_cadPoints[0], snapped) >= 0.5) AddMarkup(new CadLineMarkup { Points = [_page.ToPdf(_cadPoints[0]), _page.ToPdf(snapped)] });
                _cadPoints.Clear();
                break;

            case MarkupTool.CadRectangle when _cadPoints.Count > 0:
                AddCadRectangle(_cadPoints[0], snapped);
                _cadPoints.Clear();
                break;

            case MarkupTool.CadPolyline when e.ClickCount == 2:
                FinishCadLine(); // the first click of the double-click already added the last point
                break;

            case MarkupTool.CadPolyline when _cadPoints.Count >= 3 && CloudGeometry.Distance(snapped, _cadPoints[0]) <= SnapRadius:
                FinishCadLine(closed: true); // back on the first point: closed
                break;

            case MarkupTool.CadCircle when _cadPoints.Count > 0:
                AddCadCircle(CloudGeometry.Distance(_cadPoints[0], snapped)); // the second click sets the radius
                break;

            case MarkupTool.CadDimension when _cadPoints.Count == 2:
                AddMarkup(NewDimension(_cadPoints[0], _cadPoints[1], snapped));
                MeasureReadout?.Invoke("Dimension added. Double-click it with the Select tool to type a new length: the shape it ends on changes with it");
                _cadPoints.Clear();
                break;

            case MarkupTool.CadDimension when _cadPoints.Count == 1 && CloudGeometry.Distance(_cadPoints[0], snapped) < 0.5:
                break; // the same point twice measures nothing

            case MarkupTool.CadLine or MarkupTool.CadPolyline or MarkupTool.CadRectangle or MarkupTool.CadCircle or MarkupTool.CadDimension:
                _cadPoints.Add(snapped);
                break;

            case MarkupTool.CadOffset when _offsetSource is { } source:
                _offsetSource = null;
                if (CadGeometry.Offset(source, OffsetDistanceMm / MmPerPoint, _page.ToPdf(clickPosition)) is { } copy) AddCad(copy);
                MeasureReadout?.Invoke("Offset: click the line or circle to copy");
                break;

            case MarkupTool.CadOffset:
                _offsetSource = CadEdgeAt(clickPosition) ?? PdfLineAt(clickPosition);
                MeasureReadout?.Invoke(_offsetSource == null ? "Offset: click a line or circle" : "Offset: click the side to copy it to");
                break;

            case MarkupTool.CadTrim when CadEdgeAt(clickPosition) == null: // empty space: a window, trimming (or extending) every line its edge crosses
                _windowStart = clickPosition;
                _windowToggles = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                CaptureMouse();
                break;

            case MarkupTool.CadTrim when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                Extend(clickPosition);
                break;

            case MarkupTool.CadTrim:
                Trim(clickPosition);
                break;

            case MarkupTool.CadHatch:
                if (CadGeometry.RegionAround(CadEdges, _page.ToPdf(clickPosition)) is { } boundary) AddMarkup(new CadHatchMarkup { Boundary = boundary, Pattern = CurrentHatchPattern });
                else MeasureReadout?.Invoke("Hatch: no closed region here. Close it off with CAD lines first");
                break;

            case MarkupTool.Text:
                _firstPoint = clickPosition; // a click places the text, a drag draws a box for it to wrap in: decided on release
                CaptureMouse();
                break;

            case MarkupTool.Line or MarkupTool.Rectangle or MarkupTool.Mask or MarkupTool.Tag when _firstPoint == null:
                _firstPoint = snapped;
                break;

            case MarkupTool.Line or MarkupTool.Rectangle or MarkupTool.Mask:
                var (first, second) = (_page.ToPdf(_firstPoint!.Value), _page.ToPdf(snapped));
                _firstPoint = null;
                if (CloudGeometry.Distance(_page.ToView(first), snapped) < 0.5) break; // a click on the same spot draws nothing
                AddMarkup(Tool == MarkupTool.Line
                    ? new LineMarkup { Start = first, End = second, Ending = CurrentEnding }
                    : new RectangleMarkup
                    {
                        Corner1 = first, Corner2 = second, FillColor = CurrentFill, HasOutline = Tool == MarkupTool.Rectangle,
                        FillPattern = Tool == MarkupTool.Mask ? HatchPattern.Solid : CurrentHatchPattern, // a hatched mask hides nothing
                    });
                break;

            case MarkupTool.Tag:
                var tag = new CalloutMarkup
                {
                    ArrowTip = _page.ToPdf(_firstPoint!.Value), TextPosition = _page.ToPdf(clickPosition), FontSize = CurrentFontSize,
                    HasBorder = false, Ending = CurrentEnding, Color = CurrentColor, LineWidth = CurrentLineWidth, Font = CurrentFont,
                };
                _firstPoint = null;
                _markupBeingTyped = tag;
                OpenTextEditor(clickPosition, tag.FontSize, tag.Color, tag.Font, "", text =>
                {
                    if (text.Length == 0) return; // a leader without text: nothing to keep
                    tag.Text = text;
                    AddMarkup(tag);
                });
                break;

            case var _ when _areaAwaitingTag is { } area:
                _areaAwaitingTag = null;
                _markupBeingTyped = area;
                area.TextPosition = _page.ToPdf(clickPosition);
                area.FontSize = CurrentFontSize;
                area.Ending = CurrentEnding;
                area.Font = CurrentFont;
                OpenTextEditor(clickPosition, area.FontSize, CurrentColor, area.Font, "", text =>
                {
                    if (text.Length == 0 && area.Style == AreaLineStyle.None) return; // nothing would show
                    if (text.Length == 0) area.TextPosition = null; // no text: keep the area without a tag
                    area.Text = text;
                    AddMarkup(area);
                });
                break;

            case var _ when IsAreaTool && CurrentShape != AreaShape.Polygon && _firstPoint is { } corner:
                _firstPoint = null;
                // Two clicks on one spot: a point for the callout to point at, but nothing to cloud.
                if (Tool != MarkupTool.Cloud || CloudGeometry.Distance(corner, snapped) >= 0.5) FinishArea([corner, snapped]);
                break;

            case var _ when IsAreaTool && CurrentShape != AreaShape.Polygon:
                _firstPoint = snapped;
                break;

            case var _ when IsAreaTool && e.ClickCount == 2:
                if (_cloudVertices.Count >= 3) FinishArea([.. _cloudVertices]); // the first click of the double-click already added the last vertex
                _cloudVertices.Clear();
                break;

            case var _ when IsAreaTool:
                _cloudVertices.Add(snapped);
                break;

            case MarkupTool.Highlight or MarkupTool.Pen:
                _freehandStroke = [clickPosition];
                CaptureMouse();
                break;

            case MarkupTool.SelectText:
                StartTextSelection(clickPosition, e.ClickCount);
                break;

            case MarkupTool.Measure when e.ClickCount == 2:
                FinishMeasure(closed: false); // the first click of the double-click already added the last point
                break;

            case MarkupTool.Measure when _measurePoints.Count >= 3 && CloudGeometry.Distance(snapped, _measurePoints[0]) <= SnapRadius:
                FinishMeasure(closed: true); // back on the first point: an area
                break;

            case MarkupTool.Measure:
                _measurePoints.Add(snapped);
                break;
        }
        Redraw();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_page == null) return;
        var rawPosition = ToViewPoint(e.GetPosition(this));
        // Redrawing the shapes under the cursor makes WPF raise a move without moving: without this, that loops and the clicks lag.
        if (rawPosition == _rawMousePosition) return;
        _rawMousePosition = rawPosition;
        _mousePosition = rawPosition;
        var previousMarker = _snapMarker;
        if (UsesSnapping && _dragPreviousPosition == null) _mousePosition = Snapped(_mousePosition, LastDrawnPoint);
        else _snapMarker = null;
        bool markerMoved = _snapMarker != previousMarker;
        if (Tool == MarkupTool.Measure && _measurePoints.Count > 0) ShowMeasureReadout();
        if (_cadPoints.Count > 0)
        {
            var (from, to) = (_cadPoints[^1], _mousePosition);
            CadSizeChanged?.Invoke(
                (Tool == MarkupTool.CadRectangle ? Math.Abs(to.X - from.X) : CloudGeometry.Distance(from, to)) * MmPerPoint,
                Tool == MarkupTool.CadRectangle ? Math.Abs(to.Y - from.Y) * MmPerPoint : 0);
        }

        if (_isSelectingText && _pageText != null && _textSelection is (int start, int end))
        {
            int under = _pageText.CharacterAt(_mousePosition); // nearest character, however far: dragging past a line end keeps selecting
            if (under >= 0 && under != end)
            {
                _textSelection = (start, under);
                RedrawOverlay();
            }
        }
        else if (_freehandStroke != null)
        {
            // Skip tiny moves: they add points without changing the shape.
            if (CloudGeometry.Distance(_freehandStroke[^1], _mousePosition) >= 1)
            {
                _freehandStroke.Add(_mousePosition);
                RedrawOverlay();
            }
        }
        // Capturing the mouse raises a move to the same spot: that's a click, not a drag.
        else if (_windowStart != null)
        {
            RedrawOverlay(); // the selection window follows the mouse
        }
        else if (_dragPreviousPosition != null && _selection.Count > 0)
        {
            double dx = _mousePosition.X - _dragStart.X, dy = _mousePosition.Y - _dragStart.Y;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) // straight across or up, whichever is further
            {
                if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0;
                else dx = 0;
            }
            if ((dx, dy) != _dragOffset) // capturing the mouse raises a move to the same spot: that's no drag
            {
                if (_dragCopies && !_dragMovedMarkup)
                {
                    var copies = _selection.Select(markup => markup.CopyMapped(point => point)).ToList();
                    _markups.AddRange(copies);
                    Select(copies);
                }
                var from = _page.ToPdf(new ViewPoint(_dragStart.X + _dragOffset.X, _dragStart.Y + _dragOffset.Y));
                var to = _page.ToPdf(new ViewPoint(_dragStart.X + dx, _dragStart.Y + dy));
                if (_draggedGrip is { } grip) MoveGrip(grip, to.X - from.X, to.Y - from.Y);
                else foreach (var markup in _selection) markup.MoveBy(to.X - from.X, to.Y - from.Y);
                _dragOffset = (dx, dy);
                _dragMovedMarkup = true;
                _deselectIfClicked = null;
                RedrawMarkups(_selection);
            }
        }
        else if (IsDrawing || markerMoved)
        {
            RedrawOverlay(); // rubber-band line follows the mouse
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (Tool == MarkupTool.Text && _firstPoint is { } textStart && _page != null)
        {
            _firstPoint = null;
            var box = ViewRect.Around([textStart, ToViewPoint(e.GetPosition(this))]);
            double? wrapWidth = box.Width * _screenUnitsPerPoint >= 8 ? box.Width : null; // dragged: a text box
            var topLeft = wrapWidth != null ? new ViewPoint(box.Left, box.Top) : textStart;
            var textPosition = _page.ToPdf(topLeft);
            double textFontSize = CurrentFontSize;
            ReleaseMouseCapture(); // before the editor takes the focus
            // shortcut: the box only wraps the text as typed (as line breaks); re-editing doesn't rewrap. Store the width on TextMarkup if boxes should resize.
            string? textFont = CurrentFont;
            OpenTextEditor(topLeft, textFontSize, CurrentColor, textFont, "", AddIfNotEmpty(text => new TextMarkup
            {
                TextPosition = textPosition, FontSize = textFontSize, Text = wrapWidth is { } width ? TextLayout.Wrap(text, textFontSize, width, textFont) : text,
            }), wrapWidth);
            Redraw();
            return;
        }
        if (_freehandStroke is { } stroke && _page != null)
        {
            if (stroke.Count == 1) stroke.Add(stroke[0]); // a click without moving leaves a dot
            var points = stroke.Select(_page.ToPdf).ToList();
            AddMarkup(Tool == MarkupTool.Pen ? new PenMarkup { Points = points } : new HighlightMarkup { Points = points });
            _freehandStroke = null;
            Redraw();
        }
        if (_windowStart is { } windowStart)
        {
            _windowStart = null;
            var window = ViewRect.Around([windowStart, ToViewPoint(e.GetPosition(this))]);
            if (Math.Max(window.Width, window.Height) * _screenUnitsPerPoint < 3) { } // a click on empty space
            else if (Tool == MarkupTool.CadTrim) TrimInWindow(window, extend: _windowToggles || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            else SelectInWindow(window, _windowToggles);
            Redraw();
        }
        if (_dragMovedMarkup)
        {
            MarkupsChanged?.Invoke(); // once per drag, so a drag is one undo step
            Redraw(); // the dragged markups back in their drawing order
        }
        if (_deselectIfClicked is { } clickedOff) Select(_selection.Where(selected => selected != clickedOff));
        _deselectIfClicked = null;
        _dragMovedMarkup = false;
        _dragPreviousPosition = null;
        _draggedGrip = null;
        _isSelectingText = false;
        ReleaseMouseCapture();
    }

    /// <summary>
    /// Windows took the mouse mid-gesture (Alt+Tab, a pop-up): end the drag, stroke or window as a release would, or it
    /// carries on with no button held. A real release clears all this before letting go, so then nothing is left to do.
    /// </summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragPreviousPosition != null || _freehandStroke != null || _windowStart != null || _isSelectingText)
            OnMouseLeftButtonUp(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left));
    }

    // ---- Selecting and editing ----------------------------------------------------------------

    /// <summary>
    /// A click with the Select tool on a grip, a markup or empty space (<paramref name="clicked"/> null).
    /// Shift+click adds a markup to the selection or takes it out; a drag from empty space selects with a window.
    /// </summary>
    void SelectOrEdit(object? clicked, ViewPoint clickPosition, int clickCount)
    {
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control, shift = Keyboard.Modifiers == ModifierKeys.Shift;
        if (clicked is Grip grip)
        {
            if (ctrl && grip.Markup is CloudMarkup { Shape: AreaShape.Polygon, Vertices.Count: > 3 } cloudToTrim && grip.Index < cloudToTrim.Vertices.Count)
            {
                cloudToTrim.Vertices.RemoveAt(grip.Index);
                MarkupsChanged?.Invoke();
            }
            else StartDrag(clickPosition, grip);
            return;
        }

        var markup = clicked as Markup;
        if (ctrl && markup is CloudMarkup { Shape: AreaShape.Polygon } cloud && markup == SelectedMarkup)
        {
            var (edgeIndex, pointOnEdge) = CloudGeometry.NearestPointOnEdges(cloud.Vertices.Select(_page!.ToView).ToList(), clickPosition);
            cloud.Vertices.Insert(edgeIndex + 1, _page.ToPdf(pointOnEdge));
            StartDrag(clickPosition, new Grip(cloud, edgeIndex + 1));
            _dragMovedMarkup = true; // a new vertex is a change even if it isn't dragged
            return;
        }
        if (shift && markup != null)
        {
            // A click adds the markup to the selection or takes it out; a drag moves the selection straight across or up.
            if (_selection.Contains(markup)) _deselectIfClicked = markup;
            else Select(_markups.Where(m => m == markup || _selection.Contains(m)));
            StartDrag(clickPosition, grip: null);
            return;
        }
        if (clickCount == 2 && HasText(markup))
        {
            EditText(markup!);
            return;
        }
        if (markup == null)
        {
            if (!shift) Select([]);
            _windowStart = clickPosition;
            _windowToggles = shift;
            CaptureMouse();
            return;
        }
        if (!_selection.Contains(markup)) Select([markup]); // a click on one of several selected keeps them all, to drag together
        StartDrag(clickPosition, grip: null, copies: Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); // Ctrl (+Shift): drag a copy
    }

    /// <summary>
    /// Selects every markup the window touches: inside it or crossing its edge. With <paramref name="toggle"/> (Shift),
    /// those it touches swap: selected ones drop out, the others are added, and the rest of the selection stays.
    /// </summary>
    void SelectInWindow(ViewRect window, bool toggle)
    {
        var touched = new HashSet<Markup>();
        VisualTreeHelper.HitTest(this, null, result =>
        {
            if (TagOf(result.VisualHit) is Markup markup) touched.Add(markup);
            return HitTestResultBehavior.Continue;
        }, new GeometryHitTestParameters(new RectangleGeometry(new Rect(window.Left, window.Top, window.Width, window.Height))));
        Select(_markups.Where(markup => toggle ? touched.Contains(markup) != _selection.Contains(markup) : touched.Contains(markup)));
    }

    /// <summary>Shift held while dragging keeps the move straight across or up; <paramref name="copies"/> drags copies instead.</summary>
    void StartDrag(ViewPoint position, Grip? grip, bool copies = false)
    {
        _dragPreviousPosition = position;
        _dragStart = position;
        _dragOffset = (0, 0);
        _dragCopies = copies && grip == null;
        _draggedGrip = grip;
        _dragMovedMarkup = false;
        CaptureMouse();
    }

    void MoveGrip(Grip grip, double deltaX, double deltaY)
    {
        switch (grip.Markup)
        {
            case PlacedSheetMarkup sheet: // a corner grip scales it about the opposite corner, keeping its shape
            {
                var visible = sheet.Visible(_page!);
                var (topLeft, bottomRight) = (_page!.ToPdf(new ViewPoint(visible.Left, visible.Top)), _page.ToPdf(new ViewPoint(visible.Right, visible.Bottom)));
                var (moved, anchor) = grip.Index == 0 ? (topLeft, bottomRight) : (bottomRight, topLeft);
                double x = moved.X - anchor.X, y = moved.Y - anchor.Y;
                double scale = Math.Max(0.02, ((x + deltaX) * x + (y + deltaY) * y) / (x * x + y * y)); // along the diagonal; never flipped
                sheet.Map(point => new(anchor.X + (point.X - anchor.X) * scale, anchor.Y + (point.Y - anchor.Y) * scale));
                break;
            }
            case CloudMarkup cloud when grip.Index == cloud.Vertices.Count:
                cloud.TextPosition = new(cloud.TextPosition!.Value.X + deltaX, cloud.TextPosition.Value.Y + deltaY);
                break;
            case CloudMarkup cloud:
                cloud.Vertices[grip.Index] = new(cloud.Vertices[grip.Index].X + deltaX, cloud.Vertices[grip.Index].Y + deltaY);
                break;
            case LineMarkup line when grip.Index == 0:
                line.Start = new(line.Start.X + deltaX, line.Start.Y + deltaY);
                break;
            case LineMarkup line:
                line.End = new(line.End.X + deltaX, line.End.Y + deltaY);
                break;
            case RectangleMarkup rectangle when grip.Index == 0:
                rectangle.Corner1 = new(rectangle.Corner1.X + deltaX, rectangle.Corner1.Y + deltaY);
                break;
            case RectangleMarkup rectangle:
                rectangle.Corner2 = new(rectangle.Corner2.X + deltaX, rectangle.Corner2.Y + deltaY);
                break;
            case MeasureMarkup measure:
                measure.Points[grip.Index] = new(measure.Points[grip.Index].X + deltaX, measure.Points[grip.Index].Y + deltaY);
                break;
            case CalloutMarkup callout when grip.Index == 0:
                callout.ArrowTip = new(callout.ArrowTip.X + deltaX, callout.ArrowTip.Y + deltaY);
                break;
            case CalloutMarkup callout:
                callout.TextPosition = new(callout.TextPosition.X + deltaX, callout.TextPosition.Y + deltaY);
                break;
            case CadLineMarkup line:
                line.Points[grip.Index] = new(line.Points[grip.Index].X + deltaX, line.Points[grip.Index].Y + deltaY);
                break;
            case CadCircleMarkup circle when grip.Index == 0:
                circle.Center = new(circle.Center.X + deltaX, circle.Center.Y + deltaY);
                break;
            case CadCircleMarkup circle: // the grip on its edge sets the radius
                circle.Radius = CadGeometry.Distance(circle.Center, new(circle.Center.X + circle.Radius + deltaX, circle.Center.Y + deltaY));
                break;
            case CadDimensionMarkup dimension when grip.Index == 0:
                dimension.Start = new(dimension.Start.X + deltaX, dimension.Start.Y + deltaY);
                break;
            case CadDimensionMarkup dimension when grip.Index == 1:
                dimension.End = new(dimension.End.X + deltaX, dimension.End.Y + deltaY);
                break;
            case CadDimensionMarkup dimension: // the grip on the dimension line: the line goes through it
                var middle = dimension.LineMiddle();
                dimension.Offset = dimension.OffsetTo(new(middle.X + deltaX, middle.Y + deltaY));
                break;
        }
    }

    /// <summary>
    /// A placed sheet's edge: a faint dashed line, clickable a few pixels either side. Its inside lets clicks through to
    /// what is under it, as the sheet may cover the whole drawing.
    /// </summary>
    IEnumerable<FrameworkElement> CreateSheetFrame(ViewRect area)
    {
        double reach = 4 / _screenUnitsPerPoint;
        var clickTarget = new Rectangle
        {
            Width = area.Width + 2 * reach, Height = area.Height + 2 * reach, Stroke = Brushes.Transparent, StrokeThickness = 2 * reach,
            ToolTip = "Placed sheet: click or drag its edge to select or move it; drag a corner to resize it",
        };
        SetLeft(clickTarget, area.Left - reach);
        SetTop(clickTarget, area.Top - reach);
        yield return clickTarget;
        var line = new Rectangle
        {
            Width = area.Width, Height = area.Height, Stroke = Brushes.Gray, StrokeThickness = 1 / _screenUnitsPerPoint, StrokeDashArray = [4, 4],
            Opacity = 0.6, IsHitTestVisible = false,
        };
        SetLeft(line, area.Left);
        SetTop(line, area.Top);
        yield return line;
    }

    /// <summary>Where the grips of a markup go, with their index. Text, pen and highlights have none: they only move.</summary>
    IEnumerable<(ViewPoint Point, int Index)> GripPoints(Markup markup) => markup switch
    {
        CloudMarkup cloud => cloud.Vertices.Concat(cloud.TextPosition is { } tag ? [tag] : []).Select((point, index) => (_page!.ToView(point), index)),
        LineMarkup line => [(_page!.ToView(line.Start), 0), (_page.ToView(line.End), 1)],
        RectangleMarkup rectangle => [(_page!.ToView(rectangle.Corner1), 0), (_page.ToView(rectangle.Corner2), 1)],
        MeasureMarkup measure => measure.Points.Select((point, index) => (_page!.ToView(point), index)),
        CalloutMarkup callout => [(_page!.ToView(callout.ArrowTip), 0), (_page.ToView(callout.TextPosition), 1)],
        CadLineMarkup line => line.Points.Select((point, index) => (_page!.ToView(point), index)),
        CadCircleMarkup circle => [(_page!.ToView(circle.Center), 0), (_page.ToView(new PdfPoint(circle.Center.X + circle.Radius, circle.Center.Y)), 1)],
        CadDimensionMarkup dimension => [(_page!.ToView(dimension.Start), 0), (_page.ToView(dimension.End), 1), (_page.ToView(dimension.LineMiddle()), 2)],
        PlacedSheetMarkup sheet when sheet.Visible(_page!) is var visible => [(new ViewPoint(visible.Left, visible.Top), 0), (new ViewPoint(visible.Right, visible.Bottom), 1)],
        _ => [],
    };

    /// <summary>The markup or grip an element was drawn for. Looks up from the clicked element: a label's text sits inside its border.</summary>
    object? TagOf(object source)
    {
        for (var element = source as DependencyObject; element != null && element != this; element = VisualTreeHelper.GetParent(element))
            if (element is FrameworkElement { Tag: { } tag }) return tag;
        return null;
    }

    /// <summary>Reopens the text editor on a text or callout, filled with its text; on a dimension, with its length.</summary>
    void EditText(Markup markup)
    {
        if (markup is CadDimensionMarkup dimension)
        {
            EditDimension(dimension);
            return;
        }
        var (position, oldText, fontSize) = markup switch
        {
            TextMarkup text => (text.TextPosition, text.Text, text.FontSize),
            CalloutMarkup callout => (callout.TextPosition, callout.Text, callout.FontSize),
            CloudMarkup { TextPosition: { } tagPosition } cloud => (tagPosition, cloud.Text, cloud.FontSize),
            _ => throw new ArgumentException("Only text, callouts and cloud tags have text.", nameof(markup)),
        };
        Select([markup]);
        _editingMarkup = markup;
        OpenTextEditor(_page!.ToView(position), fontSize, markup.Color, markup.Font, oldText, newText =>
        {
            if (newText == NormalizeText(oldText)) return; // nothing changed: a file markup stays untouched
            if (markup is CloudMarkup cloud && (newText.Length > 0 || cloud.Style != AreaLineStyle.None))
            {
                cloud.Text = newText;
                if (newText.Length == 0) cloud.TextPosition = null; // emptied: the cloud stays, without its tag
            }
            else if (newText.Length == 0)
            {
                _markups.Remove(markup);
                Select([]);
            }
            else if (markup is TextMarkup text) text.Text = newText;
            else if (markup is CalloutMarkup callout) callout.Text = newText;
            MarkupsChanged?.Invoke();
        });
        Redraw();
    }

    /// <summary>
    /// Asks for a dimension's new length over its value. The shapes its ends sit on change to fit (see
    /// <see cref="Dimensioning.Resize"/>); with nothing there, the dimension stays as it is, true to the drawing.
    /// </summary>
    void EditDimension(CadDimensionMarkup dimension)
    {
        Select([dimension]);
        _editingMarkup = dimension;
        string oldText = dimension.LengthMm.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
        OpenTextEditor(Dimensioning.Shape(dimension, _page!).TextTopLeft, CadDimensionMarkup.TextSize, dimension.Color, dimension.Font, oldText, newText =>
        {
            if (newText == oldText || newText.Length == 0) return;
            if (DrawingScale.ParseLengthMm(newText) is not { } lengthMm)
                MeasureReadout?.Invoke("Dimension: type a length, for example 600, 60 cm or 0.6 m");
            else if (!Dimensioning.Resize(dimension, lengthMm, _markups, _page!))
                MeasureReadout?.Invoke("Dimension: no shape at its ends to change. Draw it from the corners or ends of CAD lines, circles or markups");
            else
            {
                MeasureReadout?.Invoke($"Dimension: {dimension.ValueText}");
                MarkupsChanged?.Invoke();
            }
        });
        Redraw();
    }

    /// <summary>A dimension from <paramref name="start"/> to <paramref name="end"/>, its line through <paramref name="onLine"/>, in the current style and scale.</summary>
    CadDimensionMarkup NewDimension(ViewPoint start, ViewPoint end, ViewPoint onLine)
    {
        var dimension = new CadDimensionMarkup
        {
            Start = _page!.ToPdf(start), End = _page.ToPdf(end), MmPerPoint = MmPerPoint, Color = CurrentColor, LineWidth = CurrentLineWidth, Font = CurrentFont,
        };
        dimension.Offset = dimension.OffsetTo(_page.ToPdf(onLine));
        return dimension;
    }

    /// <summary>Adds a finished cloud, or for the tag tools holds the area until the next click places the tag.</summary>
    void FinishArea(List<ViewPoint> points)
    {
        var area = new CloudMarkup
        {
            Vertices = points.Select(_page!.ToPdf).ToList(), Shape = CurrentShape, Color = CurrentColor,
            Style = Tool == MarkupTool.Cloud ? AreaLineStyle.Cloud : CurrentLineStyle, // the outline picked is the callout's LineWidth = CurrentLineWidth,
        };
        if (Tool == MarkupTool.Cloud) AddMarkup(area);
        else _areaAwaitingTag = area;
    }

    void AddMarkup(Markup markup)
    {
        markup.Color = markup is CadHatchMarkup ? CurrentFill ?? CurrentColor : CurrentColor; // a hatch in the fill colour; the line colour while "No fill" is picked
        markup.LineWidth = CurrentLineWidth;
        if (TakesFont(markup)) markup.Font = CurrentFont;
        _markups.Add(markup);
        MarkupsChanged?.Invoke();
    }

    void CancelDrawing()
    {
        CommitTextEditor(); // keep text the user already typed
        _cloudVertices.Clear();
        _areaAwaitingTag = null;
        _firstPoint = null;
        _freehandStroke = null;
        _measurePoints.Clear();
        _cadPoints.Clear();
        (_typedFirstMm, _typedSecondMm) = (null, null);
        _offsetSource = null;
        _snapMarker = null;
        CancelPicking();
    }

    /// <summary>True while a markup is half drawn: Esc cancels it, and moving the mouse redraws its rubber band.</summary>
    bool IsDrawing => _cloudVertices.Count > 0 || _areaAwaitingTag != null || _firstPoint != null || _measurePoints.Count > 0 || _picks != null
        || _cadPoints.Count > 0 || _offsetSource != null;

    // ---- Snapping and measuring ---------------------------------------------------------------

    /// <summary>How close the cursor must be to the line work to snap, in screen pixels.</summary>
    const double SnapRadiusPixels = 10;

    double SnapRadius => SnapRadiusPixels / _screenUnitsPerPoint;

    /// <summary>The drawing's line work on this page, for snapping. Null until the window has read it.</summary>
    public SnapIndex? Snaps
    {
        get => _snaps;
        set
        {
            _snaps = value;
            _snapMarker = null;
        }
    }

    /// <summary>Whether clicks snap to the drawing: when placing points, and while calibrating.</summary>
    public bool UsesSnapping =>
        Tool is MarkupTool.Measure or MarkupTool.Cloud or MarkupTool.Callout or MarkupTool.Tag or MarkupTool.Line or MarkupTool.Rectangle or MarkupTool.Mask
            or MarkupTool.CadLine or MarkupTool.CadPolyline or MarkupTool.CadRectangle or MarkupTool.CadCircle
        || (Tool == MarkupTool.CadDimension && _cadPoints.Count < 2) // the third click places the dimension line freely
        || _picks != null;

    /// <summary>Whether points snap to the drawing. Holding Ctrl while clicking does the opposite.</summary>
    public bool SnapEnabled { get; set; } = true;

    /// <summary>The page's scale, as real mm per point, for new measurements.</summary>
    public double MmPerPoint { get; set; } = DrawingScale.PaperMmPerPoint;

    /// <summary>The value of the measurement being drawn, or of the one just finished, for the status bar.</summary>
    public event Action<string>? MeasureReadout;

    /// <summary>Asks for two points; <paramref name="onPicked"/> gets the distance between them in points. Esc cancels.</summary>
    public void StartCalibration(Action<double> onPicked) => StartPicking(2, PickPreview.Dimension, points =>
    {
        double length = CloudGeometry.Distance(points[0], points[1]);
        if (length > 0.5) onPicked(length);
    });

    /// <summary>Asks for <paramref name="count"/> points (snapped), shown as <paramref name="preview"/>. Esc cancels.</summary>
    void StartPicking(int count, PickPreview preview, Action<List<ViewPoint>> onPicked, SnapIndex? snaps = null)
    {
        CancelDrawing();
        (_picks, _pickCount, _pickPreview, _onPicked, _pickSnaps) = ([], count, preview, onPicked, snaps);
        Redraw();
    }

    void CancelPicking()
    {
        _picks = null;
        _onPicked = null;
        _pickSnaps = null;
    }

    enum PickPreview { Dimension, Rectangle, Line }

    /// <summary>The last point placed in the drawing in progress, for Shift's horizontal/vertical lock.</summary>
    ViewPoint? LastDrawnPoint =>
        _picks is { Count: > 0 } picks ? picks[^1]
        : _measurePoints.Count > 0 ? _measurePoints[^1]
        : _cloudVertices.Count > 0 ? _cloudVertices[^1]
        : _cadPoints.Count > 0 ? _cadPoints[^1]
        : _firstPoint;

    /// <summary>
    /// Where a point lands: Shift (or Ctrl with the CAD tools) locks it horizontal or vertical from
    /// <paramref name="from"/>; otherwise Ctrl flips <see cref="SnapEnabled"/>, and it snaps to the drawing's line work
    /// within reach, and to the markups' edges, corners and circles. A CAD line that snaps to nothing and runs within
    /// a few degrees of horizontal or vertical is pulled straight.
    /// </summary>
    ViewPoint Snapped(ViewPoint point, ViewPoint? from)
    {
        var snapped = SnappedToDrawing(point, from);
        return _snapMarker == null && Tool is MarkupTool.CadLine or MarkupTool.CadPolyline && from is { } origin ? LeanToOrtho(origin, snapped) : snapped;
    }

    /// <summary>How close to horizontal or vertical a CAD line is pulled straight, in degrees.</summary>
    const double OrthoLeanDegrees = 4;

    static ViewPoint LeanToOrtho(ViewPoint origin, ViewPoint point)
    {
        double angle = Math.Atan2(Math.Abs(point.Y - origin.Y), Math.Abs(point.X - origin.X)) * 180 / Math.PI;
        return angle < OrthoLeanDegrees ? new(point.X, origin.Y) : angle > 90 - OrthoLeanDegrees ? new(origin.X, point.Y) : point;
    }

    /// <summary>Where the next CAD line point lands: <paramref name="toward"/>, or the typed length from <paramref name="from"/> in its direction.</summary>
    ViewPoint NextCadLinePoint(ViewPoint from, ViewPoint toward, double? lengthMm)
    {
        if (lengthMm is not { } mm) return toward;
        double dx = toward.X - from.X, dy = toward.Y - from.Y, length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-9) (dx, dy, length) = (1, 0, 1); // cursor on the point: to the right
        double scale = mm / MmPerPoint / length;
        return new(from.X + dx * scale, from.Y + dy * scale);
    }

    /// <summary>A CAD rectangle's corner opposite <paramref name="corner"/>: the typed width and height on the cursor's side, or the cursor's.</summary>
    ViewPoint OppositeCorner(ViewPoint corner, ViewPoint toward) => new(
        _typedFirstMm is { } width ? corner.X + Math.CopySign(width / MmPerPoint, toward.X - corner.X) : toward.X,
        _typedSecondMm is { } height ? corner.Y + Math.CopySign(height / MmPerPoint, toward.Y - corner.Y) : toward.Y);

    ViewPoint SnappedToDrawing(ViewPoint point, ViewPoint? from)
    {
        _snapMarker = null;
        var modifiers = Keyboard.Modifiers;
        bool cad = IsCadTool(Tool), ortho = cad && Tool != MarkupTool.CadRectangle; // a rectangle locked flat would be a line
        if (from is { } start && modifiers.HasFlag(ModifierKeys.Shift) && Tool is MarkupTool.CadLine or MarkupTool.CadPolyline)
            return CadGeometry.RoundedAngle(start, point);
        if (from is { } corner && modifiers.HasFlag(ModifierKeys.Shift) && Tool == MarkupTool.CadRectangle)
            return CadGeometry.RoundedCorner(corner, point, DrawingScale.RoundingStepMm(MmPerPoint) / MmPerPoint); // whole mm, or tens
        if ((modifiers.HasFlag(ModifierKeys.Shift) || (ortho && modifiers.HasFlag(ModifierKeys.Control))) && from is { } origin)
            return Math.Abs(point.X - origin.X) >= Math.Abs(point.Y - origin.Y) ? new(point.X, origin.Y) : new(origin.X, point.Y);
        if (SnapEnabled == (!cad && modifiers.HasFlag(ModifierKeys.Control))) return point;
        var onDrawing = (_pickSnaps ?? _snaps)?.Snap(point, SnapRadius);
        // shortcut: the markups' snap index is rebuilt on every move; cache it if pages with thousands of markups lag.
        var onMarkups = _page != null && _pickSnaps == null
            ? new SnapIndex(VisibleMarkups.SelectMany(markup => Dimensioning.SnapSegments(markup, _page))).Snap(point, SnapRadius)
            : null;
        _snapMarker = onMarkups is { } m && (onDrawing is not { } d || m.Kind <= d.Kind) ? onMarkups : onDrawing;
        return _snapMarker?.Point ?? point;
    }

    // ---- Placed sheets -------------------------------------------------------------------------

    /// <summary>The placed sheet, when it is the one markup selected.</summary>
    public PlacedSheetMarkup? SelectedSheet => SelectedMarkup as PlacedSheetMarkup;

    /// <summary>Adds a placed sheet (its tint in the colour picked) and selects it.</summary>
    public void PlaceSheet(PlacedSheetMarkup sheet)
    {
        CancelDrawing();
        AddMarkup(sheet);
        Select([sheet]);
    }

    /// <summary>Sets the selected sheet's opacity; <paramref name="commit"/> makes it an undo step (once a slider is let go).</summary>
    public void SetSheetOpacity(double opacity, bool commit)
    {
        if (SelectedSheet is not { } sheet) return;
        sheet.Opacity = opacity;
        if (commit) MarkupsChanged?.Invoke();
        Redraw();
    }

    public void SetSheetTinted(bool tinted) => ChangeSelected(markup => markup is PlacedSheetMarkup sheet && sheet.Tinted != tinted && Do(() => sheet.Tinted = tinted));

    public void ShowWholeSheet() => ChangeSelected(markup => markup is PlacedSheetMarkup { IsClipped: true } sheet
        && Do(() => (sheet.ClipLeft, sheet.ClipTop, sheet.ClipRight, sheet.ClipBottom) = (0, 0, 1, 1)));

    /// <summary>Cuts the selected sheet down to a rectangle: two corners picked. <paramref name="status"/> gets what to do, or how it went.</summary>
    public void StartSheetClip(Action<string> status)
    {
        if (SelectedSheet is not { } sheet || _page is not { } page) return;
        status("Clip: click two opposite corners of the part of the sheet to keep. Esc cancels");
        StartPicking(2, PickPreview.Rectangle, corners =>
        {
            if (!sheet.ClipTo(ViewRect.Around(corners), page))
            {
                status("Clip: that box misses the sheet, so nothing changed");
                return;
            }
            MarkupsChanged?.Invoke();
            status("Clipped. “Show all” brings the whole sheet back");
        });
    }

    /// <summary>
    /// Lines the selected sheet up on the drawing: two points on the sheet (snapping to <paramref name="sheetLines"/>), then
    /// the same two on the drawing. The sheet moves and scales to match; it isn't turned.
    /// </summary>
    public void StartSheetAlign(SnapIndex? sheetLines, Action<string> status)
    {
        if (SelectedSheet is not { } sheet || _page is not { } page) return;
        status("Align 1 of 2: click two points on the placed sheet, such as grid crossings (they snap to the sheet's lines). Esc cancels");
        StartPicking(2, PickPreview.Line, onSheet =>
        {
            status("Align 2 of 2: now click the same two points on the drawing, in the same order. Esc cancels");
            StartPicking(2, PickPreview.Line, onDrawing =>
            {
                PageAlignment alignment;
                try
                {
                    alignment = PageAlignment.FromPoints((onDrawing[0].X, onDrawing[0].Y), (onDrawing[1].X, onDrawing[1].Y), (onSheet[0].X, onSheet[0].Y), (onSheet[1].X, onSheet[1].Y));
                }
                catch (ArgumentException)
                {
                    status("Align: the two points must be apart. Try again");
                    return;
                }
                sheet.Map(point =>
                {
                    var onPage = page.ToView(point);
                    return page.ToPdf(new ViewPoint(onPage.X * alignment.Scale + alignment.OffsetX, onPage.Y * alignment.Scale + alignment.OffsetY));
                });
                MarkupsChanged?.Invoke();
                status($"Aligned: the sheet moved, and scaled by {alignment.Scale:0.###}");
            });
        }, sheetLines);
    }

    /// <summary>A sheet's drawing on screen, by its source and tint. Drawn in the background: null until it is ready.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], SheetImage> SheetImages = new();

    sealed record SheetImage(string? Tint, BitmapSource? Bitmap);

    /// <summary>Long side of a sheet's drawing on screen, in pixels.</summary>
    const double SheetImagePixels = 4000; // shortcut: blurs when zoomed in far on a big sheet; render the visible part at zoom if that matters

    BitmapSource? SheetBitmap(PlacedSheetMarkup sheet)
    {
        string? tint = sheet.Tinted ? sheet.Color : null;
        var source = sheet.Source;
        if (SheetImages.TryGetValue(source, out var known) && known.Tint == tint) return known.Bitmap;
        SheetImages.AddOrUpdate(source, new SheetImage(tint, null));
        double scale = SheetImagePixels / Math.Max(sheet.SourceWidth, sheet.SourceHeight);
        Task.Run(() => PlacedSheet.Render(source, scale, tint)).ContinueWith(drawn =>
        {
            if (!SheetImages.TryGetValue(source, out var current) || current.Tint != tint) return; // the tint changed meanwhile
            if (drawn.Exception is { } problem)
            {
                MeasureReadout?.Invoke($"Couldn't draw the placed sheet: {problem.InnerException?.Message}");
                return;
            }
            var pixels = drawn.Result;
            var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, pixels.BgraPixels, pixels.Width * 4);
            bitmap.Freeze();
            SheetImages.AddOrUpdate(source, new SheetImage(tint, bitmap));
            Redraw();
        }, TaskScheduler.FromCurrentSynchronizationContext());
        return null;
    }

    // ---- CAD tools ----------------------------------------------------------------------------

    /// <summary>Ends a CAD line chain, or adds the polyline drawn so far (two points or more).</summary>
    void FinishCadLine(bool closed = false)
    {
        var points = _cadPoints.ToList();
        _cadPoints.Clear();
        if (Tool == MarkupTool.CadPolyline && points.Count >= (closed ? 3 : 2))
            AddMarkup(new CadLineMarkup { Points = points.Select(_page!.ToPdf).ToList(), Closed = closed });
        Redraw();
    }

    /// <summary>
    /// Enter in a size box: places the next point as a click would, but at the typed size from the last point, towards
    /// the cursor: a line's length or a circle's radius in real mm, or a rectangle at the width and height typed so far
    /// (the cursor's where none is typed). False if no first point is placed yet.
    /// </summary>
    public bool ApplyTypedSize(double firstMm)
    {
        if (_cadPoints.Count == 0 || _page == null) return false;
        var (from, toward) = (_cadPoints[^1], _mousePosition);
        double first = firstMm / MmPerPoint;
        switch (Tool)
        {
            case MarkupTool.CadLine or MarkupTool.CadPolyline:
                var end = NextCadLinePoint(from, toward, firstMm);
                if (Tool == MarkupTool.CadPolyline) _cadPoints.Add(end);
                else
                {
                    AddMarkup(new CadLineMarkup { Points = [_page.ToPdf(from), _page.ToPdf(end)] });
                    _cadPoints.Clear();
                }
                break;
            case MarkupTool.CadRectangle:
                AddCadRectangle(from, OppositeCorner(from, toward));
                _cadPoints.Clear();
                break;
            case MarkupTool.CadCircle:
                AddCadCircle(first);
                break;
            default:
                return false;
        }
        (_typedFirstMm, _typedSecondMm) = (null, null);
        Redraw();
        return true;
    }

    /// <summary>A circle round the centre already placed, with this radius in points.</summary>
    void AddCadCircle(double radius)
    {
        var center = _cadPoints[0];
        _cadPoints.Clear();
        if (radius >= 0.5) AddMarkup(new CadCircleMarkup { Center = _page!.ToPdf(center), Radius = radius });
    }

    /// <summary>A closed CAD polyline with these opposite corners, square to the screen.</summary>
    void AddCadRectangle(ViewPoint corner, ViewPoint opposite)
    {
        if (Math.Abs(opposite.X - corner.X) < 0.5 || Math.Abs(opposite.Y - corner.Y) < 0.5) return; // flat: nothing to draw
        ViewPoint[] corners = [corner, new(opposite.X, corner.Y), opposite, new(corner.X, opposite.Y)];
        AddMarkup(new CadLineMarkup { Points = corners.Select(_page!.ToPdf).ToList(), Closed = true });
    }

    /// <summary>Joins the selected open CAD lines into one polyline, closed if they go all round, in place of them.</summary>
    public void JoinSelectedCadLines()
    {
        var lines = _selection.OfType<CadLineMarkup>().Where(line => !line.Closed).ToList();
        if (CadGeometry.Join(lines, SnapRadius) is not { } joined)
        {
            MeasureReadout?.Invoke("Join: select two or more open CAD lines first (Select tool, click or drag a window)");
            return;
        }
        int index = _markups.FindIndex(lines.Contains);
        _markups.RemoveAll(lines.Contains);
        _markups.Insert(index, joined);
        MarkupsChanged?.Invoke();
        Select([joined]);
        MeasureReadout?.Invoke($"Joined {lines.Count} lines into {(joined.Closed ? "a closed" : "an open")} polyline");
    }

    /// <summary>Adds an entity made from another one (offset, trim), keeping that one's colour and weight.</summary>
    void AddCad(CadMarkup markup)
    {
        _markups.Add(markup);
        MarkupsChanged?.Invoke();
    }

    /// <summary>The CAD line or circle under the cursor, within snapping reach.</summary>
    CadMarkup? CadEdgeAt(ViewPoint position)
    {
        var point = _page!.ToPdf(position);
        return CadEdges.Select(edge => (Edge: edge, Distance: CadGeometry.DistanceTo(edge, point)))
            .Where(candidate => candidate.Distance <= SnapRadius).MinBy(candidate => candidate.Distance).Edge;
    }

    /// <summary>The drawing's own line under the cursor (a dashed gridline whole), as a CAD line in the current colour and weight, not yet added.</summary>
    CadLineMarkup? PdfLineAt(ViewPoint position) =>
        _snaps?.LineThrough(position, SnapRadius) is var (a, b)
            ? new CadLineMarkup { Points = [_page!.ToPdf(a), _page.ToPdf(b)], Color = CurrentColor, LineWidth = CurrentLineWidth }
            : null;

    /// <summary>Cuts the clicked piece of a CAD line or circle back to the nearest crossing lines.</summary>
    void Trim(ViewPoint position)
    {
        if (CadEdgeAt(position) is not { } target) return;
        if (TrimAt(target, _page!.ToPdf(position)) == null)
        {
            MeasureReadout?.Invoke("Trim: a closed shape needs two crossing lines to trim between");
            return;
        }
        MarkupsChanged?.Invoke();
    }

    /// <summary>Trims <paramref name="target"/> at <paramref name="pick"/>, putting what is left in its place. Null if it can't be trimmed there.</summary>
    List<CadMarkup>? TrimAt(CadMarkup target, PdfPoint pick)
    {
        if (CadGeometry.Trim(target, CadEdges, pick) is not { } pieces) return null;
        int index = _markups.IndexOf(target);
        _markups.RemoveAt(index);
        _markups.InsertRange(index, pieces);
        _selection.Remove(target);
        return pieces;
    }

    /// <summary>Shift with the trim tool: lengthens the clicked end of a CAD line to the next line, or to the page edge.</summary>
    void Extend(ViewPoint position)
    {
        if (CadEdgeAt(position) is not { } target) return;
        if (CadGeometry.Extend(target, CadEdges, _page!.ToPdf(position), PageBox) is not { } points)
        {
            MeasureReadout?.Invoke("Extend: only the ends of open CAD lines extend, and this one points off the page");
            return;
        }
        ((CadLineMarkup)target).Points = points;
        MarkupsChanged?.Invoke();
    }

    /// <summary>The whole page in PDF space.</summary>
    PdfRect PageBox => _page!.ToPdf(new ViewRect(0, 0, _page.ViewWidth, _page.ViewHeight));

    /// <summary>
    /// Trims every CAD line and circle the window's edge crosses, at the crossing: the piece there goes, up to the
    /// nearest lines. With <paramref name="extend"/>, extends each open line's end nearest the crossing instead.
    /// </summary>
    void TrimInWindow(ViewRect window, bool extend)
    {
        var box = _page!.ToPdf(window);
        int changed = 0;
        if (extend)
        {
            foreach (var line in CadEdges.OfType<CadLineMarkup>().ToList())
                if (CadGeometry.Crossings(line, box) is [var at, ..] && CadGeometry.Extend(line, CadEdges, at, PageBox) is { } points)
                {
                    line.Points = points;
                    changed++;
                }
        }
        else
        {
            // What is left of a trimmed line can still cross the edge elsewhere (a line through the window): trimmed in turn.
            var queue = new Queue<CadMarkup>(CadEdges.Where(edge => CadGeometry.Crossings(edge, box).Count > 0));
            for (int guard = 0; queue.Count > 0 && guard < 1000; guard++)
            {
                var target = queue.Dequeue();
                if (CadGeometry.Crossings(target, box) is not [var at, ..] || TrimAt(target, at) is not { } pieces) continue;
                changed++;
                foreach (var piece in pieces.Where(piece => CadGeometry.Crossings(piece, box).Count > 0)) queue.Enqueue(piece);
            }
        }
        if (changed > 0) MarkupsChanged?.Invoke();
        MeasureReadout?.Invoke(changed == 0 ? $"{(extend ? "Extend" : "Trim")}: the window's edge crosses no CAD line it can change"
            : $"{(extend ? "Extended" : "Trimmed")} {changed} line{(changed == 1 ? "" : "s")}");
    }

    /// <summary>The pick cursor: a small open square, for the tools that pick lines (trim, offset).</summary>
    static readonly Cursor PickboxCursor = CreatePickboxCursor();

    static Cursor CreatePickboxCursor()
    {
        const int Size = 32, Middle = 16, Half = 5;
        var pixels = new byte[Size * Size * 4]; // BGRA, bottom row first as a .cur keeps them
        for (int y = Middle - Half - 1; y <= Middle + Half + 1; y++)
            for (int x = Middle - Half - 1; x <= Middle + Half + 1; x++)
            {
                int ring = Math.Max(Math.Abs(x - Middle), Math.Abs(y - Middle));
                if (ring < Half - 1) continue;
                int at = ((Size - 1 - y) * Size + x) * 4;
                byte shade = ring == Half ? (byte)0 : (byte)255; // black, edged in white inside and out so it shows on dark lines too
                (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]) = (shade, shade, shade, 255);
            }
        using var file = new System.IO.MemoryStream();
        using (var writer = new System.IO.BinaryWriter(file, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int andMask = Size * Size / 8;
            writer.Write((short)0); writer.Write((short)2); writer.Write((short)1); // a cursor file with one image
            writer.Write((byte)Size); writer.Write((byte)Size); writer.Write((short)0);
            writer.Write((short)Middle); writer.Write((short)Middle); // the hot spot: the middle of the square
            writer.Write(40 + pixels.Length + andMask); writer.Write(22);
            writer.Write(40); writer.Write(Size); writer.Write(Size * 2); writer.Write((short)1); writer.Write((short)32); // the bitmap header: height counts the mask too
            writer.Write(0); writer.Write(pixels.Length + andMask); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
            writer.Write(pixels);
            writer.Write(new byte[andMask]); // the alpha above decides what shows
        }
        file.Position = 0;
        return new Cursor(file);
    }

    void FinishMeasure(bool closed)
    {
        var points = _measurePoints.ToList();
        _measurePoints.Clear();
        if (points.Count < (closed ? 3 : 2)) return;
        var measure = new MeasureMarkup
        {
            Kind = closed ? MeasureKind.Area : points.Count == 2 ? MeasureKind.Distance : MeasureKind.Length,
            Points = points.Select(_page!.ToPdf).ToList(),
            MmPerPoint = MmPerPoint,
            Color = CurrentColor,
            LineWidth = CurrentLineWidth,
            Font = CurrentFont,
        };
        _measures.Add(measure);
        MeasureReadout?.Invoke($"{measure.Kind}: {measure.ValueText}");
    }

    /// <summary>Distance or length so far, up to the cursor; and the area if it were closed now.</summary>
    void ShowMeasureReadout()
    {
        if (_page == null) return;
        var points = _measurePoints.Append(_mousePosition).Select(_page.ToPdf).ToList();
        var length = new MeasureMarkup { Kind = MeasureKind.Length, Points = points, MmPerPoint = MmPerPoint };
        string text = $"{(points.Count == 2 ? "Distance" : "Length")}: {length.ValueText}";
        if (points.Count >= 3) text += $"   Area if closed: {new MeasureMarkup { Kind = MeasureKind.Area, Points = points, MmPerPoint = MmPerPoint }.ValueText}";
        MeasureReadout?.Invoke(text);
    }

    // ---- Selecting page text ----------------------------------------------------------------

    /// <summary>How close a click must be to a character to start selecting, in points.</summary>
    const double TextHitTolerance = 4;

    void StartTextSelection(ViewPoint position, int clickCount)
    {
        if (_pageText == null) return;
        int index = _pageText.CharacterAt(position, TextHitTolerance);
        if (index < 0)
        {
            ClearTextSelection();
            return;
        }
        if (clickCount == 2)
        {
            _textSelection = _pageText.WordAt(index);
            return;
        }
        _textSelection = (index, index);
        _isSelectingText = true;
        CaptureMouse();
    }

    void ClearTextSelection()
    {
        _textSelection = null;
        _isSelectingText = false;
    }

    void CopySelectedText()
    {
        if (SelectedText is not { Length: > 0 } text) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another app has the clipboard open. Rare and harmless: the user can copy again.
        }
    }

    /// <summary>Turns each line of the selection into a highlighter stroke, all as one undo step.</summary>
    void HighlightSelection()
    {
        if (_page == null || _pageText == null || _textSelection is not var (start, end)) return;
        foreach (var line in _pageText.LineRects(start, end))
        {
            // Along the line's long side (text on rotated sheets runs up the screen), inset so the round ends stay on the text.
            bool isAcross = line.Width >= line.Height;
            double thickness = isAcross ? line.Height : line.Width;
            double inset = Math.Min(thickness / 2, (isAcross ? line.Width : line.Height) / 2);
            ViewPoint from = isAcross ? new(line.Left + inset, (line.Top + line.Bottom) / 2) : new((line.Left + line.Right) / 2, line.Top + inset);
            ViewPoint to = isAcross ? new(line.Right - inset, from.Y) : new(from.X, line.Bottom - inset);
            _markups.Add(new HighlightMarkup { Points = [_page.ToPdf(from), _page.ToPdf(to)], Thickness = thickness });
        }
        ClearTextSelection();
        MarkupsChanged?.Invoke();
        Redraw();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (_cadPoints.Count > 0 && Tool is MarkupTool.CadLine or MarkupTool.CadPolyline)
        {
            FinishCadLine(); // right-click ends the line
            e.Handled = true;
            return;
        }
        if (Tool != MarkupTool.SelectText || SelectedText is not { Length: > 0 }) return;
        var copy = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C" };
        copy.Click += (_, _) => CopySelectedText();
        var highlight = new MenuItem { Header = "Highlight selection" };
        highlight.Click += (_, _) => HighlightSelection();
        new ContextMenu { Items = { copy, highlight }, PlacementTarget = this, IsOpen = true };
        e.Handled = true;
    }

    // ---- Text editing -------------------------------------------------------------------------

    /// <summary>Opens the on-canvas text box. <paramref name="onCommit"/> gets the typed text (trimmed, possibly empty) on Enter or click-away, not on Esc.</summary>
    void OpenTextEditor(ViewPoint position, double fontSize, string color, string? font, string initialText, Action<string> onCommit, double? wrapWidth = null)
    {
        _onTextCommitted = onCommit;
        _textEditor = new TextBox
        {
            MinWidth = wrapWidth == null ? 60 : 0,
            TextWrapping = wrapWidth == null ? TextWrapping.NoWrap : TextWrapping.Wrap,
            FontFamily = font == null ? LabelFont : new FontFamily(font),
            FontSize = fontSize,
            AcceptsReturn = true,
            Foreground = BrushFromHex(color),
            Text = initialText,
        };
        _textEditor.PreviewKeyDown += OnTextEditorKeyDown;
        _textEditor.LostKeyboardFocus += (_, _) => CommitTextEditor();
        SetLeft(_textEditor, position.X);
        SetTop(_textEditor, position.Y);
        SetZIndex(_textEditor, 1); // markups are redrawn after it, but must not cover it
        Children.Add(_textEditor);
        Dispatcher.BeginInvoke(() =>
        {
            if (_textEditor == null) return;
            // The text inside wraps at the box's width, as it will once added: the style's padding and border, and the 2 px WPF keeps each side of the text, come on top.
            if (wrapWidth is { } width)
                _textEditor.Width = width + _textEditor.Padding.Left + _textEditor.Padding.Right + _textEditor.BorderThickness.Left + _textEditor.BorderThickness.Right + 4;
            _textEditor.Focus();
            _textEditor.CaretIndex = _textEditor.Text.Length;
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    Action<string> AddIfNotEmpty(Func<string, Markup> createMarkup) => text =>
    {
        if (text.Length > 0) AddMarkup(createMarkup(text));
    };

    /// <summary>Text as stored: trimmed, with \n line breaks (the text box gives \r\n; files may hold \r).</summary>
    static string NormalizeText(string text) => text.Trim().Replace("\r\n", "\n").Replace('\r', '\n');

    void OnTextEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _onTextCommitted = null; // discard the text (or keep the old text when editing)
            CommitTextEditor();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift) // Shift+Enter adds a new line
        {
            CommitTextEditor();
            e.Handled = true;
        }
    }

    void CommitTextEditor()
    {
        if (_textEditor == null) return;
        var editor = _textEditor;
        _textEditor = null; // set before removing: removing fires LostKeyboardFocus, which calls back in here
        if (Children.IndexOf(editor) < _overlayStart) _overlayStart--;
        Children.Remove(editor);

        _markupBeingTyped = null; // added by the commit below, or dropped (Esc)
        var onCommit = _onTextCommitted;
        _onTextCommitted = null;
        _editingMarkup = null;
        onCommit?.Invoke(NormalizeText(editor.Text));
        Redraw();
    }

    // ---- Drawing ------------------------------------------------------------------------------

    // Children are the markups' shapes, then from here on the overlay: grips, selected page text and what is being drawn.
    // A mouse move redraws only the overlay (and the markups being dragged): rebuilding hundreds of markups each move lags.
    int _overlayStart;

    /// <summary>Draws everything again from the markup list.</summary>
    void Redraw()
    {
        for (int i = Children.Count - 1; i >= 0; i--)
            if (Children[i] != _textEditor) Children.RemoveAt(i);
        _overlayStart = Children.Count;
        if (_page == null) return;
        foreach (var markup in VisibleMarkups.OrderBy(markup => markup is PlacedSheetMarkup ? 0 : markup is CadHatchMarkup ? 1 : 2)) // placed sheets and hatches underneath: markups on them stay clickable
            AddShapes(markup);
        foreach (var measure in _measures) AddShapes(measure);
        DrawOverlay();
    }

    /// <summary>Redraws the overlay only, for a change that leaves the markups as they are.</summary>
    void RedrawOverlay()
    {
        for (int i = Children.Count - 1; i >= _overlayStart; i--)
            if (Children[i] != _textEditor) Children.RemoveAt(i);
        if (_page != null) DrawOverlay();
    }

    /// <summary>Redraws these markups (the ones being dragged) and the overlay. They come on top of the rest until the next full redraw.</summary>
    void RedrawMarkups(IReadOnlyList<Markup> markups)
    {
        var redrawn = markups.ToHashSet();
        for (int i = _overlayStart - 1; i >= 0; i--)
            if (Children[i] is FrameworkElement { Tag: Markup markup } && redrawn.Contains(markup))
            {
                Children.RemoveAt(i);
                _overlayStart--;
            }
        foreach (var markup in markups) AddShapes(markup);
        RedrawOverlay();
    }

    /// <summary>Adds a markup's shapes after the other markups, under the overlay.</summary>
    void AddShapes(Markup markup)
    {
        if (markup == _editingMarkup) return; // the text box stands in for it
        foreach (var element in CreateShapes(markup))
        {
            element.Tag = markup; // lets the Select tool find the markup that was clicked
            if ((_selection.Contains(markup) || markup == _offsetSource) && element is not Image) element.Effect = SelectionGlow; // a glow on a whole sheet's image is slow
            Children.Insert(_overlayStart++, element);
        }
    }

    void DrawOverlay()
    {
        if (SelectedMarkup is { } selected && selected != _editingMarkup)
            foreach (var (point, index) in GripPoints(selected)) Children.Add(CreateGrip(point, new Grip(selected, index)));
        if (_pageText != null && _textSelection is (int start, int end))
            foreach (var line in _pageText.LineRects(start, end)) Children.Add(CreateTextSelectionRect(line));
        int firstPreview = Children.Count;
        DrawInProgress();
        for (int i = firstPreview; i < Children.Count; i++) Children[i].IsHitTestVisible = false; // clicks go to the canvas, not the preview
    }

    static readonly Brush TextSelectionBrush = CreateFrozenBrush(Color.FromArgb(90, 0x1E, 0x88, 0xE5));

    static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    static Rectangle CreateTextSelectionRect(ViewRect line)
    {
        var rect = new Rectangle { Width = line.Width, Height = line.Height, Fill = TextSelectionBrush, IsHitTestVisible = false };
        SetLeft(rect, line.Left);
        SetTop(rect, line.Top);
        return rect;
    }

    Rectangle CreateGrip(ViewPoint center, Grip grip)
    {
        double size = GripSize / _screenUnitsPerPoint;
        var square = new Rectangle
        {
            Width = size,
            Height = size,
            Fill = Brushes.White,
            Stroke = Brushes.DodgerBlue,
            StrokeThickness = 1.5 / _screenUnitsPerPoint,
            Cursor = Cursors.SizeAll,
            Tag = grip,
            ToolTip = grip.Markup is CloudMarkup { Shape: AreaShape.Polygon } ? "Drag to move this corner. Ctrl+click removes it" : null,
        };
        SetLeft(square, center.X - size / 2);
        SetTop(square, center.Y - size / 2);
        return square;
    }

    IEnumerable<FrameworkElement> CreateShapes(Markup markup)
    {
        if (markup is not CadMarkup && markup.IsUnchangedFromFile && markup.FileBounds is { } fileBounds) // CAD is always drawn here, for the layer switch
        {
            // The page image already shows this markup as the file draws it (Bluebeam's wrapping, fills, fonts...).
            // Only an invisible click target goes here, outlined while selected.
            var area = _page!.ToView(fileBounds);
            if (markup is PlacedSheetMarkup)
            {
                foreach (var edge in CreateSheetFrame(area)) yield return edge;
                yield break;
            }
            var clickTarget = new Rectangle
            {
                Width = area.Width,
                Height = area.Height,
                Fill = Brushes.Transparent,
                Stroke = _selection.Contains(markup) ? Brushes.DodgerBlue : null,
                StrokeDashArray = [4, 2],
            };
            SetLeft(clickTarget, area.Left);
            SetTop(clickTarget, area.Top);
            yield return clickTarget;
            yield break;
        }

        var brush = BrushFromHex(markup.Color);
        if (markup is IFillable { CanFill: true, FillColor: { } fillColor } filled)
        {
            // Under the outline. Hatched or solid, the inside is clickable.
            var fillOutline = filled.FillOutline(_page!);
            var fillBrush = BrushFromHex(fillColor);
            var fillArea = CreateOutline(fillOutline.Select(_page!.ToView).ToList(), AreaLineStyle.None, fillBrush, 0);
            fillArea.Fill = filled.FillPattern == HatchPattern.Solid ? fillBrush : Brushes.Transparent;
            yield return fillArea;
            if (filled.FillPattern != HatchPattern.Solid)
                yield return CreateHatchLines(CadGeometry.HatchStrokes(fillOutline, filled.FillPattern), fillBrush, IFillable.PatternLineWidth);
        }
        switch (markup)
        {
            case TextMarkup text:
                yield return CreateLabel(text.Text, text.FontSize, _page!.ToView(text.TextPosition), brush, hasBorder: false, text.LineWidth, text.Font);
                break;

            case CalloutMarkup callout:
                var labelTopLeft = _page!.ToView(callout.TextPosition);
                var box = LabelGeometry.Box(labelTopLeft, callout.Text, callout.FontSize, callout.HasBorder, callout.LineWidth, callout.Font);
                var arrowTip = _page.ToView(callout.ArrowTip);
                foreach (var shape in CreateLeader(LabelGeometry.LeaderStart(box, arrowTip), arrowTip, callout.Ending, brush, callout.LineWidth)) yield return shape;
                yield return CreateLabel(callout.Text, callout.FontSize, labelTopLeft, brush, callout.HasBorder, callout.LineWidth, callout.Font);
                break;

            case CloudMarkup cloud:
                var vertices = cloud.Outline(_page!);
                yield return cloud.Style == AreaLineStyle.Cloud ? CreateCloud(vertices, brush, cloud.LineWidth) : CreateOutline(vertices, cloud.Style, brush, cloud.LineWidth);
                if (cloud.TextPosition is { } tagPosition)
                {
                    var tagTopLeft = _page!.ToView(tagPosition);
                    var (from, to) = LabelGeometry.CloudLeader(vertices, LabelGeometry.Box(tagTopLeft, cloud.Text, cloud.FontSize, hasBorder: false, cloud.LineWidth, cloud.Font));
                    foreach (var shape in CreateLeader(from, to, cloud.Ending, brush, cloud.LineWidth)) yield return shape;
                    yield return CreateLabel(cloud.Text, cloud.FontSize, tagTopLeft, brush, hasBorder: false, cloud.LineWidth, cloud.Font);
                }
                break;

            case HighlightMarkup highlight:
                yield return CreateFreehandStroke(highlight.Points.Select(_page!.ToView), brush, highlight.Thickness, HighlightMarkup.Opacity);
                break;

            case PenMarkup pen:
                yield return CreateFreehandStroke(pen.Points.Select(_page!.ToView), brush, pen.LineWidth, opacity: 1);
                break;

            case LineMarkup line:
                foreach (var shape in CreateLeader(_page!.ToView(line.End), _page.ToView(line.Start), line.Ending, brush, line.LineWidth)) yield return shape;
                break;

            case RectangleMarkup rectangle:
                yield return CreateRectangle(ViewRect.Around([_page!.ToView(rectangle.Corner1), _page.ToView(rectangle.Corner2)]),
                    rectangle.HasOutline ? brush : null, rectangle.HasOutline ? rectangle.LineWidth : 0, dashes: null);
                break;

            case MeasureMarkup measure:
                foreach (var shape in CreateMeasureShapes(measure, brush)) yield return shape;
                break;

            case PlacedSheetMarkup sheet:
                var visible = sheet.Visible(_page!);
                if (SheetBitmap(sheet) is { } bitmap)
                {
                    var placed = sheet.Placed(_page!);
                    var image = new Image
                    {
                        Source = bitmap, Width = placed.Width, Height = placed.Height, Stretch = Stretch.Fill, Opacity = sheet.Opacity, IsHitTestVisible = false,
                        Clip = new RectangleGeometry(new Rect(visible.Left - placed.Left, visible.Top - placed.Top, visible.Width, visible.Height)),
                    };
                    SetLeft(image, placed.Left);
                    SetTop(image, placed.Top);
                    yield return image;
                }
                foreach (var edge in CreateSheetFrame(visible)) yield return edge;
                break;

            case CadLineMarkup line:
                var linePoints = new PointCollection(line.Points.Select(point => ToWpfPoint(_page!.ToView(point))));
                Shape path = line.Closed ? new Polygon { Points = linePoints } : new Polyline { Points = linePoints };
                path.Stroke = brush;
                path.StrokeThickness = line.LineWidth;
                path.StrokeLineJoin = PenLineJoin.Round;
                yield return path;
                break;

            case CadCircleMarkup circle:
                var center = _page!.ToView(circle.Center);
                var ring = new Ellipse { Width = 2 * circle.Radius, Height = 2 * circle.Radius, Stroke = brush, StrokeThickness = circle.LineWidth };
                SetLeft(ring, center.X - circle.Radius);
                SetTop(ring, center.Y - circle.Radius);
                yield return ring;
                break;

            case CadDimensionMarkup dimension:
                var dimensionShape = Dimensioning.Shape(dimension, _page!);
                var strokes = new StreamGeometry();
                using (var context = strokes.Open())
                    foreach (var (from, to) in dimensionShape.Lines)
                    {
                        context.BeginFigure(ToWpfPoint(from), isFilled: false, isClosed: false);
                        context.LineTo(ToWpfPoint(to), isStroked: true, isSmoothJoin: false);
                    }
                strokes.Freeze();
                yield return new Path { Data = strokes, Stroke = brush, StrokeThickness = dimension.LineWidth };
                var value = new Border
                {
                    Background = Brushes.Transparent, // clickable between the letters too
                    Child = LabelText(dimension.ValueText, CadDimensionMarkup.TextSize, brush, dimension.Font),
                    RenderTransform = new RotateTransform(dimensionShape.TextAngle), // about its top-left corner, as in the saved file
                };
                SetLeft(value, dimensionShape.TextTopLeft.X);
                SetTop(value, dimensionShape.TextTopLeft.Y);
                yield return value;
                break;

            case CadHatchMarkup { Pattern: HatchPattern.Solid } solid:
                var fill = CreateOutline(solid.Boundary.Select(_page!.ToView).ToList(), AreaLineStyle.None, brush, 0);
                fill.Fill = brush;
                yield return fill;
                break;

            case CadHatchMarkup hatch:
                yield return CreateHatchLines(CadGeometry.HatchStrokes(hatch), brush, hatch.LineWidth);
                yield return CreateOutline(hatch.Boundary.Select(_page!.ToView).ToList(), AreaLineStyle.None, brush, 0); // clickable inside
                break;
        }
    }

    IEnumerable<FrameworkElement> CreateMeasureShapes(MeasureMarkup measure, Brush brush)
    {
        var points = measure.Points.Select(_page!.ToView).ToList();
        Shape outline;
        PointCollection outlinePoints;
        if (measure.Kind == MeasureKind.Area)
        {
            var polygon = new Polygon { Fill = Brushes.Transparent }; // clickable inside; no tint, as in the saved appearance
            (outline, outlinePoints) = (polygon, polygon.Points);
        }
        else
        {
            var polyline = new Polyline();
            (outline, outlinePoints) = (polyline, polyline.Points);
        }
        outline.Stroke = brush;
        outline.StrokeThickness = measure.LineWidth;
        outline.StrokeLineJoin = PenLineJoin.Round;
        // A distance has arrows at both ends: the line stops inside them, so its square ends don't show past the points.
        List<ViewPoint> drawnPoints = measure.Kind == MeasureKind.Distance
            ? [LabelGeometry.ArrowLineEnd(points[1], points[0], measure.LineWidth), LabelGeometry.ArrowLineEnd(points[0], points[1], measure.LineWidth)]
            : points;
        foreach (var point in drawnPoints) outlinePoints.Add(ToWpfPoint(point));
        yield return outline;
        if (measure.Kind == MeasureKind.Distance)
        {
            foreach (var (from, to) in new[] { (points[1], points[0]), (points[0], points[1]) })
            {
                var head = new Polygon { Fill = brush };
                foreach (var corner in LabelGeometry.ArrowHead(from, to, measure.LineWidth)) head.Points.Add(ToWpfPoint(corner));
                yield return head;
            }
        }
        // The same white box and text as the saved appearance.
        var box = measure.LabelBox(_page);
        var label = new Border
        {
            Width = box.Width, Height = box.Height, Background = Brushes.White,
            Padding = new Thickness(MeasureMarkup.LabelPadding, 1, 0, 0),
            Child = LabelText(measure.ValueText, MeasureMarkup.LabelFontSize, brush, measure.Font),
        };
        SetLeft(label, box.Left);
        SetTop(label, box.Top);
        yield return label;
    }


    void DrawInProgress()
    {
        var previewBrush = BrushFromHex(CurrentColor);
        var dashes = new DoubleCollection { 4, 3 };

        if (_cloudVertices.Count > 0)
        {
            var outline = new Polyline { Stroke = previewBrush, StrokeThickness = CurrentLineWidth, StrokeDashArray = dashes };
            foreach (var vertex in _cloudVertices.Append(_mousePosition)) outline.Points.Add(ToWpfPoint(vertex));
            Children.Add(outline);
        }
        if (_freehandStroke != null)
            Children.Add(Tool == MarkupTool.Pen
                ? CreateFreehandStroke(_freehandStroke, previewBrush, CurrentLineWidth, opacity: 1)
                : CreateFreehandStroke(_freehandStroke, previewBrush, HighlightMarkup.DefaultThickness, HighlightMarkup.Opacity));
        if (_markupBeingTyped is { } typing)
        {
            // As it will look, with the leader to where the text is being typed (the label is still empty).
            foreach (var shape in CreateShapes(typing)) Children.Add(shape);
            if (typing is CloudMarkup { Style: AreaLineStyle.None } hidden)
                Children.Add(CreateOutline(hidden.Outline(_page!), AreaLineStyle.Dashed, previewBrush, hidden.LineWidth));
        }
        if (_areaAwaitingTag is { } area)
        {
            var outline = area.Outline(_page!);
            // An area without an outline still shows where it is until its tag is placed.
            Children.Add(area.Style == AreaLineStyle.Cloud ? CreateCloud(outline, previewBrush, CurrentLineWidth)
                : CreateOutline(outline, area.Style == AreaLineStyle.None ? AreaLineStyle.Dashed : area.Style, previewBrush, CurrentLineWidth));
            var (_, onArea) = CloudGeometry.NearestPointOnEdges(outline, _mousePosition);
            Children.Add(new Line { X1 = onArea.X, Y1 = onArea.Y, X2 = _mousePosition.X, Y2 = _mousePosition.Y, Stroke = previewBrush, StrokeThickness = CurrentLineWidth, StrokeDashArray = dashes });
        }
        if (_firstPoint is { } first && Tool is MarkupTool.Rectangle or MarkupTool.Mask or MarkupTool.Text)
            Children.Add(CreateRectangle(ViewRect.Around([first, _mousePosition]), previewBrush, CurrentLineWidth, dashes,
                Tool != MarkupTool.Text && CurrentFill is { } fill ? BrushFromHex(fill) : null));
        if (_firstPoint is { } corner && IsAreaTool)
        {
            var rubberBand = new CloudMarkup { Vertices = [_page!.ToPdf(corner), _page.ToPdf(_mousePosition)], Shape = CurrentShape, Style = AreaLineStyle.Dashed };
            Children.Add(CreateOutline(rubberBand.Outline(_page), AreaLineStyle.Dashed, previewBrush, CurrentLineWidth));
        }
        if (_measurePoints.Count > 0)
        {
            var path = new Polyline { Stroke = previewBrush, StrokeThickness = CurrentLineWidth, StrokeDashArray = dashes };
            foreach (var point in _measurePoints.Append(_mousePosition)) path.Points.Add(ToWpfPoint(point));
            Children.Add(path);
        }
        if (_picks is { Count: > 0 } picks)
        {
            var ends = picks.Count == 1 ? [picks[0], _mousePosition] : picks;
            if (_pickPreview == PickPreview.Dimension) // calibrating: what was picked, and what it measures now
            {
                var dimension = new MeasureMarkup { Kind = MeasureKind.Distance, Points = ends.Select(_page!.ToPdf).ToList(), MmPerPoint = MmPerPoint };
                foreach (var shape in CreateMeasureShapes(dimension, Brushes.DodgerBlue)) Children.Add(shape);
            }
            else if (_pickPreview == PickPreview.Rectangle)
                Children.Add(CreateRectangle(ViewRect.Around(ends), Brushes.DodgerBlue, 1.5 / _screenUnitsPerPoint, dashes));
            else
            {
                Children.Add(new Line { X1 = ends[0].X, Y1 = ends[0].Y, X2 = ends[^1].X, Y2 = ends[^1].Y, Stroke = Brushes.DodgerBlue, StrokeThickness = 1.5 / _screenUnitsPerPoint, StrokeDashArray = dashes });
                foreach (var point in picks) Children.Add(CreateSnapMarker(new SnapPoint(point, SnapKind.Intersection)));
            }
        }
        if (_cadPoints.Count > 0 && Tool == MarkupTool.CadDimension)
        {
            // The dimension as it will be: to the cursor while picking its end, then its line following the cursor.
            var (end, onLine) = _cadPoints.Count == 1 ? (_mousePosition, _mousePosition) : (_cadPoints[1], _mousePosition);
            if (CloudGeometry.Distance(_cadPoints[0], end) >= 0.5)
                foreach (var shape in CreateShapes(NewDimension(_cadPoints[0], end, onLine))) Children.Add(shape);
        }
        else if (_cadPoints.Count > 0 && Tool == MarkupTool.CadCircle)
        {
            var center = _cadPoints[0];
            double radius = CloudGeometry.Distance(center, _mousePosition);
            Children.Add(CreateSnapMarker(new SnapPoint(center, SnapKind.Intersection))); // an X on the centre
            var circle = new Ellipse { Width = 2 * radius, Height = 2 * radius, Stroke = previewBrush, StrokeThickness = CurrentLineWidth, IsHitTestVisible = false };
            SetLeft(circle, center.X - radius);
            SetTop(circle, center.Y - radius);
            Children.Add(circle);
            Children.Add(new Line { X1 = center.X, Y1 = center.Y, X2 = _mousePosition.X, Y2 = _mousePosition.Y, Stroke = previewBrush, StrokeThickness = CurrentLineWidth, StrokeDashArray = dashes });
        }
        else if (_cadPoints.Count > 0)
        {
            var rubberBand = new Polyline { Stroke = previewBrush, StrokeThickness = CurrentLineWidth };
            bool line = Tool is MarkupTool.CadLine or MarkupTool.CadPolyline;
            var origin = _cadPoints[0];
            var mouse = line ? NextCadLinePoint(_cadPoints[^1], _mousePosition, _typedFirstMm) : OppositeCorner(origin, _mousePosition);
            List<ViewPoint> points = Tool switch
            {
                MarkupTool.CadLine => [origin, mouse],
                MarkupTool.CadRectangle => [origin, new(mouse.X, origin.Y), mouse, new(origin.X, mouse.Y), origin],
                _ => [.. _cadPoints, mouse],
            };
            foreach (var point in points) rubberBand.Points.Add(ToWpfPoint(point));
            Children.Add(rubberBand);
            {
                // The size next to the cursor, blue once typed (then it is fixed and only the direction follows the cursor).
                var label = new TextBlock
                {
                    Text = line ? DrawingScale.FormatLength(CloudGeometry.Distance(_cadPoints[^1], mouse) * MmPerPoint)
                        : $"{DrawingScale.FormatLength(Math.Abs(mouse.X - origin.X) * MmPerPoint)} × {DrawingScale.FormatLength(Math.Abs(mouse.Y - origin.Y) * MmPerPoint)}",
                    FontSize = 12 / _screenUnitsPerPoint, Padding = new Thickness(3 / _screenUnitsPerPoint, 0, 3 / _screenUnitsPerPoint, 0),
                    Foreground = (_typedFirstMm ?? _typedSecondMm) != null ? Brushes.DodgerBlue : Brushes.Black,
                    Background = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)), IsHitTestVisible = false,
                };
                SetLeft(label, _mousePosition.X + 14 / _screenUnitsPerPoint);
                SetTop(label, _mousePosition.Y + 14 / _screenUnitsPerPoint);
                Children.Add(label);
            }
        }
        if (_offsetSource is { } picked && !_markups.Contains(picked)) // a line of the PDF: glow it, as a picked CAD line glows
            foreach (var shape in CreateShapes(picked))
            {
                shape.Effect = SelectionGlow;
                Children.Add(shape);
            }
        if (_offsetSource is { } source && CadGeometry.Offset(source, OffsetDistanceMm / MmPerPoint, _page!.ToPdf(_mousePosition)) is { } offsetPreview)
            foreach (var shape in CreateShapes(offsetPreview))
            {
                shape.Opacity = 0.5;
                Children.Add(shape);
            }
        if (_snapMarker is { } marker) Children.Add(CreateSnapMarker(marker));
        if (_windowStart is { } windowStart)
        {
            var window = ViewRect.Around([windowStart, _mousePosition]);
            var rectangle = new Rectangle
            {
                Width = window.Width, Height = window.Height, Stroke = Brushes.DodgerBlue, StrokeThickness = 1 / _screenUnitsPerPoint,
                StrokeDashArray = [4, 3], Fill = TextSelectionBrush,
            };
            SetLeft(rectangle, window.Left);
            SetTop(rectangle, window.Top);
            Children.Add(rectangle);
        }
        if (_firstPoint is { } start && Tool is MarkupTool.Line or MarkupTool.Tag)
        {
            Children.Add(new Line
            {
                X1 = start.X, Y1 = start.Y, X2 = _mousePosition.X, Y2 = _mousePosition.Y,
                Stroke = previewBrush, StrokeThickness = CurrentLineWidth, StrokeDashArray = dashes,
            });
        }
    }

    static readonly Brush SnapBrush = CreateFrozenBrush(Color.FromRgb(0x00, 0xA8, 0x4F));

    /// <summary>The usual CAD symbols: square at an end point, X at an intersection, triangle at a midpoint, hourglass on a line.</summary>
    Shape CreateSnapMarker(SnapPoint snap)
    {
        double half = GripSize * 0.75 / _screenUnitsPerPoint;
        var (x, y) = (snap.Point.X, snap.Point.Y);
        Point[] corners = snap.Kind switch
        {
            SnapKind.Endpoint => [new(x - half, y - half), new(x + half, y - half), new(x + half, y + half), new(x - half, y + half), new(x - half, y - half)],
            SnapKind.Intersection => [new(x - half, y - half), new(x + half, y + half), new(x, y), new(x + half, y - half), new(x - half, y + half)],
            SnapKind.Midpoint => [new(x - half, y + half), new(x, y - half), new(x + half, y + half), new(x - half, y + half)],
            _ => [new(x - half, y - half), new(x + half, y - half), new(x - half, y + half), new(x + half, y + half), new(x - half, y - half)],
        };
        var symbol = new Polyline { Stroke = SnapBrush, StrokeThickness = 2 / _screenUnitsPerPoint, IsHitTestVisible = false };
        foreach (var corner in corners) symbol.Points.Add(corner);
        return symbol;
    }

    static Border CreateLabel(string text, double fontSize, ViewPoint topLeft, Brush brush, bool hasBorder, double lineWidth, string? font)
    {
        var box = LabelGeometry.Box(topLeft, text, fontSize, hasBorder, lineWidth, font);
        double borderWidth = hasBorder ? lineWidth : 0;
        var label = new Border
        {
            // Minimum rather than fixed size: if Arial is wider than the estimate, the box grows instead of cutting the text.
            MinWidth = box.Width,
            MinHeight = box.Height,
            BorderBrush = brush,
            BorderThickness = new Thickness(borderWidth),
            Padding = new Thickness(hasBorder ? LabelGeometry.CalloutPadding : 0),
            Background = Brushes.Transparent, // clickable inside, not just on the letters
            Child = LabelText(text, fontSize, brush, font),
        };
        SetLeft(label, box.Left);
        SetTop(label, box.Top);
        return label;
    }

    /// <summary>
    /// A label's text: Arial as a text block, any other font as the outlines of its letters, the same ones the saved
    /// file fills, so the two match.
    /// </summary>
    static FrameworkElement LabelText(string text, double fontSize, Brush brush, string? font) => font == null
        ? new TextBlock
        {
            Text = text,
            FontFamily = LabelFont,
            FontSize = fontSize,
            Foreground = brush,
            LineHeight = fontSize * TextLayout.LineHeightPerFontSize,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        }
        : new Path { Data = SystemFontSource.Letters(font, text, fontSize), Fill = brush };

    /// <summary>A line from <paramref name="from"/> to <paramref name="to"/>, ending at <paramref name="to"/> in a filled arrow or dot if asked.</summary>
    static IEnumerable<Shape> CreateLeader(ViewPoint from, ViewPoint to, LineEnding ending, Brush brush, double lineWidth)
    {
        var lineEnd = ending == LineEnding.Arrow ? LabelGeometry.ArrowLineEnd(from, to, lineWidth) : to; // the line's square end stays inside the head
        yield return new Line { X1 = from.X, Y1 = from.Y, X2 = lineEnd.X, Y2 = lineEnd.Y, Stroke = brush, StrokeThickness = lineWidth };
        if (ending == LineEnding.Arrow)
        {
            var arrowHead = new Polygon { Fill = brush };
            foreach (var corner in LabelGeometry.ArrowHead(from, to, lineWidth)) arrowHead.Points.Add(ToWpfPoint(corner));
            yield return arrowHead;
        }
        else if (ending == LineEnding.Dot)
        {
            double radius = LabelGeometry.DotRadius(lineWidth);
            var dot = new Ellipse { Width = 2 * radius, Height = 2 * radius, Fill = brush };
            SetLeft(dot, to.X - radius);
            SetTop(dot, to.Y - radius);
            yield return dot;
        }
    }

    /// <summary>A rectangle stroked along <paramref name="middleOfStroke"/>, as in the saved file. Clickable on its outline, and inside if filled.</summary>
    static Rectangle CreateRectangle(ViewRect middleOfStroke, Brush? brush, double lineWidth, DoubleCollection? dashes, Brush? fill = null)
    {
        var outerEdge = middleOfStroke.Inflate(lineWidth / 2);
        var rectangle = new Rectangle { Width = outerEdge.Width, Height = outerEdge.Height, Stroke = brush, StrokeThickness = lineWidth, StrokeDashArray = dashes, Fill = fill };
        SetLeft(rectangle, outerEdge.Left);
        SetTop(rectangle, outerEdge.Top);
        return rectangle;
    }

    // shortcut: WPF has no multiply blending, so on screen black lines under a highlight look slightly faded; the saved PDF multiplies properly.
    static Polyline CreateFreehandStroke(IEnumerable<ViewPoint> points, Brush brush, double thickness, double opacity)
    {
        var stroke = new Polyline
        {
            Stroke = brush,
            StrokeThickness = thickness,
            Opacity = opacity,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        foreach (var point in points) stroke.Points.Add(ToWpfPoint(point));
        return stroke;
    }

    Path CreateCloud(List<ViewPoint> vertices, Brush brush, double lineWidth)
    {
        var figure = new PathFigure { StartPoint = ToWpfPoint(vertices[0]), IsClosed = true };
        foreach (var arc in CloudGeometry.Arcs(vertices, CloudGeometry.ArcLength(_page!)))
            figure.Segments.Add(new BezierSegment(ToWpfPoint(arc.Control1), ToWpfPoint(arc.Control2), ToWpfPoint(arc.End), true));
        return new Path
        {
            Data = new PathGeometry([figure]),
            Stroke = brush,
            StrokeThickness = lineWidth,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent, // clickable inside the cloud
        };
    }

    /// <summary>A solid, dashed or invisible outline. Clickable inside, like a cloud.</summary>
    Path CreateHatchLines(List<(PdfPoint A, PdfPoint B)> strokes, Brush brush, double lineWidth)
    {
        var lines = new StreamGeometry();
        using (var context = lines.Open())
            foreach (var (from, to) in strokes)
            {
                context.BeginFigure(ToWpfPoint(_page!.ToView(from)), isFilled: false, isClosed: false);
                context.LineTo(ToWpfPoint(_page.ToView(to)), isStroked: true, isSmoothJoin: false);
            }
        lines.Freeze();
        return new Path { Data = lines, Stroke = brush, StrokeThickness = lineWidth };
    }

    static Polygon CreateOutline(List<ViewPoint> corners, AreaLineStyle style, Brush brush, double lineWidth)
    {
        var outline = new Polygon
        {
            Stroke = style == AreaLineStyle.None ? null : brush,
            StrokeThickness = lineWidth,
            StrokeDashArray = style == AreaLineStyle.Dashed ? new DoubleCollection(CloudMarkup.Dashes) : null,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent,
        };
        foreach (var corner in corners) outline.Points.Add(ToWpfPoint(corner));
        return outline;
    }

    static SolidColorBrush BrushFromHex(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    static ViewPoint ToViewPoint(Point point) => new(point.X, point.Y);
    static Point ToWpfPoint(ViewPoint point) => new(point.X, point.Y);
}
