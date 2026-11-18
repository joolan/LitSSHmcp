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
    public bool WideContinuation;
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
    private readonly List<bool> _scrollbackWrapped = new();
    private bool[] _lineWrapped = Array.Empty<bool>();

    /// <summary>是否处于备用屏（vim/top/less 等全屏程序）。</summary>
    public bool IsAltScreen => _altScreen;

    /// <summary>远端 shell 是否启用了 bracketed paste（CSI ?2004h）。</summary>
    public bool BracketedPasteEnabled { get; private set; }

    /// <summary>应用光标键模式（DECCKM，CSI ?1h）：方向键应发送 ESC O A/B/C/D，而不是 ESC [ A/B/C/D。</summary>
    public bool ApplicationCursorKeys { get; private set; }

    /// <summary>OSC 7 报告的工作目录（shell integration）。</summary>
    public string? CurrentDirectory { get; private set; }

    /// <summary>OSC 133 报告的命令执行状态。</summary>
    public bool IsCommandRunning { get; private set; }

    /// <summary>上一条命令退出码（OSC 133 D）。</summary>
    public int? LastExitCode { get; private set; }

    public event Action? ShellStateChanged;

    /// <summary>向远端终端程序回复 DSR/DA/颜色查询等终端响应。</summary>
    public event Action<string>? ResponseRequested;

    public uint ThemeFg { get; set; } = 0xD4D4D4;
    public uint ThemeBg { get; set; } = 0x1E1E1E;

    /// <summary>ANSI 16 色板（由当前终端主题注入；默认使用内置色板）。</summary>
    public uint[] AnsiPalette { get; set; } = TerminalTheme.DefaultAnsi16;

    /// <summary>回看行数上限。</summary>
    public int MaxScrollback { get; set; } = 2000;

    // 解析状态
    private enum ParseState { Ground, Esc, Csi, Osc, OscEsc, Dcs, DcsEsc }
    private ParseState _state = ParseState.Ground;
    private readonly StringBuilder _param = new();
    private readonly StringBuilder _osc = new();

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
        {
            _scrollback.Clear();
            _scrollbackWrapped.Clear();
        }
        Array.Clear(_lineWrapped, 0, _lineWrapped.Length);
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

        // 备用屏（vim/top）自身会重绘；但主屏内容仍要在后台重排，
        // 否则退出 vim 后主屏会保留缩窄时的截断状态。
        if (_altScreen)
        {
            var (mainCx, mainCy) = ReflowMainBuffer(cols, rows, _savedCx, _savedCy);
            var newAlt = new TerminalCell[cols * rows];
            Fill(newAlt, cols, rows, TerminalAttributes.Default);
            var copyCols = _cols == 0 ? 0 : Math.Min(cols, _cols);
            var copyRows = _rows == 0 ? 0 : Math.Min(rows, _rows);
            for (var y = 0; y < copyRows; y++)
                for (var x = 0; x < copyCols; x++)
                    newAlt[y * cols + x] = _altCells[y * _cols + x];

            _cols = cols;
            _rows = rows;
            _altCells = newAlt;
            _savedCx = mainCx;
            _savedCy = mainCy;
            _scrollTop = 0;
            _scrollBottom = rows - 1;
            _cx = Math.Min(_cx, cols - 1);
            _cy = Math.Min(_cy, rows - 1);
            RaiseChanged();
            return;
        }

        var (newCx, newCy) = ReflowMainBuffer(cols, rows, _cx, _cy);
        var newAltCells = new TerminalCell[cols * rows];
        Fill(newAltCells, cols, rows, TerminalAttributes.Default);
        _cols = cols;
        _rows = rows;
        _altCells = newAltCells;
        _scrollTop = 0;
        _scrollBottom = rows - 1;
        _cx = newCx;
        _cy = newCy;
        _wrapPending = false;
        RaiseChanged();
    }

    /// <summary>重排主屏与滚动历史，返回主屏光标的新位置。</summary>
    private (int Cx, int Cy) ReflowMainBuffer(int cols, int rows, int cursorX, int cursorY)
    {
        var sourceRows = new List<TerminalCell[]>(_scrollback.Count + _rows);
        var sourceWrapped = new List<bool>(_scrollback.Count + _rows);
        for (var i = 0; i < _scrollback.Count; i++)
        {
            sourceRows.Add(_scrollback[i]);
            sourceWrapped.Add(i < _scrollbackWrapped.Count && _scrollbackWrapped[i]);
        }
        for (var i = 0; i < _rows; i++)
        {
            var line = new TerminalCell[_cols];
            Array.Copy(_cells, i * _cols, line, 0, _cols);
            sourceRows.Add(line);
            sourceWrapped.Add(i < _lineWrapped.Length && _lineWrapped[i]);
        }

        var cursorGlobalRow = _scrollback.Count + cursorY;
        var logical = new List<List<TerminalCell>>();
        var cursorLogical = -1;
        var cursorOffset = 0;
        for (var i = 0; i < sourceRows.Count; i++)
        {
            if (i == 0 || !sourceWrapped[i - 1])
                logical.Add(new List<TerminalCell>(_cols));
            var current = logical[^1];
            if (i == cursorGlobalRow)
            {
                cursorLogical = logical.Count - 1;
                cursorOffset = current.Count + Math.Clamp(cursorX, 0, Math.Max(0, sourceRows[i].Length - 1));
            }
            current.AddRange(sourceRows[i]);
        }

        if (logical.Count == 0)
            logical.Add(new List<TerminalCell>());
        for (var i = 0; i < logical.Count; i++)
        {
            var line = logical[i];
            while (line.Count > 0 && (line[^1].Ch == ' ' || line[^1].Ch == '\0'))
                line.RemoveAt(line.Count - 1);
            if (i == cursorLogical && cursorOffset > line.Count)
                cursorOffset = line.Count;
        }

        // 屏幕底部的空行只是填充，不应在缩窄后变成滚动历史。
        while (logical.Count > 1 && logical[^1].Count == 0 && logical.Count - 1 != cursorLogical)
            logical.RemoveAt(logical.Count - 1);

        var newRows = new List<TerminalCell[]>();
        var newWrapped = new List<bool>();
        var cursorNewRow = 0;
        var cursorNewCol = 0;
        for (var lineIndex = 0; lineIndex < logical.Count; lineIndex++)
        {
            var line = logical[lineIndex];
            var chunkCount = Math.Max(1, (line.Count + cols - 1) / cols);
            var firstChunkRow = newRows.Count;
            for (var chunk = 0; chunk < chunkCount; chunk++)
            {
                var cells = new TerminalCell[cols];
                for (var x = 0; x < cols; x++)
                    cells[x] = new TerminalCell { Ch = ' ', Attr = TerminalAttributes.Default };
                for (var x = 0; x < cols; x++)
                {
                    var source = chunk * cols + x;
                    if (source < line.Count)
                        cells[x] = line[source];
                }
                newRows.Add(cells);
                newWrapped.Add(chunk + 1 < chunkCount);
            }
            if (lineIndex == cursorLogical)
            {
                var offset = Math.Min(cursorOffset, line.Count);
                var rowOffset = Math.Min(offset / cols, chunkCount - 1);
                cursorNewRow = firstChunkRow + rowOffset;
                cursorNewCol = Math.Clamp(offset % cols, 0, cols - 1);
            }
        }

        if (newRows.Count == 0)
        {
            var blank = new TerminalCell[cols];
            Fill(blank, cols, 1, TerminalAttributes.Default);
            newRows.Add(blank);
            newWrapped.Add(false);
        }

        var total = newRows.Count;
        var screenStart = Math.Max(0, total - rows);
        _scrollback.Clear();
        _scrollbackWrapped.Clear();
        for (var i = 0; i < screenStart; i++)
        {
            _scrollback.Add(newRows[i]);
            _scrollbackWrapped.Add(newWrapped[i]);
        }
        if (_scrollback.Count > MaxScrollback)
        {
            var trim = _scrollback.Count - MaxScrollback;
            _scrollback.RemoveRange(0, trim);
            if (_scrollbackWrapped.Count >= trim)
                _scrollbackWrapped.RemoveRange(0, trim);
        }

        var newCells = new TerminalCell[cols * rows];
        Fill(newCells, cols, rows, TerminalAttributes.Default);
        var newWrappedFlags = new bool[rows];
        var screenWriteRow = 0;
        for (var i = screenStart; i < total && screenWriteRow < rows; i++, screenWriteRow++)
        {
            Array.Copy(newRows[i], 0, newCells, screenWriteRow * cols, cols);
            newWrappedFlags[screenWriteRow] = newWrapped[i];
        }

        _cells = newCells;
        _lineWrapped = newWrappedFlags;
        return (Math.Clamp(cursorNewCol, 0, cols - 1),
            Math.Clamp(cursorNewRow - screenStart, 0, rows - 1));
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
        {
            try
            {
                Process(ch);
            }
            catch
            {
                // 单个异常序列不应让整屏内容停在未刷新状态。
                _state = ParseState.Ground;
                _param.Clear();
                _osc.Clear();
            }
        }
        RaiseChanged();
    }

    private TerminalCell[] Active => _altScreen ? _altCells : _cells;

    private void SetCell(int x, int y, char ch, TerminalAttributes attr, bool wideContinuation = false)
    {
        if (x < 0 || y < 0 || x >= _cols || y >= _rows)
            return;
        Active[y * _cols + x] = new TerminalCell { Ch = ch, Attr = attr, WideContinuation = wideContinuation };
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
                if (ch == '\a')
                {
                    ProcessOsc(_osc.ToString());
                    _osc.Clear();
                    _state = ParseState.Ground;
                }
                else if (ch == '\u001b')
                {
                    _state = ParseState.OscEsc;
                }
                else
                {
                    _osc.Append(ch);
                }
                break;
            case ParseState.OscEsc:
                if (ch == '\\')
                {
                    ProcessOsc(_osc.ToString());
                    _osc.Clear();
                    _state = ParseState.Ground;
                }
                else
                {
                    _osc.Append('\u001b').Append(ch);
                    _state = ParseState.Osc;
                }
                break;
            case ParseState.Dcs:
                // DCS/SOS/PM/APC 内容全部吞掉，不渲染（只支持 ST = ESC \ 终止）。
                if (ch == '\u001b')
                    _state = ParseState.DcsEsc;
                break;
            case ParseState.DcsEsc:
                if (ch == '\\')
                    _state = ParseState.Ground;
                else if (ch == '\u001b')
                    _state = ParseState.DcsEsc;
                else
                    _state = ParseState.Dcs;
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
                LineFeed(wrapped: false);
                return;
            case '\b':
                if (_cx > 0) _cx--;
                _wrapPending = false;
                return;
            case '\t':
                _cx = Math.Min(_cols - 1, (_cx / 8 + 1) * 8);
                _wrapPending = false;
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
            LineFeed(wrapped: true);
            _wrapPending = false;
        }

        var width = IsWide(ch) ? 2 : 1;
        if (width == 2 && _cx >= _cols - 1 && _autoWrap)
        {
            _cx = 0;
            LineFeed(wrapped: true);
        }

        SetCell(_cx, _cy, ch, _attr);
        if (width == 2 && _cx + 1 < _cols)
            SetCell(_cx + 1, _cy, ' ', _attr, wideContinuation: true);

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

    private void ProcessOsc(string text)
    {
        if (text.StartsWith("133;", StringComparison.Ordinal))
        {
            var code = text.Length > 4 ? text[4] : '\0';
            switch (code)
            {
                case 'A':
                case 'B':
                    IsCommandRunning = false;
                    ShellStateChanged?.Invoke();
                    break;
                case 'C':
                    IsCommandRunning = true;
                    LastExitCode = null;
                    ShellStateChanged?.Invoke();
                    break;
                case 'D':
                    IsCommandRunning = false;
                    LastExitCode = null;
                    var parts = text.Split(';');
                    if (parts.Length > 2 && int.TryParse(parts[2], out var exitCode))
                        LastExitCode = exitCode;
                    ShellStateChanged?.Invoke();
                    break;
            }
            return;
        }

        if (text.StartsWith("7;file://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(text[2..], UriKind.Absolute, out var uri))
        {
            CurrentDirectory = Uri.UnescapeDataString(uri.AbsolutePath);
            ShellStateChanged?.Invoke();
            return;
        }

        if (text == "10;?" || text == "11;?" || text == "12;?")
        {
            var rgb = text == "11;?" ? ThemeBg : ThemeFg;
            ResponseRequested?.Invoke($"\u001b]{text[..2]};{OscRgb(rgb)}\a");
        }
    }

    private static string OscRgb(uint rgb)
    {
        var r = ((rgb >> 16) & 0xff) * 0x101;
        var g = ((rgb >> 8) & 0xff) * 0x101;
        var b = (rgb & 0xff) * 0x101;
        return $"rgb:{r:X4}/{g:X4}/{b:X4}";
    }

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
                _wrapPending = false;
                _state = ParseState.Ground;
                return;
            case 'D':
                LineFeed(wrapped: false);
                _state = ParseState.Ground;
                return;
            case 'M':
                ReverseLineFeed();
                _state = ParseState.Ground;
                return;
            case 'E':
                _cx = 0;
                LineFeed(wrapped: false);
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
            case 'P':
            case 'X':
            case '^':
            case '_':
                // DCS/SOS/PM/APC：吞掉后续内容直到 ST(ESC \)，否则中间字节会被渲染成乱码。
                _state = ParseState.Dcs;
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
            case 'G':
                _cx = Math.Clamp(Arg(args, 0, 1) - 1, 0, _cols - 1);
                _wrapPending = false;
                break;
            case 'd':
                _cy = Math.Clamp(Arg(args, 0, 1) - 1, 0, _rows - 1);
                _wrapPending = false;
                break;
            case 'H':
            case 'f':
                _cy = Math.Clamp(Arg(args, 0, 1) - 1, 0, _rows - 1);
                _cx = Math.Clamp(Arg(args, 1, 1) - 1, 0, _cols - 1);
                // 显式定位必须终止"待换行"：否则下一个字符会先换行，
                // 若此时位于最后一行将触发滚屏，首行内容被永久挤出（vim/less 表现为缺第一行）。
                _wrapPending = false;
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
            case 'u':
                _cx = _savedCx;
                _cy = _savedCy;
                _attr = _savedAttr;
                _wrapPending = false;
                break;
            case 'h': SetMode(isPrivate, args, true); break;
            case 'l': SetMode(isPrivate, args, false); break;
            case 'n' when !isPrivate && Arg(args, 0, 0) == 6:
                ResponseRequested?.Invoke($"\u001b[{_cy + 1};{_cx + 1}R");
                break;
            case 'c':
                // DA1(CSI c) 应回 "CSI ? 1;2 c"（VT100+AVO），DA2(CSI > c) 应回 "CSI > ..."。
                // 两者不能互换：全屏程序（vim/less）依赖 DA1 前缀 "?" 判断终端能力。
                ResponseRequested?.Invoke(_param.Length > 0 && _param[0] == '>'
                    ? "\u001b[>0;95;0c"
                    : "\u001b[?1;2c");
                break;
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

    private void LineFeed(bool wrapped)
    {
        _wrapPending = false;
        if (!_altScreen && _cy >= 0 && _cy < _lineWrapped.Length)
            _lineWrapped[_cy] = wrapped;
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
            {
                _scrollback.Add(GetScreenLine(active, i));
                _scrollbackWrapped.Add(i < _lineWrapped.Length && _lineWrapped[i]);
            }
            if (_scrollback.Count > MaxScrollback)
            {
                var trim = _scrollback.Count - MaxScrollback;
                _scrollback.RemoveRange(0, trim);
                if (_scrollbackWrapped.Count >= trim)
                    _scrollbackWrapped.RemoveRange(0, trim);
            }
        }

        for (var y = _scrollTop; y <= _scrollBottom - n; y++)
            Array.Copy(active, (y + n) * _cols, active, y * _cols, _cols);
        if (!_altScreen && _lineWrapped.Length == _rows)
        {
            Array.Copy(_lineWrapped, _scrollTop + n, _lineWrapped, _scrollTop, height - n);
            for (var y = _scrollBottom - n + 1; y <= _scrollBottom; y++)
                _lineWrapped[y] = false;
        }
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
        if (!_altScreen && _lineWrapped.Length == _rows)
        {
            for (var y = _scrollBottom; y >= _scrollTop + n; y--)
                _lineWrapped[y] = _lineWrapped[y - n];
            for (var y = _scrollTop; y < _scrollTop + n; y++)
                _lineWrapped[y] = false;
        }
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
        n = Math.Clamp(n, 1, _scrollBottom - _scrollTop + 1);
        var active = Active;
        for (var y = _scrollBottom; y >= _cy + n; y--)
            Array.Copy(active, (y - n) * _cols, active, y * _cols, _cols);
        if (!_altScreen && _lineWrapped.Length == _rows)
        {
            for (var y = _scrollBottom; y >= _cy + n; y--)
                _lineWrapped[y] = _lineWrapped[y - n];
            for (var y = _cy; y < _cy + n && y <= _scrollBottom; y++)
                _lineWrapped[y] = false;
        }
        for (var y = _cy; y < _cy + n && y <= _scrollBottom; y++)
            for (var x = 0; x < _cols; x++)
                active[y * _cols + x] = new TerminalCell { Ch = ' ', Attr = TerminalAttributes.Default };
    }

    private void DeleteLines(int n)
    {
        if (_cy < _scrollTop || _cy > _scrollBottom) return;
        n = Math.Clamp(n, 1, _scrollBottom - _scrollTop + 1);
        var active = Active;
        for (var y = _cy; y <= _scrollBottom - n; y++)
            Array.Copy(active, (y + n) * _cols, active, y * _cols, _cols);
        if (!_altScreen && _lineWrapped.Length == _rows)
        {
            for (var y = _cy; y <= _scrollBottom - n; y++)
                _lineWrapped[y] = _lineWrapped[y + n];
            for (var y = _scrollBottom - n + 1; y <= _scrollBottom; y++)
                _lineWrapped[y] = false;
        }
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
        _wrapPending = false;
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
                case 1: ApplicationCursorKeys = enable; break;   // DECCKM
                case 47:
                case 1047:
                case 1049:
                    SwitchAltScreen(enable);
                    break;
                case 2004:
                    BracketedPasteEnabled = enable;
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
            _wrapPending = false;
            _scrollTop = 0; _scrollBottom = _rows - 1;
        }
        else
        {
            _altScreen = false;
            _scrollTop = 0; _scrollBottom = _rows - 1;
            _cx = Math.Min(_savedCx, _cols - 1);
            _cy = Math.Min(_savedCy, _rows - 1);
            _attr = _savedAttr;
            _wrapPending = false;
        }
    }

    private void Reset()
    {
        Fill(_cells, _cols, _rows, TerminalAttributes.Default);
        Fill(_altCells, _cols, _rows, TerminalAttributes.Default);
        _scrollback.Clear();
        _scrollbackWrapped.Clear();
        Array.Clear(_lineWrapped, 0, _lineWrapped.Length);
        _altScreen = false;
        _cx = _cy = 0;
        _attr = TerminalAttributes.Default;
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
        _cursorVisible = true;
        _autoWrap = true;
        _wrapPending = false;
        BracketedPasteEnabled = false;
        IsCommandRunning = false;
        LastExitCode = null;
        CurrentDirectory = null;
        _osc.Clear();
        ShellStateChanged?.Invoke();
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

    private uint BasicColor(int index) => AnsiPalette[Math.Clamp(index, 0, 7)];
    private uint BrightColor(int index) => AnsiPalette[8 + Math.Clamp(index, 0, 7)];

    private uint Color256(int index)
    {
        index = Math.Clamp(index, 0, 255);
        if (index < 16)
            return AnsiPalette[index];
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
