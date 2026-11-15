using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Controls;

/// <summary>
/// 自绘终端控件：渲染 <see cref="TerminalModel"/>，处理键盘/IME、选区复制粘贴、滚动回看、搜索与尺寸变化。
/// 内含近乎不可见的 <see cref="TextBox"/> 作为输入宿主（支持中文输入法）。
/// </summary>
public sealed class TerminalView : Grid
{
    private readonly Dictionary<uint, SolidColorBrush> _brushCache = new();
    private readonly Dictionary<long, FormattedText> _textCache = new();
    private readonly TextBox _ime;
    private readonly Border _findBar;
    private readonly TextBox _findBox;
    private readonly TextBlock _findCount;
    private readonly DispatcherTimer _findDebounce;
    private readonly DispatcherTimer _cursorTimer;
    private readonly DispatcherTimer _selectionCopyTimer;
    private bool _cursorOn = true;
    private readonly Dictionary<int, string> _lineTextCache = new();
    private static readonly UserDialogService Dialogs = new();

    private Typeface _typeface = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private Typeface _boldTypeface = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private double _fontSize = 14;
    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private double _pixelsPerDip = 1;
    private int _cols = 80;
    private int _rows = 24;
    private int _scrollOffset;
    private ITerminalSession? _session;

    // 选区（视图坐标：行/列）
    private bool _selecting;
    private bool _hasSelection;
    private int _selStartRow, _selStartCol, _selEndRow, _selEndCol;
    private int _clickCount;
    private long _lastClickTicks;
    private int _lastClickRow = -1, _lastClickCol = -1;

    // 搜索
    private readonly List<(int Line, int Col)> _findMatches = new();
    private int _findIndex = -1;

    public TerminalView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
        Background = Brushes.Transparent;

        _ime = new TextBox
        {
            Width = 1,
            Height = 1,
            Opacity = 0,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Brushes.Transparent,
            CaretBrush = Brushes.Transparent,
            IsUndoEnabled = false,
            AcceptsReturn = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false
        };
        _ime.PreviewKeyDown += OnImePreviewKeyDown;
        _ime.AddHandler(TextInputEvent, new TextCompositionEventHandler(OnImeTextInput), handledEventsToo: true);
        Children.Add(_ime);

