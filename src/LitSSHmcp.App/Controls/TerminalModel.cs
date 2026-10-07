using System.Text;

namespace LitSSHmcp.App.Controls;

/// <summary>终端单元格属性。Fg/Bg 为 null 表示默认色；否则为 0xRRGGBB。</summary>
public struct TerminalAttributes
{
    public uint? Fg;
    public uint? Bg;
    public bool Bold;
    public bool Italic;
    public bool Underline;
    public bool Reverse;

    public static TerminalAttributes Default => default;

    public readonly TerminalAttributes WithDefaultFg() => this;
}

/// <summary>终端一个字符单元。</summary>
public struct TerminalCell
{
    public char Ch;
    public TerminalAttributes Attr;
}

/// <summary>
/// 自包含的 VT100/VT220/ANSI 终端缓冲与解析器（无外部依赖）。
/// 覆盖常用序列：光标定位/移动、擦除(ED/EL/ECH)、SGR 颜色与属性、滚动区域(DECSTBM)、
/// 插入/删除行与字符(IL/DL/ICH/DCH)、备用屏(?1049)、自动换行、保存/恢复光标、滚动回看。
/// </summary>
public sealed class TerminalModel
{
    private int _cols;
    private int _rows;

    private TerminalCell[] _cells = Array.Empty<TerminalCell>();
    private TerminalCell[] _altCells = Array.Empty<TerminalCell>();

    private int _cx;
    private int _cy;
    private int _savedCx;
    private int _savedCy;
    private TerminalAttributes _attr = TerminalAttributes.Default;
    private TerminalAttributes _savedAttr = TerminalAttributes.Default;

    private bool _cursorVisible = true;
    private bool _autoWrap = true;
    private bool _wrapPending;
    private bool _altScreen;
    private int _scrollTop;
    private int _scrollBottom;

    private readonly List<TerminalCell[]> _scrollback = new();

    /// <summary>是否处于备用屏（vim/top/less 等全屏程序）。</summary>
    public bool IsAltScreen => _altScreen;

    /// <summary>回看行数上限。</summary>
    public int MaxScrollback { get; set; } = 2000;

    // 解析状态
    private enum ParseState { Ground, Esc, Csi, Osc, OscEsc }
    private ParseState _state = ParseState.Ground;
    private readonly StringBuilder _param = new();

    public TerminalModel(int cols, int rows)
    {
        MaxScrollback = Math.Max(200, TerminalSettings.Scrollback);
        Resize(cols, rows);
    }

    public int Cols => _cols;
    public int Rows => _rows;
    public int CursorX => _cx;
    public int CursorY => _cy;
    public bool CursorVisible => _cursorVisible && !_altScreen ? _cursorVisible : _cursorVisible;

    /// <summary>可视区相对底部的滚动偏移（0=底部）；由视图滚动滚动回看控制。</summary>
    public int ScrollOffset { get; set; }

    public int ScrollbackCount => _scrollback.Count;

    /// <summary>缓冲内容变化通知（视图据此 InvalidateVisual）。</summary>
    public event Action? Changed;

