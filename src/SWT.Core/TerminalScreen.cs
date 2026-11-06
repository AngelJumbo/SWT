using System.Text;
using System.Globalization;

namespace SWT.Core;

public enum TerminalMouseMode { None, X10, Normal, ButtonEvent, AnyEvent }

public readonly record struct TerminalColor(int Value, bool IsRgb = false)
{
    public static TerminalColor Default => new(-1);
}

[Flags]
public enum TerminalTextAttributes
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Reverse = 16,
    Strikethrough = 32
}

public readonly record struct Cell(string? Text, TerminalColor Foreground, TerminalColor Background, bool Bold)
{
    public int Width { get; init; } = 1;
    public TerminalTextAttributes Attributes { get; init; }
    internal bool IsDefaultBlank => Width != 0 && (Text is null or " ") &&
        Background.Value < 0 &&
        (Attributes & (TerminalTextAttributes.Underline | TerminalTextAttributes.Reverse |
            TerminalTextAttributes.Strikethrough)) == 0;
}

// A row of cells. Version changes whenever visible content changes, so renderers can cache by (line, version).
public sealed class TerminalLine
{
    internal Cell[] Cells;

    internal TerminalLine(Cell[] cells) => Cells = cells;

    public int Length => Cells.Length;
    public int Version { get; private set; }
    public Cell this[int column] => Cells[column];
    /// <summary>This row continues into the next row through soft autowrap, not a hard line break.</summary>
    public bool IsWrapped { get; private set; }
    /// <summary>Valid physical cells before soft wrap; excludes padding before a wide character.</summary>
    public int WrappedLength { get; private set; }
    internal Cell? NarrowWideCell;

    internal void Touch() => Version++;
    internal void SetWrap(bool wrapped, int length = 0)
    {
        if (IsWrapped == wrapped && WrappedLength == length) return;
        IsWrapped = wrapped;
        WrappedLength = length;
        Touch();
    }

    public string Text(int columns)
        => Text(0, columns);

    public string Text(int start, int count)
    {
        if (count == 0) return "";
        var builder = new StringBuilder(count);
        var end = start + count;
        if (start > 0 && start < Cells.Length && Cells[start].Width == 0) start--;
        // A selection touching either half includes the complete wide character.
        for (var x = start; x < end; x++)
            if (x >= Cells.Length) builder.Append(' ');
            else if (Cells[x].Width != 0) builder.Append(Cells[x].Text ?? " ");
        return builder.ToString();
    }
}

// Prototype parser: bounded state, fixed cell grid, bounded scrollback, and no shell-controlled side effects.
public sealed class TerminalScreen
{
    public const int MaxScrollbackLines = 100_000;
    private enum ParserState { Text, Escape, Charset, Csi, CsiDiscard, Osc, OscEscape, Ignore, IgnoreEscape }
    private sealed class ScreenBuffer(TerminalLine[] lines)
    {
        public TerminalLine[] Lines = lines;
        public int CursorX, CursorY, SavedX, SavedY;
        public int ScrollTop;
        public int ScrollBottom = lines.Length - 1;
        public bool WrapPending;
        public bool SavedWrapPending;
        public bool OriginMode, SavedOriginMode;
    }

    private static readonly string?[] CharStrings = new string?[char.MaxValue + 1];
    private ParserState _state;
    private readonly StringBuilder _sequence = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly int[] _args = new int[64];
    private readonly ScreenBuffer _mainBuffer;
    private readonly ScreenBuffer _alternateBuffer;
    private ScreenBuffer _buffer;
    private int _columns;
    private TerminalLine[] _history = [];
    private int _historyStart;
    private int _historyCount;
    private int _scrollbackLimit;
    private TerminalColor _foreground = TerminalColor.Default;
    private TerminalColor _background = TerminalColor.Default;
    private TerminalTextAttributes _attributes;
    private char? _pendingHighSurrogate;

    public TerminalScreen(int columns, int rows, int scrollbackLines = 0)
    {
        ValidateSize(columns, rows);
        _columns = columns;
        _mainBuffer = new ScreenBuffer(NewLines(columns, rows));
        _alternateBuffer = new ScreenBuffer(NewLines(columns, rows));
        _buffer = _mainBuffer;
        ScrollbackLimit = scrollbackLines;
    }

    public int Columns => _columns;
    public int Rows => _buffer.Lines.Length;
    public int CursorX => _buffer.CursorX;
    public int CursorY => _buffer.CursorY;
    public bool CursorVisible { get; private set; } = true;
    public bool IsAlternateScreen => ReferenceEquals(_buffer, _alternateBuffer);
    public bool ApplicationCursorKeys { get; private set; }
    public bool ApplicationKeypad { get; private set; }
    public bool BracketedPaste { get; private set; }
    public TerminalMouseMode MouseMode { get; private set; }
    public bool SgrMouse { get; private set; }
    public bool OriginMode => _buffer.OriginMode;
    public Cell this[int row, int column] => _buffer.Lines[row].Cells[column];
    public int ScrollbackCount => IsAlternateScreen ? 0 : _historyCount;

