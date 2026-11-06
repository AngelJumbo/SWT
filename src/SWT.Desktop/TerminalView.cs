using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SWT.Core;

namespace SWT.Desktop;

public sealed class TerminalView : FrameworkElement
{
    private readonly record struct CellPosition(int Row, int Column);
    private enum BoxStroke { None, Light, Heavy, Double, LightDash, HeavyDash }
    private readonly record struct BoxGlyph(
        BoxStroke Left, BoxStroke Right, BoxStroke Up, BoxStroke Down,
        bool DiagonalDown = false, bool DiagonalUp = false, bool Rounded = false);

    private const double PadX = 8, PadY = 4;
    private Appearance _appearance;
    private Typeface _typeface = null!;
    private GlyphTypeface? _normalGlyphs, _boldGlyphs, _italicGlyphs, _boldItalicGlyphs;
    private Brush _foreground = null!, _background = null!, _backgroundText = null!, _cursor = null!, _thumb = null!, _selection = null!;
    private Pen _compositionPen = null!;
    private string _compositionText = "";
    private double _pixelsPerDip = 1;
    private readonly Dictionary<int, Brush> _brushes = new();
    private readonly Dictionary<(int Value, bool IsRgb, bool Background), Brush> _dimBrushes = new();
    private readonly Dictionary<(string Text, int Color, bool Rgb, TerminalTextAttributes Attributes), FormattedText> _glyphs = new();
    // Per-line drawings keyed by line identity; rebuilt only when a line's version changes.
    private Dictionary<TerminalLine, (int Version, Drawing Drawing)> _lineCache = new(ReferenceEqualityComparer.Instance);
    private Dictionary<TerminalLine, (int Version, Drawing Drawing)> _nextCache = new(ReferenceEqualityComparer.Instance);
    private int _cachedColumns = -1;
    private TranslateTransform[] _rowTransforms = [];
    private ushort[] _glyphBuffer = new ushort[256];
    private bool _dirty = true;
    private int _scrollOffset;
    private long _seenScrolled;
    private long _seenResize;
    private int _wheel;
    private CellPosition? _selectionAnchor, _selectionEnd;
    private bool _selecting, _hasSelection;
    private string _searchQuery = "";
    private TerminalLine? _searchLine;
    private int _searchIndex, _searchVersion;
    private bool _searchRegex;
    private readonly Dictionary<TerminalLine, (int Version, int Column, int Length)> _searchSpans = [];
    internal string? SearchError { get; private set; }

    public TerminalScreen Screen { get; }
    public double CellWidth { get; private set; }
    public double CellHeight { get; private set; }
    public int LinesBuilt { get; private set; }
    internal bool IsSelecting => _selecting;
    internal string? SelectedText => _hasSelection ? GetSelectedText() : null;
    internal string CopyText => SelectedText ?? string.Join(Environment.NewLine,
        Enumerable.Range(0, Screen.Rows).Select(y => Screen.GetViewportLine(y, ScrollOffset)
            .Text(Screen.Columns).TrimEnd()));

    /// <summary>Lines scrolled back from the live screen; 0 follows output.</summary>
    public int ScrollOffset { get { Sync(); return _scrollOffset; } }

    public TerminalView(TerminalScreen screen, Appearance appearance)
    {
        Screen = screen;
        _appearance = appearance;
        _seenScrolled = screen.ScrolledLines;
        _seenResize = screen.ResizeVersion;
        Focusable = true;
        ClipToBounds = true;
        InputMethod.SetIsInputMethodEnabled(this, true);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        Apply(appearance);
        AddHandler(TextCompositionManager.PreviewTextInputStartEvent,
            new TextCompositionEventHandler((_, e) => SetCompositionText(e.TextComposition.CompositionText)), true);
        AddHandler(TextCompositionManager.PreviewTextInputUpdateEvent,
            new TextCompositionEventHandler((_, e) => SetCompositionText(e.TextComposition.CompositionText)), true);
        AddHandler(TextCompositionManager.TextInputEvent,
            new TextCompositionEventHandler((_, _) => SetCompositionText("")), true);
        LostKeyboardFocus += (_, _) => SetCompositionText("");
        Loaded += (_, _) => CompositionTarget.Rendering += RenderFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= RenderFrame;
    }

