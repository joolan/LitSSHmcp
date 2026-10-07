using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    private Typeface _typeface = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private Typeface _boldTypeface = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private double _fontSize = 14;
    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private double _pixelsPerDip = 1;
    private int _cols = 80;
    private int _rows = 24;
    private int _scrollOffset;
    private TerminalSessionViewModel? _session;

    // 选区（视图坐标：行/列）
    private bool _selecting;
    private bool _hasSelection;
    private int _selStartRow, _selStartCol, _selEndRow, _selEndCol;

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
        _findBox.TextChanged += (_, _) => UpdateFind();
        _findBox.PreviewKeyDown += OnFindBoxKeyDown;

        var prev = new Button { Content = "上一个", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
        prev.Click += (_, _) => FindStep(-1);
        var next = new Button { Content = "下一个", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
        next.Click += (_, _) => FindStep(1);
        var close = new Button { Content = "✕", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
        close.Click += (_, _) => HideFind();

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(_findBox);
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
        nameof(Session), typeof(TerminalSessionViewModel), typeof(TerminalView),
        new PropertyMetadata(null, OnSessionChanged));

    public TerminalSessionViewModel? Session
    {
        get => (TerminalSessionViewModel?)GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    private static void OnSessionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TerminalView view)
            view.AttachSession(e.OldValue as TerminalSessionViewModel, e.NewValue as TerminalSessionViewModel);
    }

    private void AttachSession(TerminalSessionViewModel? oldSession, TerminalSessionViewModel? newSession)
    {
        if (oldSession is not null)
            oldSession.Model.Changed -= OnModelChanged;

        _session = newSession;
        _scrollOffset = 0;
        _hasSelection = false;
        if (newSession is not null)
        {
            newSession.Model.Changed += OnModelChanged;
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

    private void OnModelChanged()
    {
        if (Dispatcher.CheckAccess())
            InvalidateVisual();
        else
            Dispatcher.BeginInvoke(new Action(InvalidateVisual));
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        RecalcCellSize();
        RecalcGrid();
        _ime.Focus();
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

        var findLines = _findMatches.Count > 0 ? _findMatches : null;
        for (var row = 0; row < _rows; row++)
            DrawLine(dc, model, row, findLines);

        DrawCursor(dc, model);
    }

    private void DrawLine(DrawingContext dc, TerminalModel model, int row, List<(int Line, int Col)>? findMatches)
    {
        var y = row * _cellHeight;
        var baseIndex = model.ScrollbackCount + row - _scrollOffset;
        var fromScrollback = baseIndex < model.ScrollbackCount;
        var findSet = findMatches is not null ? CollectRowMatches(findMatches, baseIndex) : null;

        for (var col = 0; col < _cols; col++)
        {
            TerminalCell cell;
            if (baseIndex < 0)
                cell = new TerminalCell { Ch = ' ', Attr = TerminalAttributes.Default };
            else if (fromScrollback)
                cell = model.ScrollbackLine(model.ScrollbackCount - baseIndex)[col];
            else
                cell = model.CellAt(col, baseIndex - model.ScrollbackCount);

            var x = col * _cellWidth;
            var (fg, bg, reverse) = ResolveColors(cell.Attr);
            if (reverse)
                (fg, bg) = (bg, fg);

            if (IsSelected(row, col))
            {
                bg = 0x264F78;
            }
            else if (findSet is not null && findSet.Contains(col))
            {
                bg = 0x6B5D00;
            }

            if (bg != TerminalSettings.Theme.Bg)
                dc.DrawRectangle(GetBrush(bg), null, new Rect(x, y, _cellWidth, _cellHeight));

            if (cell.Ch != ' ' && cell.Ch != '\0')
                dc.DrawText(GetText(cell.Ch, fg, cell.Attr), new Point(x, y));
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
        if (_scrollOffset != 0 || !model.CursorVisible)
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
        var key = ch | ((long)fg << 16) | ((attr.Bold ? 1L : 0L) << 48);
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

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        _ime.Focus();
        if (e.ChangedButton == MouseButton.Left)
        {
            var (row, col) = PointToCell(e.GetPosition(this));
            _selStartRow = _selEndRow = row;
            _selStartCol = _selEndCol = col;
            _selecting = true;
            _hasSelection = false;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
        }
        base.OnMouseDown(e);
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
                var ch = line is not null ? line[col].Ch : model.CellAt(col, baseIndex - model.ScrollbackCount).Ch;
                rowText.Append(ch == '\0' ? ' ' : ch);
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
        _findBar.Visibility = Visibility.Collapsed;
        _findMatches.Clear();
        _findIndex = -1;
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
            InvalidateVisual();
            return;
        }

        var model = _session.Model;
        var total = model.ScrollbackCount + model.Rows;
        for (var line = 0; line < total; line++)
        {
            var cells = FullLine(model, line);
            for (var col = 0; col + query.Length <= cells.Length; col++)
            {
                var match = true;
                for (var k = 0; k < query.Length; k++)
                {
                    if (cells[col + k] != query[k]) { match = false; break; }
                }
                if (match)
                    _findMatches.Add((line, col));
            }
        }

        if (_findMatches.Count > 0)
            FindStep(1);
        else
            InvalidateVisual();
    }

    private char[] FullLine(TerminalModel model, int line)
    {
        if (line < model.ScrollbackCount)
        {
            var cells = model.ScrollbackLine(model.ScrollbackCount - line);
            return cells.Select(c => c.Ch).ToArray();
        }
        var screenRow = line - model.ScrollbackCount;
        var result = new char[model.Cols];
        for (var c = 0; c < model.Cols; c++)
            result[c] = model.CellAt(c, screenRow).Ch;
        return result;
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
            _session?.SendInput(" ");
            e.Handled = true;
            return;
        }

        var seq = MapKey(e);
        if (seq is not null)
        {
            _session?.SendInput(seq);
            e.Handled = true;
        }
    }

    private void Paste()
    {
        try
        {
            if (Clipboard.ContainsText())
                _session?.SendInput(Clipboard.GetText());
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

        _session?.SendInput(text);
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