    public TerminalCell CellAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= _cols || y >= _rows)
            return default;
        return Active[y * _cols + x];
    }

    /// <summary>取回看行（offset 从 1 开始，1=最近滚出的一行）。始终返回当前列宽的行（旧行会补/截）。</summary>
    public TerminalCell[] ScrollbackLine(int indexFromEnd)
    {
        var idx = _scrollback.Count - indexFromEnd;
        if (idx < 0 || idx >= _scrollback.Count)
            return EmptyLine();

        var src = _scrollback[idx];
        if (src.Length == _cols)
            return src;

        // 终端宽度变化后，旧回看行长度与当前不同：补齐到当前列宽，避免越界
        var line = EmptyLine();
        Array.Copy(src, line, Math.Min(src.Length, _cols));
        return line;
    }

    /// <summary>当前屏幕最后一行非空文本（用于判断是否已回到提示符）。</summary>
    public string LastNonEmptyLineText()
    {
        for (var y = _rows - 1; y >= 0; y--)
        {
            var sb = new System.Text.StringBuilder(_cols);
            for (var x = 0; x < _cols; x++)
            {
                var ch = Active[y * _cols + x].Ch;
                sb.Append(ch == '\0' ? ' ' : ch);
            }
            var text = sb.ToString().TrimEnd();
            if (text.Length > 0)
                return text;
        }
        return string.Empty;
    }

    private TerminalCell[] EmptyLine()
    {
        var line = new TerminalCell[_cols];
        for (var i = 0; i < _cols; i++)
            line[i] = new TerminalCell { Ch = ' ', Attr = TerminalAttributes.Default };
        return line;
    }

    /// <summary>清空当前屏幕缓冲与回看（本地清屏）。</summary>
    public void ClearMainBuffer()
    {
        Fill(Active, _cols, _rows, TerminalAttributes.Default);
        if (!_altScreen)
            _scrollback.Clear();
        _cx = 0;
        _cy = 0;
        _wrapPending = false;
        RaiseChanged();
    }

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(2, cols);
        rows = Math.Max(2, rows);
        if (cols == _cols && rows == _rows && _cells.Length == cols * rows)
            return;

        var newCells = new TerminalCell[cols * rows];
        var newAlt = new TerminalCell[cols * rows];
        Fill(newCells, cols, rows, TerminalAttributes.Default);
        Fill(newAlt, cols, rows, TerminalAttributes.Default);

        var copyCols = _cols == 0 ? 0 : Math.Min(cols, _cols);
        var copyRows = _rows == 0 ? 0 : Math.Min(rows, _rows);
        for (var y = 0; y < copyRows; y++)
            for (var x = 0; x < copyCols; x++)
            {
                newCells[y * cols + x] = _cells[y * _cols + x];
                newAlt[y * cols + x] = _altCells[y * _cols + x];
            }

        _cols = cols;
        _rows = rows;
        _cells = newCells;
        _altCells = newAlt;
        _scrollTop = 0;
        _scrollBottom = rows - 1;
        _cx = Math.Min(_cx, cols - 1);
        _cy = Math.Min(_cy, rows - 1);
        RaiseChanged();
    }

    private static void Fill(TerminalCell[] cells, int cols, int rows, TerminalAttributes attr)
    {
        for (var i = 0; i < cells.Length; i++)
            cells[i] = new TerminalCell { Ch = ' ', Attr = attr };
    }

    /// <summary>把远端字节按 UTF-8 解码后送入解析器。</summary>
    public void Feed(byte[] data, int offset, int count)
    {
        if (count <= 0)
            return;
        var text = Encoding.UTF8.GetString(data, offset, count);
        Feed(text);
    }

    public void Feed(string text)
    {
        foreach (var ch in text)
            Process(ch);
        RaiseChanged();
    }

    private TerminalCell[] Active => _altScreen ? _altCells : _cells;

    private void SetCell(int x, int y, char ch, TerminalAttributes attr, bool clone = true)
    {
        if (x < 0 || y < 0 || x >= _cols || y >= _rows)
            return;
        var attrCopy = attr;
        Active[y * _cols + x] = new TerminalCell { Ch = ch, Attr = attrCopy };
    }

    private void Process(char ch)
    {
        switch (_state)
        {
            case ParseState.Ground:
                ProcessGround(ch);
                break;
            case ParseState.Esc:
                ProcessEsc(ch);
                break;
            case ParseState.Csi:
                ProcessCsi(ch);
                break;
            case ParseState.Osc:
                if (ch == '\a') _state = ParseState.Ground;
                else if (ch == '\u001b') _state = ParseState.OscEsc;
                break;
            case ParseState.OscEsc:
                _state = ch == '\\' ? ParseState.Ground : ParseState.Osc;
                break;
        }
    }

    private void ProcessGround(char ch)
    {
        switch (ch)
        {
            case '\u001b':
                _state = ParseState.Esc;
                return;
            case '\r':
                _cx = 0;
                _wrapPending = false;
                return;
            case '\n':
            case '\v':
            case '\f':
                LineFeed();
                return;
            case '\b':
                if (_cx > 0) _cx--;
                _wrapPending = false;
                return;
            case '\t':
                _cx = Math.Min(_cols - 1, (_cx / 8 + 1) * 8);
                return;
            case '\a':
                return;
            case '\u000e':
            case '\u000f':
            case '\u0000':
                return;
        }

        if (ch < ' ')
            return;

        if (_wrapPending)
        {
            _cx = 0;
            LineFeed();
            _wrapPending = false;
        }

        var width = IsWide(ch) ? 2 : 1;
        if (width == 2 && _cx >= _cols - 1 && _autoWrap)
        {
            _cx = 0;
            LineFeed();
        }

        SetCell(_cx, _cy, ch, _attr);
        if (width == 2 && _cx + 1 < _cols)
            SetCell(_cx + 1, _cy, ' ', _attr);

        if (_cx + width >= _cols)
        {
            if (_autoWrap)
                _wrapPending = true;
            else
                _cx = _cols - 1;
        }
        else
        {
            _cx += width;
        }
    }

    /// <summary>CJK/全角字符占两列。</summary>
    private static bool IsWide(char c)
        => (c >= 0x1100 && c <= 0x115F)
        || (c >= 0x2E80 && c <= 0x303E)
        || (c >= 0x3041 && c <= 0x33FF)
        || (c >= 0x3400 && c <= 0x4DBF)
        || (c >= 0x4E00 && c <= 0x9FFF)
        || (c >= 0xA000 && c <= 0xA4CF)
        || (c >= 0xAC00 && c <= 0xD7A3)
        || (c >= 0xF900 && c <= 0xFAFF)
        || (c >= 0xFE30 && c <= 0xFE4F)
        || (c >= 0xFF00 && c <= 0xFF60)
        || (c >= 0xFFE0 && c <= 0xFFE6);

    private void ProcessEsc(char ch)
    {
        switch (ch)
        {
            case '[':
                _param.Clear();
                _state = ParseState.Csi;
                return;
            case ']':
                _state = ParseState.Osc;
                return;
            case '7':
                _savedCx = _cx;
                _savedCy = _cy;
                _savedAttr = _attr;
                _state = ParseState.Ground;
                return;
            case '8':
                _cx = Math.Min(_savedCx, _cols - 1);
                _cy = Math.Min(_savedCy, _rows - 1);
                _attr = _savedAttr;
                _state = ParseState.Ground;
                return;
            case 'D':
                LineFeed();
                _state = ParseState.Ground;
                return;
            case 'M':
                ReverseLineFeed();
                _state = ParseState.Ground;
                return;
            case 'E':
                _cx = 0;
                LineFeed();
                _state = ParseState.Ground;
                return;
            case 'c':
                Reset();
                _state = ParseState.Ground;
                return;
            case '(':
            case ')':
            case '*':
            case '+':
                _state = ParseState.Ground; // 忽略字符集选择（吞掉下一个字符）
                return;
            default:
                _state = ParseState.Ground;
                return;
        }
    }

    private void ProcessCsi(char ch)
    {
        if (ch == '?' || ch == '>' || ch == '!' || ch == '=' || (ch >= '0' && ch <= '9') || ch == ';' || ch == ':')
        {
            _param.Append(ch);
            return;
        }

        _state = ParseState.Ground;
        var isPrivate = _param.Length > 0 && _param[0] == '?';
        var body = isPrivate ? _param.ToString(1, _param.Length - 1) : _param.ToString();
        var args = ParseArgs(body);

        switch (ch)
        {
            case 'A': MoveCursor(0, -Arg(args, 0, 1)); break;
            case 'B': MoveCursor(0, Arg(args, 0, 1)); break;
            case 'C': MoveCursor(Arg(args, 0, 1), 0); break;
            case 'D': MoveCursor(-Arg(args, 0, 1), 0); break;
            case 'E': _cx = 0; MoveCursor(0, Arg(args, 0, 1)); break;
            case 'F': _cx = 0; MoveCursor(0, -Arg(args, 0, 1)); break;
            case 'G': _cx = Math.Clamp(Arg(args, 0, 1) - 1, 0, _cols - 1); break;
            case 'd': _cy = Math.Clamp(Arg(args, 0, 1) - 1, 0, _rows - 1); break;
            case 'H':
            case 'f':
                _cy = Math.Clamp(Arg(args, 0, 1) - 1, 0, _rows - 1);
                _cx = Math.Clamp(Arg(args, 1, 1) - 1, 0, _cols - 1);
                break;
            case 'J': EraseInDisplay(Arg(args, 0, 0)); break;
            case 'K': EraseInLine(Arg(args, 0, 0)); break;
            case 'X': EraseChars(Arg(args, 0, 1)); break;
            case 'L': InsertLines(Arg(args, 0, 1)); break;
            case 'M': DeleteLines(Arg(args, 0, 1)); break;
            case 'P': DeleteChars(Arg(args, 0, 1)); break;
            case '@': InsertChars(Arg(args, 0, 1)); break;
            case 'S': ScrollUp(Arg(args, 0, 1)); break;
            case 'T': ScrollDown(Arg(args, 0, 1)); break;
            case 'm': ApplySgr(args); break;
            case 'r': SetScrollRegion(args); break;
            case 's': _savedCx = _cx; _savedCy = _cy; _savedAttr = _attr; break;
            case 'u': _cx = _savedCx; _cy = _savedCy; _attr = _savedAttr; break;
            case 'h': SetMode(isPrivate, args, true); break;
            case 'l': SetMode(isPrivate, args, false); break;
            default: break;
        }
    }

    private static int[] ParseArgs(string body)
    {
        if (string.IsNullOrEmpty(body))
            return Array.Empty<int>();
        var parts = body.Split(';');
        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            var colon = p.IndexOf(':');
            if (colon >= 0) p = p[..colon];
            result[i] = int.TryParse(p, out var v) ? v : 0;
        }
        return result;
    }

    private static int Arg(int[] args, int index, int fallback)
        => index < args.Length && args[index] > 0 ? args[index] : fallback;

    private void MoveCursor(int dx, int dy)
    {
        _cx = Math.Clamp(_cx + dx, 0, _cols - 1);
        _cy = Math.Clamp(_cy + dy, _scrollTop, _scrollBottom);
        _wrapPending = false;
    }

    private void LineFeed()
    {
        _wrapPending = false;
        if (_cy == _scrollBottom)
            ScrollUp(1);
        else if (_cy < _rows - 1)
            _cy++;
    }

    private void ReverseLineFeed()
    {
        _wrapPending = false;
        if (_cy == _scrollTop)
            ScrollDown(1);
        else if (_cy > 0)
            _cy--;
    }

    private void ScrollUp(int n)
    {
        n = Math.Clamp(n, 1, _scrollBottom - _scrollTop + 1);
        var active = Active;
        var height = _scrollBottom - _scrollTop + 1;
        if (!_altScreen && _scrollTop == 0 && n <= height)
        {
            for (var i = 0; i < n; i++)
                _scrollback.Add(GetScreenLine(active, i));
            if (_scrollback.Count > MaxScrollback)
                _scrollback.RemoveRange(0, _scrollback.Count - MaxScrollback);
        }

        for (var y = _scrollTop; y <= _scrollBottom - n; y++)
            Array.Copy(active, (y + n) * _cols, active, y * _cols, _cols);
        var blank = TerminalAttributes.Default;
        for (var y = _scrollBottom - n + 1; y <= _scrollBottom; y++)
            for (var x = 0; x < _cols; x++)
                active[y * _cols + x] = new TerminalCell { Ch = ' ', Attr = blank };
    }

    private void ScrollDown(int n)
    {
        n = Math.Clamp(n, 1, _scrollBottom - _scrollTop + 1);
        var active = Active;
        for (var y = _scrollBottom; y >= _scrollTop + n; y--)
            Array.Copy(active, (y - n) * _cols, active, y * _cols, _cols);
        for (var y = _scrollTop; y < _scrollTop + n; y++)
            for (var x = 0; x < _cols; x++)
                active[y * _cols + x] = new TerminalCell { Ch = ' ', Attr = TerminalAttributes.Default };
    }

    private TerminalCell[] GetScreenLine(TerminalCell[] active, int y)
    {
        var line = new TerminalCell[_cols];
        Array.Copy(active, y * _cols, line, 0, _cols);
        return line;
    }

    private void EraseInDisplay(int mode)
    {
        var active = Active;
        void Blank(int x, int y) => active[y * _cols + x] = new TerminalCell { Ch = ' ', Attr = _attr };

        switch (mode)
        {
            case 0:
                for (var x = _cx; x < _cols; x++) Blank(x, _cy);
                for (var y = _cy + 1; y < _rows; y++)
                    for (var x = 0; x < _cols; x++) Blank(x, y);
                break;
            case 1:
                for (var y = 0; y < _cy; y++)
                    for (var x = 0; x < _cols; x++) Blank(x, y);
                for (var x = 0; x <= _cx; x++) Blank(x, _cy);
                break;
            case 2:
            case 3:
                for (var y = 0; y < _rows; y++)
                    for (var x = 0; x < _cols; x++) Blank(x, y);
                break;
        }
    }

    private void EraseInLine(int mode)
    {
        var active = Active;
        void Blank(int x) => active[_cy * _cols + x] = new TerminalCell { Ch = ' ', Attr = _attr };
        switch (mode)
        {
            case 0: for (var x = _cx; x < _cols; x++) Blank(x); break;
            case 1: for (var x = 0; x <= _cx; x++) Blank(x); break;
            case 2: for (var x = 0; x < _cols; x++) Blank(x); break;
        }
    }

    private void EraseChars(int n)
    {
        var active = Active;
        for (var i = 0; i < n && _cx + i < _cols; i++)
            active[_cy * _cols + _cx + i] = new TerminalCell { Ch = ' ', Attr = _attr };
    }

    private void InsertLines(int n)
    {
        if (_cy < _scrollTop || _cy > _scrollBottom) return;
        var active = Active;
        for (var y = _scrollBottom; y >= _cy + n; y--)
            Array.Copy(active, (y - n) * _cols, active, y * _cols, _cols);
        for (var y = _cy; y < _cy + n && y <= _scrollBottom; y++)
            for (var x = 0; x < _cols; x++)
                active[y * _cols + x] = new TerminalCell { Ch = ' ', Attr = TerminalAttributes.Default };
    }

    private void DeleteLines(int n)
    {
        if (_cy < _scrollTop || _cy > _scrollBottom) return;
        var active = Active;
        for (var y = _cy; y <= _scrollBottom - n; y++)
            Array.Copy(active, (y + n) * _cols, active, y * _cols, _cols);
        for (var y = _scrollBottom - n + 1; y <= _scrollBottom; y++)
            for (var x = 0; x < _cols; x++)
                active[y * _cols + x] = new TerminalCell { Ch = ' ', Attr = TerminalAttributes.Default };
    }

    private void InsertChars(int n)
    {
        var active = Active;
        n = Math.Clamp(n, 1, _cols);
        for (var x = _cols - 1; x >= _cx + n; x--)
            active[_cy * _cols + x] = active[_cy * _cols + x - n];
        for (var x = _cx; x < _cx + n && x < _cols; x++)
            active[_cy * _cols + x] = new TerminalCell { Ch = ' ', Attr = _attr };
    }

    private void DeleteChars(int n)
    {
        var active = Active;
        n = Math.Clamp(n, 1, _cols);
        for (var x = _cx; x < _cols - n; x++)
            active[_cy * _cols + x] = active[_cy * _cols + x + n];
        for (var x = Math.Max(_cx, _cols - n); x < _cols; x++)
            active[_cy * _cols + x] = new TerminalCell { Ch = ' ', Attr = _attr };
    }

    private void SetScrollRegion(int[] args)
    {
        var top = Arg(args, 0, 1) - 1;
        var bottom = Arg(args, 1, _rows) - 1;
        top = Math.Clamp(top, 0, _rows - 1);
        bottom = Math.Clamp(bottom, 0, _rows - 1);
        if (top >= bottom)
            return;
        _scrollTop = top;
        _scrollBottom = bottom;
        _cx = 0;
        _cy = top;
    }

    private void SetMode(bool priv, int[] args, bool enable)
    {
        if (!priv)
            return;
        foreach (var a in args)
        {
            switch (a)
            {
                case 25: _cursorVisible = enable; break;
                case 7: _autoWrap = enable; break;
                case 47:
                case 1047:
                case 1049:
                    SwitchAltScreen(enable);
                    break;
                default: break;
            }
        }
    }

    private void SwitchAltScreen(bool enable)
    {
        if (enable == _altScreen)
            return;
        if (enable)
        {
            _savedCx = _cx; _savedCy = _cy; _savedAttr = _attr;
            _altScreen = true;
            Fill(_altCells, _cols, _rows, TerminalAttributes.Default);
            _cx = 0; _cy = 0;
            _scrollTop = 0; _scrollBottom = _rows - 1;
        }
        else
        {
            _altScreen = false;
            _scrollTop = 0; _scrollBottom = _rows - 1;
            _cx = Math.Min(_savedCx, _cols - 1);
            _cy = Math.Min(_savedCy, _rows - 1);
            _attr = _savedAttr;
        }
    }

    private void Reset()
    {
        Fill(_cells, _cols, _rows, TerminalAttributes.Default);
        Fill(_altCells, _cols, _rows, TerminalAttributes.Default);
        _altScreen = false;
        _cx = _cy = 0;
        _attr = TerminalAttributes.Default;
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
        _cursorVisible = true;
        _autoWrap = true;
        _wrapPending = false;
    }

    private void ApplySgr(int[] args)
    {
        if (args.Length == 0)
        {
            _attr = TerminalAttributes.Default;
            return;
        }

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case 0: _attr = TerminalAttributes.Default; break;
                case 1: _attr.Bold = true; break;
                case 3: _attr.Italic = true; break;
                case 4: _attr.Underline = true; break;
                case 7: _attr.Reverse = true; break;
                case 22: _attr.Bold = false; break;
                case 23: _attr.Italic = false; break;
                case 24: _attr.Underline = false; break;
                case 27: _attr.Reverse = false; break;
                case >= 30 and <= 37: _attr.Fg = BasicColor(a - 30); break;
                case 39: _attr.Fg = null; break;
                case >= 40 and <= 47: _attr.Bg = BasicColor(a - 40); break;
                case 49: _attr.Bg = null; break;
                case >= 90 and <= 97: _attr.Fg = BrightColor(a - 90); break;
                case >= 100 and <= 107: _attr.Bg = BrightColor(a - 100); break;
                case 38:
                case 48:
                    var isFg = a == 38;
                    if (i + 1 < args.Length && args[i + 1] == 5 && i + 2 < args.Length)
                    {
                        var color = Color256(args[i + 2]);
                        if (isFg) _attr.Fg = color; else _attr.Bg = color;
                        i += 2;
                    }
                    else if (i + 1 < args.Length && args[i + 1] == 2 && i + 4 < args.Length)
                    {
                        var color = Rgb(args[i + 2], args[i + 3], args[i + 4]);
                        if (isFg) _attr.Fg = color; else _attr.Bg = color;
                        i += 4;
                    }
                    break;
                default: break;
            }
        }
    }

    private static uint Rgb(int r, int g, int b)
        => (uint)((Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255));

    private static readonly uint[] Ansi16 =
    {
        0x000000, 0xCD0000, 0x00CD00, 0xCDCD00, 0x0000EE, 0xCD00CD, 0x00CDCD, 0xE5E5E5,
        0x7F7F7F, 0xFF0000, 0x00FF00, 0xFFFF00, 0x5C5CFF, 0xFF00FF, 0x00FFFF, 0xFFFFFF
    };

    private static uint BasicColor(int index) => Ansi16[Math.Clamp(index, 0, 7)];
    private static uint BrightColor(int index) => Ansi16[8 + Math.Clamp(index, 0, 7)];

    private static uint Color256(int index)
    {
        index = Math.Clamp(index, 0, 255);
        if (index < 16)
            return Ansi16[index];
        if (index >= 232)
        {
            var v = (uint)(8 + (index - 232) * 10);
            return (v << 16) | (v << 8) | v;
        }
        var n = index - 16;
        var r = n / 36;
        var g = (n % 36) / 6;
        var b = n % 6;
        static uint C(int c) => c == 0 ? 0u : (uint)(55 + c * 40);
        return (C(r) << 16) | (C(g) << 8) | C(b);
    }

    private void RaiseChanged() => Changed?.Invoke();
}