        _findBox = new TextBox { Width = 160, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 2, 4, 2) };
        _findCount = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            MinWidth = 54,
            Foreground = Brushes.White
        };
        _findDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _findDebounce.Tick += (_, _) =>
        {
            _findDebounce.Stop();
            UpdateFind();
        };
        _findBox.TextChanged += (_, _) =>
        {
            _findDebounce.Stop();
            _findDebounce.Start();
        };
        _findBox.PreviewKeyDown += OnFindBoxKeyDown;

        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _cursorTimer.Tick += (_, _) =>
        {
            _cursorOn = !_cursorOn;
            if (_session is not null && IsKeyboardFocusWithin)
                InvalidateVisual();
        };
        _selectionCopyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _selectionCopyTimer.Tick += (_, _) =>
        {
            _selectionCopyTimer.Stop();
            if (_hasSelection && TerminalSettings.CopyOnSelect)
                CopySelection();
        };
        Loaded += (_, _) => _cursorTimer.Start();
        Unloaded += (_, _) =>
        {
            _cursorTimer.Stop();
            _selectionCopyTimer.Stop();
        };

        var prev = new Button { Content = "上一个", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
        prev.Click += (_, _) => FindStep(-1);
        var next = new Button { Content = "下一个", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
        next.Click += (_, _) => FindStep(1);
        var close = new Button { Content = "✕", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
        close.Click += (_, _) => HideFind();

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(_findBox);
        panel.Children.Add(_findCount);
        panel.Children.Add(prev);
        panel.Children.Add(next);
        panel.Children.Add(close);

        _findBar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x2D, 0x2D, 0x30)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 6, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Child = panel
        };
        Panel.SetZIndex(_findBar, 10);
        Children.Add(_findBar);

        TerminalSettings.Changed += OnSettingsChanged;
        Unloaded += (_, _) => TerminalSettings.Changed -= OnSettingsChanged;

        ApplySettings();
    }

    private void OnSettingsChanged()
    {
        if (Dispatcher.CheckAccess())
        {
            ApplySettings();
            InvalidateVisual();
        }
        else
        {
            Dispatcher.BeginInvoke(new Action(() => { ApplySettings(); InvalidateVisual(); }));
        }
    }

    private void ApplySettings()
    {
        _fontSize = TerminalSettings.FontSize;
        var family = new FontFamily(string.IsNullOrWhiteSpace(TerminalSettings.FontFamilyName) ? "Consolas" : TerminalSettings.FontFamilyName);
        _typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _boldTypeface = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        _textCache.Clear();
        _lineTextCache.Clear();
        if (_session is not null)
        {
            _session.Model.AnsiPalette = TerminalSettings.Theme.Ansi16Palette;
            _session.Model.ThemeFg = TerminalSettings.Theme.Fg;
            _session.Model.ThemeBg = TerminalSettings.Theme.Bg;
        }
        RecalcCellSize();
        RecalcGrid();
    }

    /// <summary>聚焦输入（切换标签后自动激活终端输入）。</summary>
    public void FocusInput()
    {
        _ime.Focus();
        Keyboard.Focus(_ime);
    }

    public static readonly DependencyProperty SessionProperty = DependencyProperty.Register(
        nameof(Session), typeof(ITerminalSession), typeof(TerminalView),
        new PropertyMetadata(null, OnSessionChanged));

    public ITerminalSession? Session
    {
        get => (ITerminalSession?)GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    private static void OnSessionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TerminalView view)
            view.AttachSession(e.OldValue as ITerminalSession, e.NewValue as ITerminalSession);
    }

    private void AttachSession(ITerminalSession? oldSession, ITerminalSession? newSession)
    {
        if (oldSession is not null)
        {
            oldSession.Model.Changed -= OnModelChanged;
            oldSession.Model.ResponseRequested -= OnResponseRequested;
        }

        _session = newSession;
        _lineTextCache.Clear();
        _scrollOffset = 0;
        _hasSelection = false;
        if (newSession is not null)
        {
            newSession.Model.Changed += OnModelChanged;
            newSession.Model.ResponseRequested += OnResponseRequested;
            newSession.Model.AnsiPalette = TerminalSettings.Theme.Ansi16Palette;
            newSession.Model.ThemeFg = TerminalSettings.Theme.Fg;
            newSession.Model.ThemeBg = TerminalSettings.Theme.Bg;
            _ = newSession.EnsureStartedAsync(_cols, _rows);
            if (IsLoaded)
                OnViewLoaded(this, new RoutedEventArgs());
            else
            {
                Loaded -= OnViewLoaded;
                Loaded += OnViewLoaded;
            }
        }
        InvalidateVisual();
    }

    private void OnResponseRequested(string response)
    {
        var session = _session;
        if (session is null)
            return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { session.SendInput(response); }
            catch { /* 终端查询响应失败不应中断绘制 */ }
        }), DispatcherPriority.Background);
    }

    private void OnModelChanged()
    {
        _lineTextCache.Clear();
        if (_session?.Model.IsAltScreen == true)
            _scrollOffset = 0;
        if (Dispatcher.CheckAccess())
            InvalidateVisual();
        else
            Dispatcher.BeginInvoke(new Action(InvalidateVisual));
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        _cursorOn = true;
        _cursorTimer.Start();
        RecalcCellSize();
        RecalcGrid();
        _ime.Focus();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _cursorOn = true;
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RecalcGrid();
        InvalidateVisual();
    }

    private void RecalcCellSize()
    {
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var probe = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, _fontSize, Brushes.White, _pixelsPerDip);
        _cellWidth = Math.Max(4, probe.WidthIncludingTrailingWhitespace);
        _cellHeight = Math.Max(8, probe.Height);
        _textCache.Clear();
    }

    private void RecalcGrid()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;
        var cols = Math.Max(2, (int)(ActualWidth / _cellWidth));
        var rows = Math.Max(2, (int)(ActualHeight / _cellHeight));
        if (cols == _cols && rows == _rows)
            return;

        _cols = cols;
        _rows = rows;
        _session?.Resize(cols, rows);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(GetBrush(TerminalSettings.Theme.Bg), null, new Rect(RenderSize));
        if (_session is null)
            return;

        var model = _session.Model;
        _cols = model.Cols;
        _rows = model.Rows;
        if (model.IsAltScreen)
            _scrollOffset = 0;

        var findLines = _findMatches.Count > 0 ? _findMatches : null;
        for (var row = 0; row < _rows; row++)
            DrawLine(dc, model, row, findLines);

        DrawCursor(dc, model);
    }

    private void DrawLine(DrawingContext dc, TerminalModel model, int row, List<(int Line, int Col)>? findMatches)
    {
        var y = row * _cellHeight;
        var baseIndex = model.IsAltScreen ? row : model.ScrollbackCount + row - _scrollOffset;
        var fromScrollback = !model.IsAltScreen && baseIndex < model.ScrollbackCount;
        var findSet = findMatches is not null ? CollectRowMatches(findMatches, baseIndex) : null;
        var currentFind = _findIndex >= 0 && _findIndex < _findMatches.Count ? _findMatches[_findIndex] : default;
        var hasCurrentFind = _findIndex >= 0 && _findIndex < _findMatches.Count;
        var currentLen = Math.Max(1, _findBox.Text.Length);

        var cells = new TerminalCell[_cols];
        if (baseIndex < 0)
        {
            for (var col = 0; col < _cols; col++)
                cells[col] = new TerminalCell { Ch = ' ', Attr = TerminalAttributes.Default };
        }
        else if (fromScrollback)
        {
            var line = model.ScrollbackLine(model.ScrollbackCount - baseIndex);
            Array.Copy(line, cells, Math.Min(line.Length, _cols));
        }
        else
        {
            var screenRow = model.IsAltScreen ? row : baseIndex - model.ScrollbackCount;
            for (var col = 0; col < _cols; col++)
                cells[col] = model.CellAt(col, screenRow);
        }

        // 先画完整行背景，再画文字，避免双宽字符右侧单元格的背景覆盖汉字右半部分。
        for (var col = 0; col < _cols; col++)
        {
            var cell = cells[col];
            var (cellFg, bg, reverse) = ResolveColors(cell.Attr);
            if (reverse)
                bg = cellFg;

            if (IsSelected(row, col))
            {
                bg = TerminalSettings.Theme.SelectionBg;
            }
            else if (findSet is not null && findSet.Contains(col))
            {
                bg = hasCurrentFind && currentFind.Line == baseIndex &&
                    col >= currentFind.Col && col < currentFind.Col + currentLen
                    ? TerminalSettings.Theme.FindCurrentBg
                    : TerminalSettings.Theme.FindBg;
            }

            if (bg != TerminalSettings.Theme.Bg)
                dc.DrawRectangle(GetBrush(bg), null, new Rect(col * _cellWidth, y, _cellWidth, _cellHeight));
        }

        for (var col = 0; col < _cols; col++)
        {
            var cell = cells[col];
            if (cell.WideContinuation || cell.Ch == ' ' || cell.Ch == '\0')
                continue;

            var (fg, cellBg, reverse) = ResolveColors(cell.Attr);
            if (reverse)
                fg = cellBg;
            dc.DrawText(GetText(cell.Ch, fg, cell.Attr), new Point(col * _cellWidth, y));
        }
    }

    private static HashSet<int>? CollectRowMatches(List<(int Line, int Col)> matches, int line)
    {
        HashSet<int>? set = null;
        foreach (var (matchLine, col) in matches)
        {
            if (matchLine != line)
                continue;
            set ??= new HashSet<int>();
            set.Add(col);
        }
        return set;
    }

    private void DrawCursor(DrawingContext dc, TerminalModel model)
    {
        if (_scrollOffset != 0 || !model.CursorVisible || !IsKeyboardFocusWithin || !_cursorOn)
            return;
        if (model.CursorX >= _cols || model.CursorY >= _rows)
            return;

        var x = model.CursorX * _cellWidth;
        var y = model.CursorY * _cellHeight;
        var color = GetBrush(TerminalSettings.Theme.Cursor);
        switch (TerminalSettings.CursorStyle)
        {
            case "bar":
                dc.DrawRectangle(color, null, new Rect(x, y, 2, _cellHeight));
                break;
            case "underline":
                dc.DrawRectangle(color, null, new Rect(x, y + _cellHeight - 2, _cellWidth, 2));
                break;
            default:
                var pen = new Pen(color, 1.0);
                dc.DrawRectangle(null, pen, new Rect(x, y, _cellWidth, _cellHeight));
                break;
        }
    }

    private static (uint Fg, uint Bg, bool Reverse) ResolveColors(TerminalAttributes attr)
    {
        var fg = attr.Fg ?? TerminalSettings.Theme.Fg;
        var bg = attr.Bg ?? TerminalSettings.Theme.Bg;
        if (fg == bg)
            fg = bg > 0x7F7F7Fu ? 0x000000u : 0xFFFFFFu;
        return (fg, bg, attr.Reverse);
    }

    private SolidColorBrush GetBrush(uint rgb)
    {
        if (_brushCache.TryGetValue(rgb, out var brush))
            return brush;
        brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        _brushCache[rgb] = brush;
        return brush;
    }

    private FormattedText GetText(char ch, uint fg, TerminalAttributes attr)
    {
        var key = ch | ((long)fg << 16) | ((attr.Bold ? 1L : 0L) << 48) |
                  ((attr.Italic ? 1L : 0L) << 49) | ((attr.Underline ? 1L : 0L) << 50);
        if (_textCache.TryGetValue(key, out var cached))
            return cached;

        if (_textCache.Count > 4096)
            _textCache.Clear();

        var typeface = attr.Bold ? _boldTypeface : _typeface;
        var text = new FormattedText(ch.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, _fontSize, GetBrush(fg), _pixelsPerDip);
        if (attr.Italic)
            text.SetFontStyle(FontStyles.Italic);
        if (attr.Underline)
            text.SetTextDecorations(TextDecorations.Underline);
        _textCache[key] = text;
        return text;
    }

    // ---- 选区 ----

    private bool IsSelected(int row, int col)
    {
        if (!_hasSelection)
            return false;
        var (r1, c1, r2, c2) = NormalizedSelection();
        if (row < r1 || row > r2)
            return false;
        if (row == r1 && row == r2)
            return col >= c1 && col <= c2;
        if (row == r1)
            return col >= c1;
        if (row == r2)
            return col <= c2;
        return true;
    }

    private (int, int, int, int) NormalizedSelection()
    {
        if (_selStartRow < _selEndRow || (_selStartRow == _selEndRow && _selStartCol <= _selEndCol))
            return (_selStartRow, _selStartCol, _selEndRow, _selEndCol);
        return (_selEndRow, _selEndCol, _selStartRow, _selStartCol);
    }

    private (int Row, int Col) PointToCell(Point p)
    {
        var col = Math.Clamp((int)(p.X / _cellWidth), 0, _cols - 1);
        var row = Math.Clamp((int)(p.Y / _cellHeight), 0, _rows - 1);
        return (row, col);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && _findBar.IsAncestorOf(source))
            return;
        _ime.Focus();
        if (e.ChangedButton == MouseButton.Left)
        {
            var (row, col) = PointToCell(e.GetPosition(this));
            var clickCount = Math.Max(e.ClickCount, RegisterClickCount(row, col));
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && TryOpenLink(row, col))
            {
                e.Handled = true;
                return;
            }
            if (clickCount == 2)
            {
                SelectWord(row, col);
                e.Handled = true;
                return;
            }
            if (clickCount >= 3)
            {
                SelectLine(row);
                e.Handled = true;
                return;
            }

            _selStartRow = _selEndRow = row;
            _selStartCol = _selEndCol = col;
            _selecting = true;
            _hasSelection = false;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
        }
        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_selecting)
        {
            var (row, col) = PointToCell(e.GetPosition(this));
            _selEndRow = row;
            _selEndCol = col;
            _hasSelection = true;
            InvalidateVisual();
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _session is not null)
        {
            var (row, col) = PointToCell(e.GetPosition(this));
            Cursor = TryGetLink(row, col, out _) ? Cursors.Hand : Cursors.IBeam;
        }
        else
        {
            Cursor = Cursors.IBeam;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_selecting && e.ChangedButton == MouseButton.Left)
        {
            _selecting = false;
            ReleaseMouseCapture();
            if (_selStartRow == _selEndRow && _selStartCol == _selEndCol)
                _hasSelection = false;
            else if (TerminalSettings.CopyOnSelect)
                CopySelection();
            InvalidateVisual();
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        Paste();
        e.Handled = true;
    }

    private void ScheduleSelectionCopy()
    {
        if (!TerminalSettings.CopyOnSelect)
            return;
        _selectionCopyTimer.Stop();
        _selectionCopyTimer.Start();
    }

    private int RegisterClickCount(int row, int col)
    {
        var now = Environment.TickCount64;
        var interval = 900;
        if (now - _lastClickTicks <= interval && row == _lastClickRow && col == _lastClickCol)
            _clickCount = Math.Min(3, _clickCount + 1);
        else
            _clickCount = 1;
        _lastClickTicks = now;
        _lastClickRow = row;
        _lastClickCol = col;
        return _clickCount;
    }

    private string GetRowText(int row)
    {
        if (_session is null || row < 0 || row >= _rows)
            return string.Empty;
        var model = _session.Model;
        var abs = model.ScrollbackCount + row - _scrollOffset;
        if (abs < 0)
            return string.Empty;

        var chars = new char[_cols];
        if (abs < model.ScrollbackCount)
        {
            var cells = model.ScrollbackLine(model.ScrollbackCount - abs);
            for (var c = 0; c < chars.Length && c < cells.Length; c++)
                chars[c] = cells[c].WideContinuation ? '\0' : cells[c].Ch;
        }
        else
        {
            for (var c = 0; c < chars.Length; c++)
            {
                var cell = model.CellAt(c, abs - model.ScrollbackCount);
                chars[c] = cell.WideContinuation ? '\0' : cell.Ch;
            }
        }
        return new string(chars).TrimEnd('\0', ' ');
    }

    private static bool IsWordChar(char ch) =>
        ch == '\0' || (!char.IsWhiteSpace(ch) && "\"'`()[]{}<>".IndexOf(ch) < 0);

    private void SelectWord(int row, int col)
    {
        var text = GetRowText(row);
        if (col < 0 || col >= text.Length || !IsWordChar(text[col]))
            return;

        var start = col;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        var end = col;
        while (end < text.Length && IsWordChar(text[end])) end++;

        _selStartRow = _selEndRow = row;
        _selStartCol = start;
        _selEndCol = Math.Max(start, end - 1);
        _selecting = false;
        _hasSelection = true;
        ScheduleSelectionCopy();
        InvalidateVisual();
    }

    private void SelectLine(int row)
    {
        var text = GetRowText(row);
        _selStartRow = _selEndRow = row;
        _selStartCol = 0;
        _selEndCol = Math.Max(0, text.Length - 1);
        _selecting = false;
        _hasSelection = text.Length > 0;
        ScheduleSelectionCopy();
        InvalidateVisual();
    }

    private bool TryOpenLink(int row, int col)
    {
        if (!TryGetLink(row, col, out var url))
            return false;
        try
        {
            NativeShell.OpenFile(url);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryGetLink(int row, int col, out string url)
    {
        url = string.Empty;
        var text = GetRowText(row);
        if (col < 0 || col >= text.Length)
            return false;

        static bool IsLinkChar(char ch) =>
            char.IsLetterOrDigit(ch) || ":/?#[]@!$&'()*+,;=%._~-".IndexOf(ch) >= 0;

        if (!IsLinkChar(text[col]))
            return false;
        var start = col;
        while (start > 0 && IsLinkChar(text[start - 1])) start--;
        var end = col + 1;
        while (end < text.Length && IsLinkChar(text[end])) end++;
        var candidate = text[start..end].TrimEnd('.', ',', ';', ')', ']', '}');
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;
        url = candidate;
        return true;
    }

    private void CopySelection()
    {
        var text = ExtractSelection();
        if (!string.IsNullOrEmpty(text))
        {
            try { Clipboard.SetText(text); } catch { /* ignore */ }
        }
    }

    private string ExtractSelection()
    {
        if (!_hasSelection || _session is null)
            return string.Empty;

        var model = _session.Model;
        var (r1, c1, r2, c2) = NormalizedSelection();
        var sb = new System.Text.StringBuilder();
        for (var row = r1; row <= r2; row++)
        {
            var baseIndex = model.ScrollbackCount + row - _scrollOffset;
            if (baseIndex < 0)
                continue;

            var line = baseIndex < model.ScrollbackCount
                ? model.ScrollbackLine(model.ScrollbackCount - baseIndex)
                : null;

            var start = row == r1 ? c1 : 0;
            var end = row == r2 ? c2 : _cols - 1;
            var rowText = new System.Text.StringBuilder();
            for (var col = start; col <= end && col < _cols; col++)
            {
                var cell = line is not null ? line[col] : model.CellAt(col, baseIndex - model.ScrollbackCount);
                if (cell.WideContinuation)
                    continue;
                rowText.Append(cell.Ch == '\0' ? ' ' : cell.Ch);
            }
            sb.Append(rowText.ToString().TrimEnd());
            if (row != r2)
                sb.Append('\n');
        }
        return sb.ToString();
    }

    // ---- 搜索 ----

    private void ShowFind()
    {
        _findBar.Visibility = Visibility.Visible;
        _findBox.Focus();
        _findBox.SelectAll();
    }

    private void HideFind()
    {
        _findDebounce.Stop();
        _findBar.Visibility = Visibility.Collapsed;
        _findMatches.Clear();
        _findIndex = -1;
        UpdateFindCount();
        InvalidateVisual();
        _ime.Focus();
    }

    private void OnFindBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindStep(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideFind();
            e.Handled = true;
        }
    }

    private void UpdateFind()
    {
        _findMatches.Clear();
        _findIndex = -1;
        var query = _findBox.Text;
        if (_session is null || string.IsNullOrEmpty(query))
        {
            UpdateFindCount();
            InvalidateVisual();
            return;
        }

        var model = _session.Model;
        var total = model.ScrollbackCount + model.Rows;
        for (var line = 0; line < total; line++)
        {
            var text = GetLineText(model, line);
            var offset = 0;
            while (offset <= text.Length - query.Length)
            {
                var col = text.IndexOf(query, offset, StringComparison.Ordinal);
                if (col < 0)
                    break;
                _findMatches.Add((line, col));
                offset = col + Math.Max(1, query.Length);
            }
        }

        if (_findMatches.Count > 0)
            FindStep(1);
        else
        {
            UpdateFindCount();
            InvalidateVisual();
        }
    }

    private string GetLineText(TerminalModel model, int line)
    {
        if (_lineTextCache.TryGetValue(line, out var cached))
            return cached;

        var chars = new char[model.Cols];
        if (line < model.ScrollbackCount)
        {
            var cells = model.ScrollbackLine(model.ScrollbackCount - line);
            for (var c = 0; c < chars.Length; c++)
                chars[c] = cells[c].Ch;
        }
        else
        {
            var screenRow = line - model.ScrollbackCount;
            for (var c = 0; c < chars.Length; c++)
                chars[c] = model.CellAt(c, screenRow).Ch;
        }

        var text = new string(chars);
        _lineTextCache[line] = text;
        return text;
    }

    private void UpdateFindCount()
    {
        _findCount.Text = _findMatches.Count == 0
            ? (_findBox.Text.Length == 0 ? string.Empty : "无结果")
            : $"{_findIndex + 1}/{_findMatches.Count}";
    }

    private void FindStep(int direction)
    {
        if (_findMatches.Count == 0)
            return;
        _findIndex = (_findIndex + direction + _findMatches.Count) % _findMatches.Count;
        var target = _findMatches[_findIndex].Line;
        // 让匹配行进入视图
        var model = _session?.Model;
        if (model is null)
            return;
        UpdateFindCount();
        if (target < model.ScrollbackCount)
        {
            var desiredRow = Math.Max(0, _rows / 2);
            _scrollOffset = Math.Clamp(model.ScrollbackCount - target + (_rows - 1 - desiredRow), 0, model.ScrollbackCount);
        }
        else
        {
            _scrollOffset = 0;
        }
        InvalidateVisual();
    }

    // ---- 输入 ----

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_session is null)
            return;
        if (_session.Model.IsAltScreen)
        {
            e.Handled = true;
            return;
        }
        var max = _session.Model.ScrollbackCount;
        _scrollOffset = Math.Clamp(_scrollOffset + (e.Delta > 0 ? 3 : -3), 0, max);
        InvalidateVisual();
        e.Handled = true;
    }

    private void OnImePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_scrollOffset != 0)
        {
            _scrollOffset = 0;
            InvalidateVisual();
        }

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (ctrl && shift && e.Key == Key.C) { CopySelection(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.V) { Paste(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.K) { ClearScreen(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.F) { ShowFind(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Insert) { CopySelection(); e.Handled = true; return; }
        if (shift && e.Key == Key.Insert) { Paste(); e.Handled = true; return; }

        // 普通空格：英文输入法下 TextInput 常收不到空格。输入宿主中无正在组合的文本 → 视为真实空格直接补发；
        // 有组合文本（中文输入法预编辑）时交给输入法处理（用于确认候选）。
        if (e.Key == Key.Space
            && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0
            && string.IsNullOrEmpty(_ime.Text))
        {
            _session?.SendUserInput(" ");
            e.Handled = true;
            return;
        }

        var seq = MapKey(e);
        if (seq is not null)
        {
            _session?.SendUserInput(seq);
            e.Handled = true;
        }
    }

    private void Paste()
    {
        try
        {
            if (!Clipboard.ContainsText())
                return;
            var text = Clipboard.GetText();
            if (string.IsNullOrEmpty(text))
                return;

            if (text.Contains('\n') || text.Contains('\r'))
            {
                var preview = text.Length <= 600 ? text : text[..600] + "…";
                if (!Dialogs.Confirm("粘贴内容包含换行，可能会一次执行多条命令。是否继续？\n\n" + preview, "确认粘贴"))
                    return;
            }

            if (_session?.Model.BracketedPasteEnabled == true)
                text = "\u001b[200~" + text + "\u001b[201~";
            _session?.SendUserInput(text);
        }
        catch
        {
            // 剪贴板访问失败忽略
        }
    }

    private void ClearScreen()
    {
        _session?.Model.ClearMainBuffer();
        _scrollOffset = 0;
        InvalidateVisual();
        _session?.SendInput("\u000c"); // 同时让远端重绘
    }

    private void OnImeTextInput(object sender, TextCompositionEventArgs e)
    {
        var text = FilterInput(e.Text);
        if (string.IsNullOrEmpty(text))
            return;

        _session?.SendUserInput(text);
        _ime.Text = string.Empty;
    }

    private static string FilterInput(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
            if (c >= ' ' || c == '\t')
                sb.Append(c);
        return sb.ToString();
    }

    private static string? MapKey(KeyEventArgs e)
    {
        var mods = e.KeyboardDevice.Modifiers;
        var ctrl = mods.HasFlag(ModifierKeys.Control);
        var shift = mods.HasFlag(ModifierKeys.Shift);
        var alt = mods.HasFlag(ModifierKeys.Alt);
        var m = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);

        string Csi(char final) => m == 1 ? $"\u001b[{final}" : $"\u001b[1;{m}{final}";
        string Tilde(int code) => m == 1 ? $"\u001b[{code}~" : $"\u001b[{code};{m}~";

        if (ctrl && !alt)
        {
            if (e.Key >= Key.A && e.Key <= Key.Z)
                return ((char)('A' + (e.Key - Key.A) - 'A' + 1)).ToString();
            switch (e.Key)
            {
                case Key.Space: return "\0";
                case Key.OemOpenBrackets: return "\u001b";
                case Key.OemBackslash: return "\u001c";
                case Key.OemCloseBrackets: return "\u001d";
                case Key.OemMinus: return "\u001f";
            }
        }

        switch (e.Key)
        {
            case Key.Enter: return "\r";
            case Key.Back: return "\u007f";
            case Key.Tab: return shift ? "\u001b[Z" : "\t";
            case Key.Escape: return "\u001b";
            case Key.Up: return Csi('A');
            case Key.Down: return Csi('B');
            case Key.Right: return Csi('C');
            case Key.Left: return Csi('D');
            case Key.Home: return Csi('H');
            case Key.End: return Csi('F');
            case Key.Insert: return Tilde(2);
            case Key.Delete: return Tilde(3);
            case Key.PageUp: return Tilde(5);
            case Key.PageDown: return Tilde(6);
            case Key.F1: return "\u001bOP";
            case Key.F2: return "\u001bOQ";
            case Key.F3: return "\u001bOR";
            case Key.F4: return "\u001bOS";
            case Key.F5: return Tilde(15);
            case Key.F6: return Tilde(17);
            case Key.F7: return Tilde(18);
            case Key.F8: return Tilde(19);
            case Key.F9: return Tilde(20);
            case Key.F10: return Tilde(21);
            case Key.F11: return Tilde(23);
            case Key.F12: return Tilde(24);
            default: return null;
        }
    }
}