    public void Apply(Appearance appearance)
    {
        _appearance = appearance;
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _typeface = new Typeface(appearance.FontFamily);
        _typeface.TryGetGlyphTypeface(out _normalGlyphs);
        new Typeface(_typeface.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal).TryGetGlyphTypeface(out _boldGlyphs);
        new Typeface(_typeface.FontFamily, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out _italicGlyphs);
        new Typeface(_typeface.FontFamily, FontStyles.Italic, FontWeights.Bold, FontStretches.Normal).TryGetGlyphTypeface(out _boldItalicGlyphs);
        _foreground = Brush(appearance.Foreground);
        var background = (SolidColorBrush)Brush(appearance.Background);
        _background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(appearance.BackgroundOpacity * 255),
            background.Color.R, background.Color.G, background.Color.B));
        _background.Freeze();
        _backgroundText = Brush(appearance.Background);
        _cursor = Brush(appearance.Cursor);
        _compositionPen = new Pen(_foreground, Math.Max(1, _pixelsPerDip));
        _compositionPen.Freeze();
        _thumb = _foreground.CloneCurrentValue();
        _thumb.Opacity = .45;
        _thumb.Freeze();
        _selection = Brush("#81A1C1").CloneCurrentValue();
        _selection.Opacity = .45;
        _selection.Freeze();
        _brushes.Clear();
        _dimBrushes.Clear();
        _glyphs.Clear();
        _lineCache.Clear();
        var metrics = Text("M", _foreground, false);
        CellWidth = Math.Ceiling(metrics.WidthIncludingTrailingWhitespace);
        CellHeight = Math.Ceiling(metrics.Height + 2);
        _rowTransforms = [];
        MarkDirty();
    }

    internal const double MinFontSize = 6, MaxFontSize = 72;

    internal double FontSize => _appearance.FontSize;

    internal void SetFontSize(double size) =>
        Apply(_appearance with { FontSize = Math.Clamp(size, MinFontSize, MaxFontSize) });

    public void MarkDirty() => _dirty = true;

    internal void SetCompositionText(string text)
    {
        if (_compositionText == text) return;
        _compositionText = text;
        MarkDirty();
    }

    internal void ScreenChanged()
    {
        var scrolled = Screen.ScrolledLines - _seenScrolled;
        if ((_hasSelection || _selecting) && _scrollOffset == 0 && scrolled > 0)
        {
            _scrollOffset = (int)Math.Min(scrolled, Screen.ScrollbackCount);
            _seenScrolled = Screen.ScrolledLines;
        }
        Sync();
        MarkDirty();
    }

    public void ScrollBy(int lines)
    {
        Sync();
        var next = (int)Math.Clamp((long)_scrollOffset + lines, 0, Screen.ScrollbackCount);
        if (next == _scrollOffset) return;
        ClearSelection();
        _scrollOffset = next;
        MarkDirty();
    }

    public void ScrollToBottom()
    {
        ClearSelection();
        ScrollBy(int.MinValue);
    }
    public void ScrollToTop() => ScrollBy(int.MaxValue);
    public int PageLines => Math.Max(1, Screen.Rows - 1);

    internal void BeginSelection(Point point)
    {
        ClearSelection();
        _selectionAnchor = _selectionEnd = CellAt(point);
        _selecting = true;
        CaptureMouse();
        MarkDirty();
    }

    internal (int Column, int Row) MouseCellAt(Point point)
    {
        var cell = CellAt(point);
        return (cell.Column, cell.Row);
    }

    internal void UpdateSelection(Point point)
    {
        if (!_selecting || _selectionAnchor is not { } anchor) return;
        var end = CellAt(point);
        if (end != anchor) _hasSelection = true;
        _selectionEnd = end;
        MarkDirty();
    }

    internal void EndSelection(Point point)
    {
        if (!_selecting) return;
        UpdateSelection(point);
        _selecting = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        MarkDirty();
    }

    internal void ClearSelection()
    {
        _selectionAnchor = _selectionEnd = null;
        _selecting = _hasSelection = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        MarkDirty();
    }

    internal void ClearSearch()
    {
        _searchQuery = "";
        _searchLine = null;
        _searchSpans.Clear();
        SearchError = null;
        MarkDirty();
    }

    internal bool FindText(string query, bool previous = false, bool regex = false)
    {
        Sync();
        SearchError = null;
        if (query.Length == 0) { ClearSearch(); return false; }
        var history = Screen.ScrollbackCount;
        var count = history + Screen.Rows;
        var rows = new List<(TerminalLine Line, int Start, int Length)>(count);
        var builder = new StringBuilder();
        var anchor = -1;
        for (var row = 0; row < count; row++)
        {
            var line = row < history ? Screen.GetScrollbackLine(row) : Screen.GetLine(row - history);
            var text = line.Text(line.IsWrapped ? line.WrappedLength : line.Length);
            if (!line.IsWrapped) text = text.TrimEnd(' ');
            if (builder.Length + text.Length + 1 > 8 * 1024 * 1024)
            {
                SearchError = "Search buffer exceeds 8 Mi characters; reduce retained scrollback.";
                _searchSpans.Clear();
                MarkDirty();
                return false;
            }
            if (query == _searchQuery && regex == _searchRegex && ReferenceEquals(line, _searchLine) &&
                line.Version == _searchVersion) anchor = builder.Length + _searchIndex;
            rows.Add((line, builder.Length, text.Length));
            builder.Append(text);
            if (!line.IsWrapped && row + 1 < count) builder.Append('\n');
        }
        var buffer = builder.ToString();
        (int Index, int Length)? first = null, last = null, next = null;
        try
        {
            var pattern = new Regex(regex ? query : $"(?=({Regex.Escape(query.Replace("\\n", "\n"))}))",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline,
                TimeSpan.FromMilliseconds(100));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var match = pattern.Match(buffer); match.Success; match = match.NextMatch())
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(2))
                    throw new RegexMatchTimeoutException(buffer, query, TimeSpan.FromSeconds(2));
                Group matched = regex ? match : match.Groups[1];
                if (matched.Length == 0) continue;
                var candidate = (matched.Index, matched.Length);
                first ??= candidate;
                last = candidate;
                if (previous && anchor >= 0 && match.Index < anchor) next = candidate;
                else if (!previous && anchor >= 0 && next is null && match.Index > anchor) next = candidate;
            }
        }
        catch (ArgumentException ex) { SearchError = $"Invalid regex: {ex.Message}"; }
        catch (RegexMatchTimeoutException) { SearchError = "Search timed out; simplify the pattern."; }
        _searchSpans.Clear();
        _searchQuery = query;
        _searchRegex = regex;
        var found = next ?? (previous || anchor < 0 ? last : first);
        if (SearchError is not null || found is not { } result)
        {
            _searchLine = null;
            MarkDirty();
            return false;
        }
        ClearSelection();
        var firstRow = -1;
        for (var row = 0; row < rows.Count; row++)
        {
            var segment = rows[row];
            if (result.Index >= segment.Start &&
                (result.Index < segment.Start + segment.Length ||
                 result.Index == segment.Start + segment.Length && !segment.Line.IsWrapped) && firstRow < 0)
            {
                firstRow = row;
                _searchLine = segment.Line;
                _searchVersion = segment.Line.Version;
                _searchIndex = result.Index - segment.Start;
            }
            var start = Math.Max(result.Index, segment.Start) - segment.Start;
            var end = Math.Min(result.Index + result.Length, segment.Start + segment.Length) - segment.Start;
            if (end <= start) continue;
            var offset = 0;
            var firstColumn = -1;
            var endColumn = 0;
            for (var column = 0; column < segment.Line.Length; column++)
            {
                var cell = segment.Line[column];
                if (cell.Width == 0) continue;
                var length = (cell.Text ?? " ").Length;
                if (offset < end && offset + length > start)
                {
                    if (firstColumn < 0) firstColumn = column;
                    endColumn = column + Math.Max(1, cell.Width);
                }
                offset += length;
            }
            if (firstColumn >= 0)
                _searchSpans[segment.Line] = (segment.Line.Version, firstColumn, endColumn - firstColumn);
        }
        _scrollOffset = Math.Clamp(history - firstRow, 0, history);
        MarkDirty();
        return true;
    }

    private CellPosition CellAt(Point point) => new(
        Math.Clamp((int)Math.Floor((point.Y - PadY) / CellHeight), 0, Screen.Rows - 1),
        Math.Clamp((int)Math.Floor((point.X - PadX) / CellWidth), 0, Screen.Columns - 1));

    private string GetSelectedText()
    {
        var (first, last) = OrderedSelection();
        var text = new StringBuilder();
        for (var row = first.Row; row <= last.Row; row++)
        {
            var line = Screen.GetViewportLine(row, ScrollOffset);
            var start = row == first.Row ? first.Column : 0;
            var end = row == last.Row ? last.Column + 1 : Screen.Columns;
            if (line.IsWrapped) end = Math.Min(end, line.WrappedLength);
            var selected = line.Text(start, Math.Max(0, end - start));
            text.Append(line.IsWrapped ? selected : selected.TrimEnd());
            if (row < last.Row && !line.IsWrapped) text.Append(Environment.NewLine);
        }
        return text.ToString();
    }

    private (CellPosition First, CellPosition Last) OrderedSelection()
    {
        var anchor = _selectionAnchor!.Value;
        var end = _selectionEnd!.Value;
        return anchor.Row < end.Row || anchor.Row == end.Row && anchor.Column <= end.Column
            ? (anchor, end) : (end, anchor);
    }

    private bool TryGetSelectionSpan(int row, out int start, out int length)
    {
        if (!_hasSelection || _selectionAnchor is null || _selectionEnd is null)
        {
            start = length = 0;
            return false;
        }
        var (first, last) = OrderedSelection();
        if (row < first.Row || row > last.Row)
        {
            start = length = 0;
            return false;
        }
        start = row == first.Row ? first.Column : 0;
        var afterEnd = row == last.Row ? last.Column + 1 : Screen.Columns;
        var line = Screen.GetViewportLine(row, ScrollOffset);
        if (start > 0 && start < line.Length && line[start].Width == 0) start--;
        if (afterEnd > 0 && afterEnd <= line.Length && line[afterEnd - 1].Width == 2)
            afterEnd = Math.Min(Screen.Columns, afterEnd + 1);
        length = afterEnd - start;
        return length > 0;
    }

    // Keeps scrolled-back content anchored while new lines enter scrollback.
    private void Sync()
    {
        if (_seenResize != Screen.ResizeVersion)
        {
            _seenResize = Screen.ResizeVersion;
            _seenScrolled = Screen.ScrolledLines;
            _scrollOffset = Math.Clamp(_scrollOffset, 0, Screen.ScrollbackCount);
            ClearSelection();
            ClearSearch();
        }
        var total = Screen.ScrolledLines;
        if (_scrollOffset > 0) _scrollOffset = (int)Math.Min(_scrollOffset + (total - _seenScrolled), int.MaxValue);
        _seenScrolled = total;
        _scrollOffset = Math.Clamp(_scrollOffset, 0, Screen.ScrollbackCount);
    }

    private void RenderFrame(object? sender, EventArgs e)
    {
        if (!_dirty) return;
        _dirty = false;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        // 3 lines per 120-unit notch; smaller precision-touchpad deltas accumulate.
        _wheel += e.Delta;
        var lines = _wheel / 40;
        _wheel %= 40;
        if (lines != 0) ScrollBy(lines);
        e.Handled = true;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Apply(_appearance);
        InvalidateMeasure();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        Sync();
        var rows = Screen.Rows;
        var columns = Screen.Columns;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var contentBottom = Math.Min(ActualHeight, rows * CellHeight + PadY);
        if (_cachedColumns != columns) { _lineCache.Clear(); _cachedColumns = columns; }
        if (_rowTransforms.Length < rows)
        {
            _rowTransforms = new TranslateTransform[rows];
            for (var y = 0; y < rows; y++)
            {
                _rowTransforms[y] = new TranslateTransform(PadX, PadY + y * CellHeight);
                _rowTransforms[y].Freeze();
            }
        }
        for (var y = 0; y < rows; y++)
        {
            var line = Screen.GetViewportLine(y, _scrollOffset);
            if (!_lineCache.TryGetValue(line, out var entry) || entry.Version != line.Version)
            {
                entry = (line.Version, BuildLine(line, columns));
                LinesBuilt++;
            }
            _nextCache[line] = entry;
            dc.PushTransform(_rowTransforms[y]);
            dc.DrawDrawing(entry.Drawing);
            if (_searchSpans.TryGetValue(line, out var span) && line.Version == span.Version)
            {
                dc.PushOpacity(.4);
                dc.DrawRectangle(Brushes.DarkGoldenrod, null, new Rect(span.Column * CellWidth, 0,
                    span.Length * CellWidth, CellHeight));
                dc.Pop();
            }
            if (TryGetSelectionSpan(y, out var selectionStart, out var selectionLength))
                dc.DrawRectangle(_selection, null, new Rect(selectionStart * CellWidth, 0,
                    selectionLength * CellWidth, CellHeight));
            dc.Pop();
        }
        (_lineCache, _nextCache) = (_nextCache, _lineCache);
        _nextCache.Clear();
        if (_scrollOffset == 0 && Screen.CursorVisible)
        {
            if (_compositionText.Length > 0)
            {
                var x = Screen.CursorX * CellWidth + PadX;
                var y = Screen.CursorY * CellHeight + PadY;
                var visibleWidth = Math.Max(0, ActualWidth - x - PadX);
                var composition = Text(_compositionText, _foreground, false);
                dc.PushClip(new RectangleGeometry(new Rect(x, y, visibleWidth, CellHeight)));
                dc.DrawText(composition, new Point(x, y));
                var underlineY = y + CellHeight - Math.Max(1, _pixelsPerDip);
                dc.DrawLine(_compositionPen, new Point(x, underlineY),
                    new Point(Math.Min(x + composition.WidthIncludingTrailingWhitespace, x + visibleWidth), underlineY));
                dc.Pop();
            }
            if (IsKeyboardFocused)
                dc.DrawRectangle(_cursor, null, new Rect(Screen.CursorX * CellWidth + PadX,
                    (Screen.CursorY + 1) * CellHeight + PadY - 2, CellWidth, 2));
        }
        if (_scrollOffset > 0 && contentBottom > PadY)
        {
            var track = contentBottom - PadY;
            var total = (double)Screen.ScrollbackCount + rows;
            var height = Math.Max(12, track * rows / total);
            var top = PadY + (track - height) * (Screen.ScrollbackCount - _scrollOffset) / Math.Max(1, Screen.ScrollbackCount);
            dc.DrawRectangle(_thumb, null, new Rect(Math.Max(0, ActualWidth - 5), top, 3, height));
        }
    }

    private Drawing BuildLine(TerminalLine line, int columns)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            var width = Math.Min(line.Length, columns);
            for (var x = 0; x < width;)
            {
                var cell = line[x];
                var background = cell.Background;
                var reverse = cell.Attributes.HasFlag(TerminalTextAttributes.Reverse);
                var end = x + 1;
                while (end < width && line[end].Background == background &&
                    (!reverse || line[end].Foreground == cell.Foreground) &&
                    line[end].Attributes.HasFlag(TerminalTextAttributes.Reverse) == reverse) end++;
                if (background.Value >= 0 || reverse)
                    dc.DrawRectangle(reverse
                            ? Resolve(cell.Foreground, _foreground)
                            : Resolve(background, _background, background: true), null,
                        new Rect(x * CellWidth, 0, (end - x) * CellWidth, CellHeight));
                x = end;
            }
            for (var x = 0; x < width;)
            {
                var cell = line[x];
                if (cell.Width == 0) { x++; continue; }
                var foreground = ForegroundBrush(cell);
                if (TryDrawBox(dc, cell, x, foreground) || TryDrawBlock(dc, cell, x, foreground))
                {
                    DrawDecorations(dc, cell, x, cell.Width, foreground);
                    x++;
                    continue;
                }
                var face = GlyphTypefaceFor(cell.Attributes);
                var start = x;
                var count = 0;
                var ink = false;
                while (face is not null && x < width)
                {
                    var next = line[x];
                    var value = next.Text ?? " ";
                    if (next.Width != 1 || next.Foreground != cell.Foreground ||
                        next.Background != cell.Background || next.Attributes != cell.Attributes || value.Length != 1 ||
                        value[0] is >= '\u2500' and <= '\u257F' or >= '\u2580' and <= '\u259F' ||
                        !face.CharacterToGlyphMap.TryGetValue(value[0], out var glyph)) break;
                    if (count == _glyphBuffer.Length) Array.Resize(ref _glyphBuffer, count * 2);
                    _glyphBuffer[count++] = glyph;
                    ink |= value[0] != ' ';
                    x++;
                }
                if (count > 0)
                {
                    if (ink)
                    {
                        var advances = new double[count];
                        Array.Fill(advances, CellWidth);
                        var run = new GlyphRun(face, 0, false, _appearance.FontSize, (float)_pixelsPerDip,
                            _glyphBuffer.AsSpan(0, count).ToArray(),
                            new Point(start * CellWidth, face!.Baseline * _appearance.FontSize),
                            advances, null, null, null, null, null, null);
                        dc.DrawGlyphRun(foreground, run);
                    }
                    DrawDecorations(dc, cell, start, count, foreground);
                    continue;
                }
                x++;
                var textValue = cell.Text ?? " ";
                if (textValue != " ")
                {
                    var reverse = cell.Attributes.HasFlag(TerminalTextAttributes.Reverse);
                    var effectiveForeground = reverse ? cell.Background : cell.Foreground;
                    var key = (textValue, effectiveForeground.Value, effectiveForeground.IsRgb, cell.Attributes);
                    if (!_glyphs.TryGetValue(key, out var text))
                    {
                        if (_glyphs.Count >= 4096) _glyphs.Clear();
                        _glyphs[key] = text = Text(textValue, foreground, cell.Attributes);
                    }
                    dc.PushClip(new RectangleGeometry(new Rect(start * CellWidth, 0,
                        Math.Min(cell.Width, columns - start) * CellWidth, CellHeight)));
                    dc.DrawText(text, new Point(start * CellWidth, 0));
                    dc.Pop();
                }
                DrawDecorations(dc, cell, start, cell.Width, foreground);
            }
        }
        group.Freeze();
        return group;
    }

    private bool TryDrawBox(DrawingContext dc, Cell cell, int column, Brush brush)
    {
        if (cell.Text is not { Length: 1 } text || !TryGetBoxGlyph(text[0], out var glyph))
            return false;
        var left = column * CellWidth;
        var centerX = (column + .5) * CellWidth;
        var centerY = CellHeight / 2;
        if (glyph.Rounded)
        {
            var figure = new PathFigure { IsClosed = false, IsFilled = false };
            Point start, end;
            if (glyph.Right != BoxStroke.None && glyph.Down != BoxStroke.None)
                (start, end) = (new(centerX, CellHeight), new(left + CellWidth, centerY));
            else if (glyph.Left != BoxStroke.None && glyph.Down != BoxStroke.None)
                (start, end) = (new(centerX, CellHeight), new(left, centerY));
            else if (glyph.Left != BoxStroke.None && glyph.Up != BoxStroke.None)
                (start, end) = (new(centerX, 0), new(left, centerY));
            else
                (start, end) = (new(centerX, 0), new(left + CellWidth, centerY));
            const double arcControl = .55228475;
            var controlY = Math.Abs(end.Y - start.Y) * arcControl;
            var controlX = Math.Abs(end.X - start.X) * arcControl;
            figure.StartPoint = start;
            figure.Segments.Add(new BezierSegment(
                new Point(start.X, start.Y + Math.Sign(end.Y - start.Y) * controlY),
                new Point(end.X - Math.Sign(end.X - start.X) * controlX, end.Y),
                end, true));
            var geometry = new PathGeometry([figure]);
            geometry.Freeze();
            dc.DrawGeometry(null, BoxPen(brush, BoxStroke.Light, CellHeight), geometry);
            return true;
        }
        DrawBoxArm(dc, brush, centerX, centerY, 0, -1, glyph.Up);
        DrawBoxArm(dc, brush, centerX, centerY, 0, 1, glyph.Down);
        DrawBoxArm(dc, brush, centerX, centerY, -1, 0, glyph.Left);
        DrawBoxArm(dc, brush, centerX, centerY, 1, 0, glyph.Right);
        if (glyph.DiagonalDown)
            dc.DrawLine(BoxPen(brush, BoxStroke.Light, CellHeight), new Point(left, 0),
                new Point(left + CellWidth, CellHeight));
        if (glyph.DiagonalUp)
            dc.DrawLine(BoxPen(brush, BoxStroke.Light, CellHeight), new Point(left, CellHeight),
                new Point(left + CellWidth, 0));
        return true;
    }

    private void DrawBoxArm(DrawingContext dc, Brush brush, double centerX, double centerY,
        int directionX, int directionY, BoxStroke stroke)
    {
        if (stroke == BoxStroke.None) return;
        var horizontal = directionY == 0;
        var offset = horizontal ? CellHeight * .11 : CellWidth * .11;
        var width = horizontal ? CellHeight : CellWidth;
        var reach = horizontal ? CellWidth : CellHeight;
        var endX = centerX + directionX * reach / 2;
        var endY = centerY + directionY * reach / 2;
        if (stroke == BoxStroke.Double)
        {
            for (var side = -1; side <= 1; side += 2)
            {
                var shiftX = horizontal ? 0 : side * offset;
                var shiftY = horizontal ? side * offset : 0;
                dc.DrawLine(BoxPen(brush, BoxStroke.Light, width),
                    new Point(centerX + shiftX, centerY + shiftY),
                    new Point(endX + shiftX, endY + shiftY));
            }
            return;
        }

        var pen = BoxPen(brush, stroke, width);
        dc.DrawLine(pen, new Point(centerX, centerY), new Point(endX, endY));
    }

    private static Pen BoxPen(Brush brush, BoxStroke stroke, double cellDimension)
    {
        var width = stroke is BoxStroke.Heavy or BoxStroke.HeavyDash
            ? cellDimension * .22
            : Math.Max(1, cellDimension * .085);
        var pen = new Pen(brush, width);
        if (stroke is BoxStroke.LightDash or BoxStroke.HeavyDash)
            pen.DashStyle = new DashStyle([4, 2], 0);
        pen.StartLineCap = PenLineCap.Square;
        pen.EndLineCap = PenLineCap.Square;
        pen.Freeze();
        return pen;
    }

    private static bool TryGetBoxGlyph(char ch, out BoxGlyph glyph)
    {
        const BoxStroke n = BoxStroke.None, l = BoxStroke.Light, h = BoxStroke.Heavy;
        const BoxStroke d = BoxStroke.Double, dl = BoxStroke.LightDash, dh = BoxStroke.HeavyDash;
        glyph = ch switch
        {
            '\u2500' => new(l, l, n, n), '\u2501' => new(h, h, n, n),
            '\u2502' => new(n, n, l, l), '\u2503' => new(n, n, h, h),
            '\u2504' => new(dl, dl, n, n), '\u2505' => new(dh, dh, n, n),
            '\u2506' => new(n, n, dl, dl), '\u2507' => new(n, n, dh, dh),
            '\u2508' => new(dl, dl, n, n), '\u2509' => new(dh, dh, n, n),
            '\u250A' => new(n, n, dl, dl), '\u250B' => new(n, n, dh, dh),
            '\u250C' => new(n, l, n, l), '\u250D' => new(n, h, n, l),
            '\u250E' => new(n, l, n, h), '\u250F' => new(n, h, n, h),
            '\u2510' => new(l, n, n, l), '\u2511' => new(h, n, n, l),
            '\u2512' => new(l, n, n, h), '\u2513' => new(h, n, n, h),
            '\u2514' => new(n, l, l, n), '\u2515' => new(n, h, l, n),
            '\u2516' => new(n, l, h, n), '\u2517' => new(n, h, h, n),
            '\u2518' => new(l, n, l, n), '\u2519' => new(h, n, l, n),
            '\u251A' => new(l, n, h, n), '\u251B' => new(h, n, h, n),
            '\u251C' => new(n, l, l, l), '\u251D' => new(n, h, l, l),
            '\u251E' => new(n, l, h, l), '\u251F' => new(n, l, l, h),
            '\u2520' => new(n, l, h, h), '\u2521' => new(n, h, h, l),
            '\u2522' => new(n, h, l, h), '\u2523' => new(n, h, h, h),
            '\u2524' => new(l, n, l, l), '\u2525' => new(h, n, l, l),
            '\u2526' => new(l, n, h, l), '\u2527' => new(l, n, l, h),
            '\u2528' => new(l, n, h, h), '\u2529' => new(h, n, h, l),
            '\u252A' => new(h, n, l, h), '\u252B' => new(h, n, h, h),
            '\u252C' => new(l, l, n, l), '\u252D' => new(h, l, n, l),
            '\u252E' => new(l, h, n, l), '\u252F' => new(h, h, n, l),
            '\u2530' => new(l, l, n, h), '\u2531' => new(h, l, n, h),
            '\u2532' => new(l, h, n, h), '\u2533' => new(h, h, n, h),
            '\u2534' => new(l, l, l, n), '\u2535' => new(h, l, l, n),
            '\u2536' => new(l, h, l, n), '\u2537' => new(h, h, l, n),
            '\u2538' => new(l, l, h, n), '\u2539' => new(h, l, h, n),
            '\u253A' => new(l, h, h, n), '\u253B' => new(h, h, h, n),
            '\u253C' => new(l, l, l, l), '\u253D' => new(h, l, l, l),
            '\u253E' => new(l, h, l, l), '\u253F' => new(h, h, l, l),
            '\u2540' => new(l, l, h, l), '\u2541' => new(l, l, l, h),
            '\u2542' => new(l, l, h, h), '\u2547' => new(h, h, h, l),
            '\u2548' => new(h, h, l, h), '\u2549' => new(l, h, h, h),
            '\u254A' => new(l, h, h, h), '\u254B' => new(h, h, h, h),
            '\u254C' => new(dl, dl, n, n), '\u254D' => new(dh, dh, n, n),
            '\u254E' => new(n, n, dl, dl), '\u254F' => new(n, n, dh, dh),
            '\u2550' => new(d, d, n, n), '\u2551' => new(n, n, d, d),
            '\u2552' => new(n, d, n, l), '\u2553' => new(n, l, n, d),
            '\u2554' => new(n, d, n, d), '\u2555' => new(d, n, n, l),
            '\u2556' => new(l, n, n, d), '\u2557' => new(d, n, n, d),
            '\u2558' => new(n, d, l, n), '\u2559' => new(n, l, d, n),
            '\u255A' => new(n, d, d, n), '\u255B' => new(d, n, l, n),
            '\u255C' => new(l, n, d, n), '\u255D' => new(d, n, d, n),
            '\u255E' => new(n, d, l, l), '\u255F' => new(n, l, d, d),
            '\u2560' => new(n, d, d, d), '\u2561' => new(d, n, l, l),
            '\u2562' => new(l, n, d, d), '\u2563' => new(d, n, d, d),
            '\u2564' => new(l, l, n, d), '\u2565' => new(d, d, n, l),
            '\u2566' => new(d, d, n, d), '\u2567' => new(l, l, d, n),
            '\u2568' => new(d, d, l, n), '\u2569' => new(d, d, d, n),
            '\u256A' => new(l, l, d, d), '\u256B' => new(d, d, l, l),
            '\u256C' => new(d, d, d, d),
            '\u256D' => new(n, l, n, l, Rounded: true),
            '\u256E' => new(l, n, n, l, Rounded: true),
            '\u256F' => new(l, n, l, n, Rounded: true),
            '\u2570' => new(n, l, l, n, Rounded: true),
            '\u2571' => new(n, n, n, n, DiagonalUp: true),
            '\u2572' => new(n, n, n, n, DiagonalDown: true),
            '\u2573' => new(n, n, n, n, DiagonalDown: true, DiagonalUp: true),
            '\u2574' => new(l, n, n, n), '\u2575' => new(n, n, l, n),
            '\u2576' => new(n, l, n, n), '\u2577' => new(n, n, n, l),
            '\u2578' => new(h, n, n, n), '\u2579' => new(n, n, h, n),
            '\u257A' => new(n, h, n, n), '\u257B' => new(n, n, n, h),
            '\u257C' => new(l, h, n, n), '\u257D' => new(n, n, l, h),
            '\u257E' => new(h, l, n, n), '\u257F' => new(n, n, h, l),
            _ => default
        };
        return ch is >= '\u2500' and <= '\u250B'
            or >= '\u250C' and <= '\u251B'
            or >= '\u251C' and <= '\u2542'
            or >= '\u2547' and <= '\u257F';
    }

    private bool TryDrawBlock(DrawingContext dc, Cell cell, int column, Brush brush)
    {
        if (cell.Text is not { Length: 1 } text || text[0] is < '\u2580' or > '\u259F')
            return false;

        var ch = text[0];
        var left = column * CellWidth;
        if (ch is >= '\u2591' and <= '\u2593')
        {
            brush = brush.CloneCurrentValue();
            brush.Opacity = (ch - '\u2590') / 4.0;
            brush.Freeze();
            dc.DrawRectangle(brush, null, new Rect(left, 0, CellWidth, CellHeight));
            return true;
        }

        if (ch >= '\u2596')
        {
            // Bits are upper-left, upper-right, lower-left, lower-right.
            var quadrants = ch switch
            {
                '\u2596' => 4, '\u2597' => 8, '\u2598' => 1, '\u2599' => 13,
                '\u259A' => 9, '\u259B' => 7, '\u259C' => 11, '\u259D' => 2,
                '\u259E' => 6, '\u259F' => 14,
                _ => 0
            };
            for (var q = 0; q < 4; q++)
                if ((quadrants & (1 << q)) != 0)
                    dc.DrawRectangle(brush, null, new Rect(left + (q % 2) * CellWidth / 2,
                        (q / 2) * CellHeight / 2, CellWidth / 2, CellHeight / 2));
            return true;
        }

        var rect = new Rect(left, 0, CellWidth, CellHeight);
        if (ch == '\u2580') rect.Height /= 2;
        else if (ch is >= '\u2581' and <= '\u2587')
        {
            rect.Height = CellHeight * (ch - '\u2580') / 8;
            rect.Y = CellHeight - rect.Height;
        }
        else if (ch is >= '\u2589' and <= '\u258F')
            rect.Width = CellWidth * ('\u2590' - ch) / 8;
        else if (ch == '\u2590') { rect.X += CellWidth / 2; rect.Width /= 2; }
        else if (ch == '\u2594') rect.Height /= 8;
        else if (ch == '\u2595') { rect.Width /= 8; rect.X += CellWidth - rect.Width; }
        dc.DrawRectangle(brush, null, rect);
        return true;
    }

    private FormattedText Text(string text, Brush brush, TerminalTextAttributes attributes)
    {
        var typeface = new Typeface(_typeface.FontFamily,
            attributes.HasFlag(TerminalTextAttributes.Italic) ? FontStyles.Italic : FontStyles.Normal,
            attributes.HasFlag(TerminalTextAttributes.Bold) ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);
        return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, _appearance.FontSize, brush, _pixelsPerDip);
    }

    private GlyphTypeface? GlyphTypefaceFor(TerminalTextAttributes attributes) =>
        (attributes.HasFlag(TerminalTextAttributes.Bold), attributes.HasFlag(TerminalTextAttributes.Italic)) switch
        {
            (true, true) => _boldItalicGlyphs,
            (true, false) => _boldGlyphs,
            (false, true) => _italicGlyphs,
            _ => _normalGlyphs
        };

    private Brush ForegroundBrush(Cell cell)
    {
        var reverse = cell.Attributes.HasFlag(TerminalTextAttributes.Reverse);
        var color = reverse ? cell.Background : cell.Foreground;
        var brush = reverse
            ? Resolve(color, _backgroundText)
            : Resolve(color, _foreground);
        if (!cell.Attributes.HasFlag(TerminalTextAttributes.Dim)) return brush;
        var key = (color.Value, color.IsRgb, reverse);
        if (_dimBrushes.TryGetValue(key, out var cached)) return cached;
        var dim = brush.CloneCurrentValue();
        dim.Opacity *= .5;
        dim.Freeze();
        _dimBrushes[key] = dim;
        return dim;
    }

    private void DrawDecorations(DrawingContext dc, Cell cell, int column, int count, Brush brush)
    {
        if (count <= 0) return;
        var attributes = cell.Attributes;
        var left = column * CellWidth;
        var right = (column + count) * CellWidth;
        var thickness = Math.Max(1, _appearance.FontSize / 14);
        if (attributes.HasFlag(TerminalTextAttributes.Underline))
        {
            var y = Math.Min(CellHeight - thickness / 2, _typeface.FontFamily.LineSpacing * _appearance.FontSize - thickness);
            dc.DrawLine(new Pen(brush, thickness), new Point(left, y), new Point(right, y));
        }
        if (attributes.HasFlag(TerminalTextAttributes.Strikethrough))
        {
            var y = CellHeight * .52;
            dc.DrawLine(new Pen(brush, thickness), new Point(left, y), new Point(right, y));
        }
    }

    private FormattedText Text(string text, Brush brush, bool bold) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            bold ? new Typeface(_typeface.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal) : _typeface,
            _appearance.FontSize, brush, _pixelsPerDip);

    private Brush Resolve(TerminalColor color, Brush fallback, bool background = false)
    {
        if (color.Value < 0) return fallback;
        var key = (color.IsRgb ? color.Value | 0x1000000 : color.Value) | (background ? 0x2000000 : 0);
        if (_brushes.TryGetValue(key, out var existing)) return existing;
        Brush brush;
        if (!color.IsRgb && color.Value < 16) brush = Brush(_appearance.Ansi[color.Value]);
        else
        {
            int r, g, b;
            if (color.IsRgb)
                (r, g, b) = ((color.Value >> 16) & 255, (color.Value >> 8) & 255, color.Value & 255);
            else if (color.Value >= 232) r = g = b = 8 + (color.Value - 232) * 10;
            else
            {
                var n = color.Value - 16;
                static int Level(int v) => v == 0 ? 0 : 55 + v * 40;
                (r, g, b) = (Level(n / 36), Level(n / 6 % 6), Level(n % 6));
            }
            brush = new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b));
            brush.Freeze();
        }
        if (background)
        {
            brush = brush.CloneCurrentValue();
            brush.Opacity = _appearance.BackgroundOpacity;
            brush.Freeze();
        }
        if (_brushes.Count >= 4096) _brushes.Clear();
        _brushes[key] = brush;
        return brush;
    }

    private static Brush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