    /// <summary>Total lines ever pushed into scrollback; viewers use deltas to stay anchored.</summary>
    public long ScrolledLines { get; private set; }
    /// <summary>Changes on grid resize; physical row-based search, selection and viewport anchors become stale.</summary>
    public long ResizeVersion { get; private set; }

    public event Action<string>? Reply;

    public string EncodePaste(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return "";
        var normalized = text.Replace("\r\n", "\n").Replace('\n', '\r');
        return BracketedPaste ? $"\x1b[200~{normalized}\x1b[201~" : normalized;
    }

    public int ScrollbackLimit
    {
        get => _scrollbackLimit;
        set
        {
            if (value is < 0 or > MaxScrollbackLines)
                throw new ArgumentOutOfRangeException(nameof(value), $"Scrollback must be 0..{MaxScrollbackLines}.");
            var keep = Math.Min(_historyCount, value);
            var next = new TerminalLine[Math.Min(value, Math.Max(keep, 1024))];
            for (var i = 0; i < keep; i++) next[i] = History(_historyCount - keep + i);
            _history = next;
            _historyStart = 0;
            _historyCount = keep;
            _scrollbackLimit = value;
        }
    }

    public TerminalLine GetLine(int row) => _buffer.Lines[row];

    /// <summary>Returns a visible row when the viewport is scrolled back by <paramref name="scrollOffset"/> lines.</summary>
    public TerminalLine GetViewportLine(int row, int scrollOffset)
    {
        var historyCount = ScrollbackCount;
        var index = historyCount - Math.Clamp(scrollOffset, 0, historyCount) + row;
        return index < historyCount ? History(index) : _buffer.Lines[index - historyCount];
    }

