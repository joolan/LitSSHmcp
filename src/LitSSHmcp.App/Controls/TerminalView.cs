using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Controls;

/// <summary>
/// 自绘终端控件：把 <see cref="TerminalModel"/> 的单元格画到界面，处理键盘/IME 输入、鼠标滚动回看与尺寸变化。
/// 内含一个近乎不可见的 <see cref="TextBox"/> 作为输入宿主（用于中文输入法 IME 组合输入）。
/// </summary>
public sealed class TerminalView : Grid
{
    private const uint DefaultFg = 0xD4D4D4;
    private const uint DefaultBg = 0x1E1E1E;

    private readonly Typeface _typeface = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private readonly Typeface _boldTypeface = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private readonly Dictionary<uint, SolidColorBrush> _brushCache = new();
    private readonly Dictionary<long, FormattedText> _textCache = new();
    private readonly TextBox _ime;

    private double _fontSize = 14;
    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private double _pixelsPerDip = 1;
    private int _cols = 80;
    private int _rows = 24;
    private int _scrollOffset;
    private TerminalSessionViewModel? _session;

    public TerminalView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
        Background = Brushes.Transparent;

        // 输入宿主：近乎不可见的 TextBox，用于中文输入法(IME)组合输入；键盘事件在其上处理。
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
        _ime.TextChanged += OnImeTextChanged;
        Children.Add(_ime);
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
        if (newSession is not null)
        {
            newSession.Model.Changed += OnModelChanged;
            // 立即用当前已知尺寸启动，避免依赖布局/Loaded 时序导致终端永不启动
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
        dc.DrawRectangle(GetBrush(DefaultBg), null, new Rect(RenderSize));
        if (_session is null)
            return;

        var model = _session.Model;
        _cols = model.Cols;
        _rows = model.Rows;

        for (var row = 0; row < _rows; row++)
            DrawLine(dc, model, row);

        DrawCursor(dc, model);
    }

    private void DrawLine(DrawingContext dc, TerminalModel model, int row)
    {
        var y = row * _cellHeight;
        var baseIndex = model.ScrollbackCount + row - _scrollOffset;
        var fromScrollback = baseIndex < model.ScrollbackCount;

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

            if (bg != DefaultBg)
                dc.DrawRectangle(GetBrush(bg), null, new Rect(x, y, _cellWidth, _cellHeight));

            if (cell.Ch != ' ' && cell.Ch != '\0')
                dc.DrawText(GetText(cell.Ch, fg, cell.Attr), new Point(x, y));
        }
    }

    private void DrawCursor(DrawingContext dc, TerminalModel model)
    {
        if (_scrollOffset != 0 || !model.CursorVisible)
            return;
        if (model.CursorX >= _cols || model.CursorY >= _rows)
            return;

        var rect = new Rect(model.CursorX * _cellWidth, model.CursorY * _cellHeight, _cellWidth, _cellHeight);
        var pen = new Pen(GetBrush(DefaultFg), 1.0);
        dc.DrawRectangle(null, pen, rect);
    }

    private static (uint Fg, uint Bg, bool Reverse) ResolveColors(TerminalAttributes attr)
    {
        var fg = attr.Fg ?? DefaultFg;
        var bg = attr.Bg ?? DefaultBg;
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

    // ---- 输入 ----

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        _ime.Focus();
        base.OnMouseDown(e);
    }

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

        var seq = MapKey(e);
        if (seq is not null)
        {
            _session?.SendInput(seq);
            e.Handled = true;
        }
    }

    private bool _clearingIme;

    private void OnImeTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_clearingIme)
            return;

        var text = _ime.Text;
        if (string.IsNullOrEmpty(text))
            return;

        _clearingIme = true;
        _ime.Text = string.Empty;
        _clearingIme = false;

        var filtered = FilterInput(text);
        if (!string.IsNullOrEmpty(filtered))
            _session?.SendInput(filtered);
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

        // Ctrl + 字母 / 符号 → 控制字符
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