    /// <summary>Scrollback line by age; 0 is the oldest retained line.</summary>
    public TerminalLine GetScrollbackLine(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _historyCount);
        return History(index);
    }

    public void ClearScrollback()
    {
        Array.Clear(_history);
        _historyStart = _historyCount = 0;
    }

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        Span<char> chars = stackalloc char[4096];
        do
        {
            _decoder.Convert(bytes, chars, false, out var used, out var written, out _);
            foreach (var c in chars[..written]) Consume(c);
            bytes = bytes[used..];
        } while (!bytes.IsEmpty);
    }

    public void Resize(int columns, int rows)
    {
        ValidateSize(columns, rows);
        if (columns == Columns && rows == Rows) return;
        if (columns != Columns) ReflowMainBuffer(columns, rows);
        else ResizeBuffer(_mainBuffer, columns, rows, true);
        ResizeBuffer(_alternateBuffer, columns, rows, false);
        _columns = columns;
        ResizeVersion++;
    }

    public string Line(int row) => _buffer.Lines[row].Text(Columns);

    private static void ValidateSize(int columns, int rows)
    {
        if (columns is < 1 or > 1000 || rows is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(columns), "Grid must be 1..1000 columns by 1..500 rows.");
    }

    private TerminalLine History(int index) => _history[(_historyStart + index) % _history.Length];

    private TerminalLine NewLine(int columns)
    {
        var cells = new Cell[columns];
        Array.Fill(cells, Blank);
        return new TerminalLine(cells);
    }

    private TerminalLine[] NewLines(int columns, int rows)
    {
        var lines = new TerminalLine[rows];
        for (var y = 0; y < rows; y++) lines[y] = NewLine(columns);
        return lines;
    }

    private void ResizeBuffer(ScreenBuffer buffer, int columns, int rows, bool keepHistory)
    {
        var lines = buffer.Lines;
        if (rows != lines.Length)
        {
            buffer.ScrollTop = 0;
            buffer.ScrollBottom = rows - 1;
        }
        var shift = Math.Max(0, buffer.CursorY - rows + 1);
        if (keepHistory)
            for (var y = 0; y < shift; y++) PushHistory(lines[y], out _);
        var resized = new TerminalLine[rows];
        for (var y = 0; y < rows; y++)
        {
            var source = y + shift;
            resized[y] = source < lines.Length ? lines[source] : NewLine(columns);
            if (resized[y].Length == columns) continue;
            var cells = resized[y].Cells;
            var old = cells.Length;
            Array.Resize(ref cells, columns);
            for (var x = old; x < columns; x++) cells[x] = Blank;
            resized[y].Cells = cells;
            resized[y].SetWrap(false);
            resized[y].NarrowWideCell = null;
            RepairWideCells(resized[y], Blank);
            resized[y].Touch();
        }
        buffer.Lines = resized;
        buffer.CursorX = Math.Min(buffer.CursorX, columns - 1);
        buffer.CursorY = Math.Min(buffer.CursorY - shift, rows - 1);
        buffer.SavedX = Math.Min(buffer.SavedX, columns - 1);
        buffer.SavedY = Math.Clamp(buffer.SavedY - shift, 0, rows - 1);
        buffer.WrapPending = false;
    }

    private void ReflowMainBuffer(int columns, int rows)
    {
        var buffer = _mainBuffer;
        var oldRows = buffer.Lines.Length;
        var source = new List<TerminalLine>(_historyCount + buffer.Lines.Length);
        for (var i = 0; i < _historyCount; i++) source.Add(History(i));
        source.AddRange(buffer.Lines);
        var cursorRow = _historyCount + buffer.CursorY;
        var savedRow = _historyCount + buffer.SavedY;
        // Unused rows below the cursor are grid capacity, not hard breaks in the transcript.
        while (source.Count > Math.Max(cursorRow, savedRow) + 1 &&
               !source[^1].IsWrapped && ContentLength(source[^1]) == 0)
            source.RemoveAt(source.Count - 1);

        var result = new List<TerminalLine>();
        (int Row, int X, bool Pending) cursor = default, saved = default;
        for (var first = 0; first < source.Count;)
        {
            var cells = new List<Cell>();
            int? cursorOffset = null, savedOffset = null;
            var last = first;
            var logicalLength = 0;
            do
            {
                var line = source[last];
                var length = line.IsWrapped ? line.WrappedLength : ContentLength(line);
                var start = logicalLength;
                if (last == cursorRow)
                {
                    var x = Math.Min(buffer.CursorX + (buffer.WrapPending ? 1 : 0),
                        line.IsWrapped ? line.WrappedLength : line.Length);
                    cursorOffset = start + (line.NarrowWideCell.HasValue && x > 0 ? 2 : x);
                    length = Math.Max(length, x);
                }
                if (last == savedRow)
                {
                    var x = Math.Min(buffer.SavedX + (buffer.SavedWrapPending ? 1 : 0),
                        line.IsWrapped ? line.WrappedLength : line.Length);
                    savedOffset = start + (line.NarrowWideCell.HasValue && x > 0 ? 2 : x);
                    length = Math.Max(length, x);
                }
                for (var x = 0; x < length; x++)
                {
                    var cell = x < line.Length ? line.Cells[x] : DefaultBlank;
                    if (cell.Width != 0)
                    {
                        cell = x == 0 && line.NarrowWideCell is Cell wide ? wide : cell;
                        cells.Add(cell);
                        logicalLength += cell.Width;
                    }
                }
                last++;
            } while (last < source.Count && source[last - 1].IsWrapped);

            var output = NewLine(columns);
            Array.Fill(output.Cells, DefaultBlank);
            var outputRow = result.Count;
            var column = 0;
            var offset = 0;
            var positions = new List<(int Offset, int Row, int X)>();
            foreach (var cell in cells)
            {
                var width = Math.Min(cell.Width, columns);
                if (column + width > columns)
                {
                    output.SetWrap(true, column);
                    result.Add(output);
                    output = NewLine(columns);
                    Array.Fill(output.Cells, DefaultBlank);
                    outputRow++;
                    column = 0;
                }
                positions.Add((offset, outputRow, column));
                if (cell.Width > columns)
                {
                    output.Cells[column] = cell with { Text = "\uFFFD", Width = 1 };
                    output.NarrowWideCell = cell;
                }
                else
                {
                    output.Cells[column] = cell;
                    if (width == 2) output.Cells[column + 1] = cell with { Text = "", Width = 0 };
                }
                column += width;
                offset += cell.Width;
            }
            positions.Add((offset, outputRow, column));
            result.Add(output);
            (int Row, int X, bool Pending) Map(int target, bool pending)
            {
                for (var i = 0; i < positions.Count; i++)
                {
                    var p = positions[i];
                    if (p.Offset < target && i + 1 < positions.Count) continue;
                    if (p.Offset > target && i > 0)
                    {
                        var previous = positions[i - 1];
                        return (previous.Row, Math.Min(columns - 1, previous.X + target - previous.Offset), false);
                    }
                    if (pending && p.X == 0 && i > 0 && result[p.Row - 1].WrappedLength == columns)
                        return (p.Row - 1, columns - 1, true);
                    return (p.Row, Math.Min(p.X, columns - 1), p.X == columns);
                }
                return (outputRow, Math.Min(column, columns - 1), column == columns);
            }
            if (cursorOffset is int current) cursor = Map(current, buffer.WrapPending);
            if (savedOffset is int stored) saved = Map(stored, buffer.SavedWrapPending);
            first = last;
        }

        // Preserve the newest transcript; cursor positions evicted from the grid are clamped.
        var screenStart = Math.Max(0, result.Count - rows);
        var historyStart = Math.Max(0, screenStart - _scrollbackLimit);
        var oldHistoryCount = _historyCount;
        ClearScrollback();
        for (var i = historyStart; i < screenStart; i++) PushHistory(result[i], out _);
        // Reflow changes physical history rows; viewers invalidate row-based anchors on resize.
        ScrolledLines -= _historyCount;
        ScrolledLines += Math.Max(0, screenStart - oldHistoryCount);
        buffer.Lines = new TerminalLine[rows];
        for (var y = 0; y < rows; y++)
            buffer.Lines[y] = screenStart + y < result.Count ? result[screenStart + y] : NewLine(columns);
        buffer.CursorX = cursor.X;
        buffer.CursorY = Math.Clamp(cursor.Row - screenStart, 0, rows - 1);
        buffer.WrapPending = cursor.Pending;
        buffer.SavedX = saved.X;
        buffer.SavedY = Math.Clamp(saved.Row - screenStart, 0, rows - 1);
        buffer.SavedWrapPending = saved.Pending;
        if (rows != oldRows)
        {
            buffer.ScrollTop = 0;
            buffer.ScrollBottom = rows - 1;
        }
    }

    private static int ContentLength(TerminalLine line)
    {
        var length = line.Length;
        // Erased cells can retain foreground/bold attributes but remain implicit trailing blanks.
        // Treating those as text would spill an empty row on shrink and lift a bottom-row prompt.
        while (length > 0 && line.Cells[length - 1].IsDefaultBlank) length--;
        return length;
    }

    private static readonly Cell DefaultBlank = new(" ", TerminalColor.Default, TerminalColor.Default, false);

    // Moves a line into scrollback. Returns the evicted line object (if any) for recycling, and a cell
    // array no longer referenced by history (if any) so steady-state scrolling does not allocate.
    private TerminalLine? PushHistory(TerminalLine line, out Cell[]? freed)
    {
        freed = null;
        if (_scrollbackLimit == 0) return null;
        ScrolledLines++;
        TerminalLine? evicted = null;
        int slot;
        if (_historyCount < _history.Length) slot = (_historyStart + _historyCount++) % _history.Length;
        else if (_history.Length < _scrollbackLimit)
        {
            var grown = new TerminalLine[Math.Min(_scrollbackLimit, _history.Length * 2)];
            for (var i = 0; i < _historyCount; i++) grown[i] = History(i);
            _history = grown;
            _historyStart = 0;
            slot = _historyCount++;
        }
        else
        {
            evicted = _history[_historyStart];
            slot = _historyStart;
            _historyStart = (_historyStart + 1) % _history.Length;
        }
        _history[slot] = line;
        // Trailing default blanks are implicit; trimming keeps long histories small. Rendering is unchanged.
        var full = line.Cells;
        var spare = evicted?.Cells;
        var length = full.Length;
        while (length > (line.IsWrapped ? line.WrappedLength : 0) && full[length - 1] == DefaultBlank) length--;
        if (length == full.Length) { freed = spare; return evicted; }
        if (spare is not null && spare.Length >= length && spare.Length <= full.Length)
        {
            Array.Copy(full, spare, length);
            Array.Fill(spare, DefaultBlank, length, spare.Length - length);
            line.Cells = spare;
        }
        else line.Cells = full[..length];
        freed = full;
        return evicted;
    }

    private void Consume(char c)
    {
        if (_state == ParserState.Text && c is >= ' ' and < '\x7f' && _pendingHighSurrogate is null)
        {
            Print(CharStrings[c] ??= c.ToString());
            return;
        }
        if (c == '\x18' || c == '\x1a') { _state = ParserState.Text; return; }
        switch (_state)
        {
            case ParserState.Charset:
                _state = ParserState.Text;
                return;
            case ParserState.CsiDiscard:
                if (c is >= '@' and <= '~') _state = ParserState.Text;
                else if (c == '\x1b') _state = ParserState.Escape;
                return;
            case ParserState.Osc:
                if (c == '\a') _state = ParserState.Text;
                else if (c == '\x1b') _state = ParserState.OscEscape;
                return;
            case ParserState.OscEscape:
                _state = c == '\\' ? ParserState.Text : ParserState.Osc;
                return;
            case ParserState.Ignore:
                if (c == '\x1b') _state = ParserState.IgnoreEscape;
                return;
            case ParserState.IgnoreEscape:
                _state = c == '\\' ? ParserState.Text : ParserState.Ignore;
                return;
        }
        if (c == '\x1b') { _state = ParserState.Escape; return; }
        if (_state == ParserState.Escape)
        {
            _state = ParserState.Text;
            switch (c)
            {
                case '[': _sequence.Clear(); _state = ParserState.Csi; break;
                case ']': _state = ParserState.Osc; break;
                case 'P': case '^': case '_': _state = ParserState.Ignore; break;
                case '(': case ')': _state = ParserState.Charset; break;
                case '7': SaveCursor(); break;
                case '8': RestoreCursor(); break;
                case '=': ApplicationKeypad = true; break;
                case '>': ApplicationKeypad = false; break;
                case 'D': LineFeed(); break;
                case 'M': ReverseIndex(); break;
                case 'E': _buffer.CursorX = 0; LineFeed(); break;
            }
            return;
        }
        if (_state == ParserState.Csi)
        {
            if (c is >= '@' and <= '~')
            {
                Execute(c);
                _state = ParserState.Text;
            }
            else if (_sequence.Length < 256) _sequence.Append(c);
            else _state = ParserState.CsiDiscard;
            return;
        }
        switch (c)
        {
            case '\r': _buffer.CursorX = 0; _buffer.WrapPending = false; return;
            case '\n': LineFeed(); return;
            case '\b': Move(CursorX - 1, CursorY); return;
            case '\t': Move(Math.Min((CursorX / 8 + 1) * 8, Columns - 1), CursorY); return;
        }
        if (char.IsControl(c)) return;
        if (char.IsHighSurrogate(c)) { _pendingHighSurrogate = c; return; }
        var text = _pendingHighSurrogate is char high && char.IsLowSurrogate(c)
            ? new string([high, c])
            : CharStrings[c] ??= c.ToString();
        _pendingHighSurrogate = null;
        Print(text);
    }

    private void Print(string text)
    {
        var rune = Rune.GetRuneAt(text, 0);
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark)
        {
            var x = _buffer.WrapPending ? CursorX : CursorX - 1;
            if (x < 0) return;
            var previous = _buffer.Lines[CursorY];
            if (previous.Cells[x].Width == 0) x--;
            if (x >= 0 && (previous.Cells[x].Text?.Length ?? 0) + text.Length <= 64)
            {
                previous.Cells[x] = previous.Cells[x] with { Text = previous.Cells[x].Text + text };
                previous.Touch();
            }
            return;
        }
        var width = CharacterWidth(rune.Value);
        if (width > Columns) { text = "\uFFFD"; width = 1; }
        if (_buffer.WrapPending) SoftWrap(Columns);
        if (width == 2 && CursorX == Columns - 1) SoftWrap(Columns - 1);
        var line = _buffer.Lines[CursorY];
        line.NarrowWideCell = null;
        ClearWideCharacter(line, CursorX);
        if (width == 2) ClearWideCharacter(line, CursorX + 1);
        line.Cells[CursorX] = new Cell(text, _foreground, _background,
            _attributes.HasFlag(TerminalTextAttributes.Bold)) { Width = width, Attributes = _attributes };
        if (width == 2)
            line.Cells[CursorX + 1] = new Cell("", _foreground, _background,
                _attributes.HasFlag(TerminalTextAttributes.Bold)) { Width = 0, Attributes = _attributes };
        if (line.IsWrapped) line.SetWrap(true, Math.Max(line.WrappedLength, CursorX + width));
        line.Touch();
        if (CursorX + width >= Columns) { _buffer.CursorX = Columns - 1; _buffer.WrapPending = true; }
        else _buffer.CursorX += width;
    }

    private static int CharacterWidth(int value) => value is
        >= 0x1100 and <= 0x115F or 0x2329 or 0x232A or
        >= 0x2E80 and <= 0x303E or >= 0x3040 and <= 0xA4CF or
        >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF or
        >= 0xFE10 and <= 0xFE19 or >= 0xFE30 and <= 0xFE6F or
        >= 0xFF01 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or
        >= 0x20000 and <= 0x3FFFD ? 2 : 1;

    private void ClearWideCharacter(TerminalLine line, int x)
    {
        if (line.Cells[x].Width == 0 && x > 0) line.Cells[x - 1] = Blank;
        else if (line.Cells[x].Width == 2 && x + 1 < line.Length) line.Cells[x + 1] = Blank;
        line.Cells[x] = Blank;
    }

    private static void RepairWideCells(TerminalLine line, Cell blank)
    {
        for (var x = 0; x < line.Length; x++)
        {
            if (line.Cells[x].Width == 2)
            {
                if (x + 1 < line.Length && line.Cells[x + 1].Width == 0) x++;
                else line.Cells[x] = blank;
            }
            else if (line.Cells[x].Width == 0) line.Cells[x] = blank;
        }
    }

    private void Move(int x, int y)
    {
        _buffer.CursorX = Math.Clamp(x, 0, Columns - 1);
        _buffer.CursorY = Math.Clamp(y, OriginMode ? _buffer.ScrollTop : 0,
            OriginMode ? _buffer.ScrollBottom : Rows - 1);
        _buffer.WrapPending = false;
    }

    private void Position(int x, int y) => Move(x, y + (OriginMode ? _buffer.ScrollTop : 0));

    private void SaveCursor()
    {
        _buffer.SavedX = CursorX;
        _buffer.SavedY = CursorY;
        _buffer.SavedOriginMode = OriginMode;
        _buffer.SavedWrapPending = _buffer.WrapPending;
    }

    private void RestoreCursor()
    {
        _buffer.OriginMode = _buffer.SavedOriginMode;
        Move(_buffer.SavedX, _buffer.SavedY);
        _buffer.WrapPending = _buffer.SavedWrapPending;
    }

    private void SoftWrap(int length)
    {
        var line = _buffer.Lines[CursorY];
        if (length < Columns)
        {
            ClearWideCharacter(line, length);
            line.Cells[length] = Blank;
            line.Touch();
        }
        line.SetWrap(true, length);
        _buffer.CursorX = 0;
        LineFeed(softWrap: true);
    }

    private void LineFeed(bool softWrap = false)
    {
        if (!softWrap) _buffer.Lines[CursorY].SetWrap(false);
        _buffer.WrapPending = false;
        if (CursorY == _buffer.ScrollBottom)
            ScrollUp(_buffer.ScrollTop, _buffer.ScrollBottom, 1, keepHistory: true, softWrap);
        else if (CursorY < Rows - 1) _buffer.CursorY++;
    }

    private void ReverseIndex()
    {
        _buffer.WrapPending = false;
        if (CursorY == _buffer.ScrollTop)
            ScrollDown(_buffer.ScrollTop, _buffer.ScrollBottom, 1);
        else if (CursorY > 0) _buffer.CursorY--;
    }

    private void ScrollUp(int first, int last, int count, bool keepHistory, bool softWrap = false)
    {
        count = Math.Min(count, last - first + 1);
        if (first > 0) _buffer.Lines[first - 1].SetWrap(false);
        for (var i = 0; i < count; i++) ScrollUpOne(first, last, keepHistory);
        if (!softWrap && last - count >= first) _buffer.Lines[last - count].SetWrap(false);
    }

    private void ScrollUpOne(int first, int last, bool keepHistory)
    {
        var lines = _buffer.Lines;
        var top = lines[first];
        var save = keepHistory && !IsAlternateScreen && first == 0 && last == Rows - 1 && _scrollbackLimit > 0;
        Cell[]? freed = null;
        var recycled = save ? PushHistory(top, out freed) : null;
        Array.Copy(lines, first + 1, lines, first, last - first);
        TerminalLine bottom;
        if (!save) bottom = top;
        else
        {
            var cells = freed?.Length == Columns ? freed : new Cell[Columns];
            bottom = recycled ?? new TerminalLine(cells);
            bottom.Cells = cells;
        }
        Array.Fill(bottom.Cells, Blank);
        bottom.SetWrap(false);
        bottom.NarrowWideCell = null;
        bottom.Touch();
        lines[last] = bottom;
    }

    private void ScrollDown(int first, int last, int count)
    {
        var lines = _buffer.Lines;
        count = Math.Min(count, last - first + 1);
        if (first > 0) lines[first - 1].SetWrap(false);
        for (var i = 0; i < count; i++)
        {
            var recycled = lines[last];
            Array.Copy(lines, first, lines, first + 1, last - first);
            Array.Fill(recycled.Cells, Blank);
            recycled.SetWrap(false);
            recycled.NarrowWideCell = null;
            recycled.Touch();
            lines[first] = recycled;
        }
        lines[last].SetWrap(false);
    }

    private Cell Blank => new(" ", _foreground, _background,
        _attributes.HasFlag(TerminalTextAttributes.Bold)) { Attributes = _attributes };

    private void EraseRows(int startRow, int endRow)
    {
        for (var y = startRow; y < endRow; y++) EraseInRow(y, 0, Columns);
    }

    private void EraseInRow(int row, int start, int end)
    {
        if (start >= end) return;
        var line = _buffer.Lines[row];
        line.SetWrap(false);
        line.NarrowWideCell = null;
        if (line.Cells[start].Width == 0) ClearWideCharacter(line, start);
        if (line.Cells[end - 1].Width == 2) ClearWideCharacter(line, end - 1);
        Array.Fill(line.Cells, Blank, start, end - start);
        line.Touch();
    }

    private void ShiftCharacters(int count, bool insert)
    {
        var line = _buffer.Lines[CursorY];
        line.SetWrap(false);
        line.NarrowWideCell = null;
        count = Math.Min(count, Columns - CursorX);
        var remaining = Columns - CursorX - count;
        if (insert)
        {
            Array.Copy(line.Cells, CursorX, line.Cells, CursorX + count, remaining);
            Array.Fill(line.Cells, Blank, CursorX, count);
        }
        else
        {
            Array.Copy(line.Cells, CursorX + count, line.Cells, CursorX, remaining);
            Array.Fill(line.Cells, Blank, Columns - count, count);
        }
        RepairWideCells(line, Blank);
        line.Touch();
        _buffer.WrapPending = false;
    }

    private void Execute(char command)
    {
        if (_sequence.Length > 0 && _sequence[0] == '?')
        {
            if (command is 'h' or 'l') SetPrivateModes(command == 'h');
            return;
        }
        var count = 1;
        _args[0] = 0;
        for (var i = 0; i < _sequence.Length; i++)
        {
            var ch = _sequence[i];
            if (ch == ';')
            {
                if (count == _args.Length) return;
                _args[count++] = 0;
            }
            else if (ch is >= '0' and <= '9') _args[count - 1] = Math.Min(_args[count - 1] * 10 + (ch - '0'), 100_000);
            else return;
        }
        ReadOnlySpan<int> args = _args.AsSpan(0, count);
        int Arg(ReadOnlySpan<int> values, int index, int fallback = 1) =>
            index < values.Length && values[index] != 0 ? Math.Min(values[index], 10000) : fallback;
        switch (command)
        {
            case 'A': Move(CursorX, CursorY - Arg(args, 0)); break;
            case 'B': Move(CursorX, CursorY + Arg(args, 0)); break;
            case 'C': Move(CursorX + Arg(args, 0), CursorY); break;
            case 'D': Move(CursorX - Arg(args, 0), CursorY); break;
            case 'E': Move(0, CursorY + Arg(args, 0)); break;
            case 'F': Move(0, CursorY - Arg(args, 0)); break;
            case 'G': case '`': Move(Arg(args, 0) - 1, CursorY); break;
            case 'd': Position(CursorX, Arg(args, 0) - 1); break;
            case 'H': case 'f': Position(Arg(args, 1) - 1, Arg(args, 0) - 1); break;
            case 'J':
                if (args[0] == 0) { EraseInRow(CursorY, CursorX, Columns); EraseRows(CursorY + 1, Rows); }
                else if (args[0] == 1) { EraseRows(0, CursorY); EraseInRow(CursorY, 0, CursorX + 1); }
                else if (args[0] == 2) EraseRows(0, Rows);
                else if (args[0] == 3) ClearScrollback();
                break;
            case 'K':
                if (args[0] == 0) EraseInRow(CursorY, CursorX, Columns);
                else if (args[0] == 1) EraseInRow(CursorY, 0, CursorX + 1);
                else if (args[0] == 2) EraseInRow(CursorY, 0, Columns);
                break;
            case 'X': EraseInRow(CursorY, CursorX, Math.Min(CursorX + Arg(args, 0), Columns)); break;
            case '@': ShiftCharacters(Arg(args, 0), insert: true); break;
            case 'P': ShiftCharacters(Arg(args, 0), insert: false); break;
            case 'r':
                var top = Arg(args, 0) - 1;
                var bottom = Arg(args, 1, Rows) - 1;
                if (args.Length <= 2 && top >= 0 && bottom < Rows && top < bottom)
                {
                    _buffer.ScrollTop = top;
                    _buffer.ScrollBottom = bottom;
                    Position(0, 0);
                }
                break;
            case 'L':
                if (CursorY >= _buffer.ScrollTop && CursorY <= _buffer.ScrollBottom)
                {
                    ScrollDown(CursorY, _buffer.ScrollBottom, Arg(args, 0));
                    _buffer.WrapPending = false;
                }
                break;
            case 'M':
                if (CursorY >= _buffer.ScrollTop && CursorY <= _buffer.ScrollBottom)
                {
                    ScrollUp(CursorY, _buffer.ScrollBottom, Arg(args, 0), keepHistory: false);
                    _buffer.WrapPending = false;
                }
                break;
            case 'S': ScrollUp(_buffer.ScrollTop, _buffer.ScrollBottom, Arg(args, 0), keepHistory: true); break;
            case 'T': ScrollDown(_buffer.ScrollTop, _buffer.ScrollBottom, Arg(args, 0)); break;
            case 'm': Attributes(args); break;
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 'n' when args[0] == 6:
                Reply?.Invoke($"\x1b[{CursorY + 1 - (OriginMode ? _buffer.ScrollTop : 0)};{CursorX + 1}R");
                break;
            case 'n' when args[0] == 5: Reply?.Invoke("\x1b[0n"); break;
        }
    }

    private void SetPrivateModes(bool enabled)
    {
        var count = 1;
        _args[0] = 0;
        for (var i = 1; i < _sequence.Length; i++)
        {
            var ch = _sequence[i];
            if (ch == ';')
            {
                if (count == _args.Length) return;
                _args[count++] = 0;
            }
            else if (ch is >= '0' and <= '9')
                _args[count - 1] = Math.Min(_args[count - 1] * 10 + (ch - '0'), 100_000);
            else return;
        }

        for (var i = 0; i < count; i++)
        {
            switch (_args[i])
            {
                case 1: ApplicationCursorKeys = enabled; break;
                case 6:
                    _buffer.OriginMode = enabled;
                    Position(0, 0);
                    break;
                case 25: CursorVisible = enabled; break;
                case 9: SetMouseMode(TerminalMouseMode.X10, enabled); break;
                case 1000: SetMouseMode(TerminalMouseMode.Normal, enabled); break;
                case 1002: SetMouseMode(TerminalMouseMode.ButtonEvent, enabled); break;
                case 1003: SetMouseMode(TerminalMouseMode.AnyEvent, enabled); break;
                case 1006: SgrMouse = enabled; break;
                case 2004: BracketedPaste = enabled; break;
                case 47:
                    if (enabled) EnterAlternateScreen(clear: false);
                    else ExitAlternateScreen(restore1049Cursor: false);
                    break;
                case 1047:
                    if (enabled) EnterAlternateScreen(clear: false);
                    else ExitAlternateScreen(restore1049Cursor: false, clearAlternate: true);
                    break;
                case 1048:
                    if (enabled) SaveCursor();
                    else RestoreCursor();
                    break;
                case 1049:
                    if (enabled) EnterAlternateScreen(clear: true);
                    else ExitAlternateScreen(restore1049Cursor: true);
                    break;
            }
        }
    }

    private void SetMouseMode(TerminalMouseMode mode, bool enabled)
    {
        if (enabled) MouseMode = mode;
        else if (MouseMode == mode) MouseMode = TerminalMouseMode.None;
    }

    private void EnterAlternateScreen(bool clear)
    {
        if (IsAlternateScreen) return;
        _buffer = _alternateBuffer;
        if (!clear) return;
        ClearBuffer(_buffer);
    }

    private void ExitAlternateScreen(bool restore1049Cursor, bool clearAlternate = false)
    {
        if (!IsAlternateScreen) return;
        _buffer = _mainBuffer;
        if (restore1049Cursor) Move(_mainBuffer.CursorX, _mainBuffer.CursorY);
        if (clearAlternate) ClearBuffer(_alternateBuffer);
    }

    private void ClearBuffer(ScreenBuffer buffer)
    {
        foreach (var line in buffer.Lines)
        {
            Array.Fill(line.Cells, Blank);
            line.SetWrap(false);
            line.NarrowWideCell = null;
            line.Touch();
        }
        buffer.CursorX = buffer.SavedX = 0;
        buffer.CursorY = buffer.SavedY = buffer.OriginMode ? buffer.ScrollTop : 0;
        buffer.SavedOriginMode = buffer.OriginMode;
        buffer.WrapPending = false;
        buffer.SavedWrapPending = false;
    }

    private void Attributes(ReadOnlySpan<int> args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var n = args[i];
            switch (n)
            {
                case 0: _foreground = _background = TerminalColor.Default; _attributes = TerminalTextAttributes.None; break;
                case 1: _attributes |= TerminalTextAttributes.Bold; break;
                case 2: _attributes |= TerminalTextAttributes.Dim; break;
                case 3: _attributes |= TerminalTextAttributes.Italic; break;
                case 4: _attributes |= TerminalTextAttributes.Underline; break;
                case 7: _attributes |= TerminalTextAttributes.Reverse; break;
                case 9: _attributes |= TerminalTextAttributes.Strikethrough; break;
                case 22: _attributes &= ~(TerminalTextAttributes.Bold | TerminalTextAttributes.Dim); break;
                case 23: _attributes &= ~TerminalTextAttributes.Italic; break;
                case 24: _attributes &= ~TerminalTextAttributes.Underline; break;
                case 27: _attributes &= ~TerminalTextAttributes.Reverse; break;
                case 29: _attributes &= ~TerminalTextAttributes.Strikethrough; break;
                case 39: _foreground = TerminalColor.Default; break;
                case 49: _background = TerminalColor.Default; break;
                case >= 30 and <= 37: _foreground = new(n - 30); break;
                case >= 40 and <= 47: _background = new(n - 40); break;
                case >= 90 and <= 97: _foreground = new(n - 90 + 8); break;
                case >= 100 and <= 107: _background = new(n - 100 + 8); break;
                case 38: case 48:
                    TerminalColor color;
                    if (i + 2 < args.Length && args[i + 1] == 5 && args[i + 2] is >= 0 and <= 255)
                    { color = new(args[i + 2]); i += 2; }
                    else if (i + 4 < args.Length && args[i + 1] == 2 &&
                             args[i + 2] <= 255 && args[i + 3] <= 255 && args[i + 4] <= 255)
                    { color = new((args[i + 2] << 16) | (args[i + 3] << 8) | args[i + 4], true); i += 4; }
                    else break;
                    if (n == 38) _foreground = color; else _background = color;
                    break;
            }
        }
    }
}
