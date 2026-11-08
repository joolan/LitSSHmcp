using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Pet;
using Microsoft.Win32;
using LitSSHmcp.Core.Models;
using Wpf.Ui.Controls;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace LitSSHmcp.App.Views;

public partial class PetWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MinTickInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan MaxTickInterval = TimeSpan.FromMilliseconds(100);
    private static readonly bool PerMonitorDpiAware = DetectPerMonitorDpi();
    private static readonly uint SystemDpi = ReadSystemDpi();

    private readonly PetFrameSet _frames;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _systemTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _bubbleTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private double _frameAccumulator;
    private double _configuredOpacity = 1.0;
    private bool _systemLocked;
    private bool _systemHidden;
    private readonly Random _random = new();
    private PetAction _action = PetAction.Idle;
    private int _frameIndex;
    private int _clickFrames;
    private int _waitTicks;
    private int _idleTicks;
    private int _actionTicks;
    private bool _flipped;
    private Point? _target;
    private bool _movementEnabled;
    private bool _reduceMotion;
    private bool _lowPower;
    private PetAiState _aiState;
    private long? _aiSessionId;
    private int _speed;
    private bool _dragging;
    private bool _dragMoved;
    private Point _dragStartScreen;
    private Point _dragStartWindow;
    private bool _showcaseActive;
    private IReadOnlyList<PetAction> _showcaseActions = Array.Empty<PetAction>();
    private int _showcaseIndex;
    private double _showcaseRemainingMs;

    public PetWindow(PetConfig config, PetFrameSet? frames = null)
    {
        InitializeComponent();
        _frames = frames ?? PetPlaceholder.Create();
        var initialFrames = _frames[PetAction.Idle];
        if (initialFrames.Count > 0)
            PetImage.Source = initialFrames[0];
        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += (_, _) => Tick();
        _systemTimer.Tick += (_, _) => UpdateSystemVisibility();
        _bubbleTimer.Tick += (_, _) =>
        {
            BubblePopup.IsOpen = false;
            _bubbleTimer.Stop();
        };
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        ApplyConfig(config);
        RestorePosition();
        SetAction(PetAction.Idle);
        _waitTicks = NextWaitTicks();
        SourceInitialized += OnSourceInitialized;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _timer.Start();
            else _timer.Stop();
        };
        Loaded += (_, _) =>
        {
            if (!_reduceMotion) _timer.Start();
            _systemTimer.Start();
            UpdateSystemVisibility();
        };
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Drawing.Point point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("shcore.dll")]
    private static extern int GetProcessDpiAwareness(IntPtr process, out int awareness);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    public void SetAiState(PetAiState state, long? sessionId = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetAiState(state, sessionId));
            return;
        }
        _aiSessionId = sessionId;
        _aiState = state is PetAiState.Running or PetAiState.WaitingApproval ? state : PetAiState.Idle;
        var applyAction = !_showcaseActive;
        switch (state)
        {
            case PetAiState.Running:
                if (applyAction)
                    SetAction(PetAction.Work);
                ShowBubble("AI 正在执行…", autoHide: false);
                break;
            case PetAiState.WaitingApproval:
                if (applyAction)
                    SetAction(PetAction.Look);
                ShowBubble("等待审批 · 请处理", autoHide: false, warning: true);
                break;
            case PetAiState.Completed:
                if (applyAction)
                    SetAction(PetAction.Click);
                ShowBubble("AI 任务完成", autoHide: true);
                break;
            case PetAiState.Failed:
                if (applyAction)
                    SetAction(PetAction.Sleep);
                ShowBubble("AI 任务失败", autoHide: true);
                break;
            default:
                BubblePopup.IsOpen = false;
                _bubbleTimer.Stop();
                if (applyAction)
                    SetAction(PetAction.Idle);
                break;
        }
    }

    private void ShowBubble(string text, bool autoHide, bool warning = false)
    {
        BubbleText.Text = text;
        if (TryFindResource(warning ? "AppWarningSubtleBrush" : "AppCardBrush") is Brush background)
            BubbleBorder.Background = background;
        BubblePopup.IsOpen = true;
        _bubbleTimer.Stop();
        if (autoHide)
            _bubbleTimer.Start();
    }

    private void UpdateSystemVisibility()
    {
        var fullscreen = false;
        try
        {
            if (SHQueryUserNotificationState(out var state) == 0)
                fullscreen = state is 2 or 3 or 4;
        }
        catch
        {
            // 旧系统或 API 不可用时忽略
        }

        _systemHidden = _systemLocked || fullscreen;
        _lowPower = IsLowPowerOrRemote();
        UpdateTimerInterval();
        Opacity = _systemHidden ? 0 : _configuredOpacity;
        IsHitTestVisible = !_systemHidden;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        _systemLocked = e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff;
        UpdateSystemVisibility();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (!IsOnAnyScreen(Left, Top))
                ResetPosition();
            else
                ClampToWorkArea();
        });

    private static bool IsLowPowerOrRemote()
    {
        try
        {
            if (Forms.SystemInformation.TerminalServerSession)
                return true;
            var power = Forms.SystemInformation.PowerStatus;
            return power.PowerLineStatus != Forms.PowerLineStatus.Online
                && power.BatteryLifePercent <= 0.2f;
        }
        catch
        {
            return false;
        }
    }

    public void ApplyConfig(PetConfig config)
    {
        var scale = Math.Clamp(config.Scale, 0.5, 2.0);
        Width = PetPlaceholder.Size * scale;
        Height = PetPlaceholder.Size * scale;
        _configuredOpacity = Math.Clamp(config.Opacity, 0.2, 1.0);
        UpdateSystemVisibility();
        Topmost = config.TopMost;
        _reduceMotion = config.ReduceMotion;
        if (_reduceMotion) _timer.Stop();
        else if (IsVisible) _timer.Start();
        _movementEnabled = config.MovementEnabled && !_reduceMotion;
        _speed = Math.Clamp(config.Speed, 10, 240);
        if (!_movementEnabled)
        {
            _target = null;
            SetAction(PetAction.Idle);
        }
        ClampToWorkArea();
    }

    public bool MovementEnabled => _movementEnabled;

    public void ToggleMovement()
    {
        if (_reduceMotion)
            return;
        _movementEnabled = !_movementEnabled;
        _target = null;
        SetAction(PetAction.Idle);
    }

    public void ResetPosition()
    {
        var area = GetWorkArea();
        Left = area.Right - Width - 32;
        Top = area.Bottom - Height - 32;
        ClampToWorkArea();
        UiPrefs.SetDouble("pet.left", Left);
        UiPrefs.SetDouble("pet.top", Top);
        _target = null;
        _waitTicks = NextWaitTicks();
        SetAction(PetAction.Idle);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExToolWindow);
        }
        catch
        {
            // 非关键：仅影响 Alt+Tab 隐藏
        }
    }

    private void Tick()
    {
        if (_systemHidden)
            return;

        if (_showcaseActive)
        {
            TickShowcase();
            return;
        }

        if (_reduceMotion)
            return;

        if (_dragging)
        {
            SetAction(PetAction.Drag);
            AdvanceAnimation();
            return;
        }

        if (_action == PetAction.Click && _clickFrames > 0)
        {
            _clickFrames--;
            AdvanceAnimation();
            if (_clickFrames == 0)
                SetAction(PetAction.Idle);
            return;
        }

        if (_aiState == PetAiState.Running)
        {
            SetAction(PetAction.Work);
            AdvanceAnimation();
            return;
        }

        if (_aiState == PetAiState.WaitingApproval)
        {
            SetAction(PetAction.Look);
            AdvanceAnimation();
            return;
        }

        if (_action is PetAction.Sleep or PetAction.Stretch or PetAction.Look)
        {
            _actionTicks++;
            AdvanceAnimation();
            var maxTicks = _action == PetAction.Sleep ? 40 : 8;
            if (_actionTicks >= maxTicks)
            {
                SetAction(PetAction.Idle);
                _idleTicks = 0;
                _waitTicks = NextWaitTicks();
            }
            return;
        }

        if (!_movementEnabled)
        {
            SetAction(PetAction.Idle);
            _idleTicks++;
            if (_idleTicks > 300)
            {
                SetAction(PetAction.Sleep);
                _actionTicks = 0;
            }
            AdvanceAnimation();
            return;
        }

        if (_waitTicks > 0)
        {
            _waitTicks--;
            _idleTicks++;
            SetAction(PetAction.Idle);
            if (_idleTicks > 300)
            {
                SetAction(PetAction.Sleep);
                _actionTicks = 0;
            }
            AdvanceAnimation();
            return;
        }

        if (_target is null)
        {
            var roll = _random.Next(100);
            if (roll < 12)
            {
                SetAction(PetAction.Stretch);
                _actionTicks = 0;
                return;
            }
            if (roll < 24)
            {
                SetAction(PetAction.Look);
                _actionTicks = 0;
                return;
            }
            _idleTicks = 0;
            _target = PickTarget();
            SetAction(PetAction.Walk);
        }

        var target = _target.Value;
        var dx = target.X - Left;
        var dy = target.Y - Top;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        SetFacing(dx);
        var step = Math.Max(1.0, _speed * _timer.Interval.TotalSeconds);
        if (distance <= step)
        {
            Left = target.X;
            Top = target.Y;
            ClampToWorkArea();
            _target = null;
            _waitTicks = NextWaitTicks();
            _idleTicks = 0;
            SetAction(PetAction.Idle);
        }
        else
        {
            Left += dx / distance * step;
            Top += dy / distance * step;
        }
        AdvanceAnimation();
    }

    private void SetFacing(double dx)
    {
        if (Math.Abs(dx) < 0.5)
            return;
        var baseFacesLeft = _frames.Facing == PetFacing.Left;
        var flip = (dx < 0) != baseFacesLeft;
        if (flip == _flipped)
            return;
        _flipped = flip;
        PetImage.RenderTransformOrigin = new Point(0.5, 0.5);
        PetImage.RenderTransform = new ScaleTransform(flip ? -1 : 1, 1);
    }

    private void AdvanceAnimation()
    {
        _frameAccumulator += _timer.Interval.TotalMilliseconds;
        var delay = _frames.GetDelay(_action, _frameIndex);
        if (_lowPower)
            delay *= 2;
        if (_frameAccumulator < delay)
            return;
        _frameAccumulator %= delay;
        AdvanceFrame();
    }

    private void AdvanceFrame()
    {
        var frames = _frames[_action];
        if (frames.Count == 0)
            return;
        _frameIndex = (_frameIndex + 1) % frames.Count;
        PetImage.Source = frames[_frameIndex];
    }

    private void OnShowcaseAll(object sender, RoutedEventArgs e) =>
        StartShowcase(Enum.GetValues<PetAction>());

    private void OnShowcaseAction(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string value } &&
            Enum.TryParse<PetAction>(value, out var action))
            StartShowcase(new[] { action });
    }

    private void OnStopShowcase(object sender, RoutedEventArgs e) => StopShowcase();

    private void StartShowcase(IReadOnlyList<PetAction> actions)
    {
        if (actions.Count == 0)
            return;
        _showcaseActive = true;
        _showcaseActions = actions;
        _showcaseIndex = 0;
        _target = null;
        if (!_timer.IsEnabled && IsVisible)
            _timer.Start();
        ShowShowcaseStep();
    }

    private void ShowShowcaseStep()
    {
        SetAction(_showcaseActions[_showcaseIndex]);
        _showcaseRemainingMs = _random.Next(1000, 2001);
    }

    private void TickShowcase()
    {
        _showcaseRemainingMs -= _timer.Interval.TotalMilliseconds;
        if (_showcaseRemainingMs > 0)
        {
            AdvanceAnimation();
            return;
        }
        _showcaseIndex++;
        if (_showcaseIndex >= _showcaseActions.Count)
        {
            StopShowcase();
            return;
        }
        ShowShowcaseStep();
        AdvanceAnimation();
    }

    private void StopShowcase()
    {
        if (!_showcaseActive)
            return;
        _showcaseActive = false;
        _showcaseRemainingMs = 0;
        _target = null;
        _waitTicks = NextWaitTicks();
        SetAction(PetAction.Idle);
        if (_reduceMotion)
            _timer.Stop();
    }

    private void SetAction(PetAction action)
    {
        if (_action == action)
            return;
        _action = action;
        _frameIndex = 0;
        _frameAccumulator = 0;
        var frames = _frames[action];
        if (frames.Count > 0)
            PetImage.Source = frames[0];
        UpdateTimerInterval();
    }

    private void UpdateTimerInterval()
    {
        if (_timer is null)
            return;
        var frames = _frames[_action];
        var minDelay = 100;
        for (var i = 0; i < frames.Count; i++)
            minDelay = Math.Min(minDelay, _frames.GetDelay(_action, i));
        if (_lowPower)
            minDelay *= 2;
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(minDelay,
            MinTickInterval.TotalMilliseconds, MaxTickInterval.TotalMilliseconds));
        if (Math.Abs(_timer.Interval.TotalMilliseconds - interval.TotalMilliseconds) > 0.5)
            _timer.Interval = interval;
    }

    private int NextWaitTicks() => _random.Next(20, 65);

    private Point PickTarget()
    {
        var area = GetWorkArea();
        var maxX = Math.Max(area.Left, area.Right - Width);
        var maxY = Math.Max(area.Top, area.Bottom - Height);
        if (_random.Next(100) < 40)
        {
            return _random.Next(3) switch
            {
                0 => new Point(area.Left, area.Top + _random.NextDouble() * Math.Max(1, maxY - area.Top)),
                1 => new Point(maxX, area.Top + _random.NextDouble() * Math.Max(1, maxY - area.Top)),
                _ => new Point(area.Left + _random.NextDouble() * Math.Max(1, maxX - area.Left), maxY)
            };
        }
        return new Point(
            area.Left + _random.NextDouble() * Math.Max(1, maxX - area.Left),
            area.Top + _random.NextDouble() * Math.Max(1, maxY - area.Top));
    }

    private void RestorePosition()
    {
        var area = GetWorkArea();
        var left = UiPrefs.GetDouble("pet.left", double.NaN);
        var top = UiPrefs.GetDouble("pet.top", double.NaN);
        if (double.IsNaN(left) || double.IsNaN(top) || !IsOnAnyScreen(left, top))
        {
            Left = area.Right - Width - 32;
            Top = area.Bottom - Height - 32;
        }
        else
        {
            Left = left;
            Top = top;
        }
        ClampToWorkArea();
    }

    private void ClampToWorkArea()
    {
        var area = GetWorkAreaAt(Left, Top);
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }

    private bool IsOnAnyScreen(double left, double top)
    {
        try
        {
            foreach (var screen in Forms.Screen.AllScreens)
            {
                var area = ToDip(screen.WorkingArea, GetDpiForScreen(screen));
                var visible = Rect.Intersect(area, new Rect(left, top, Width, Height));
                if (!visible.IsEmpty && visible.Width >= 32 && visible.Height >= 32)
                    return true;
            }
        }
        catch
        {
            // 屏幕信息不可用时按主屏处理
        }
        return false;
    }

    private Rect GetWorkAreaAt(double left, double top)
    {
        try
        {
            foreach (var screen in Forms.Screen.AllScreens)
            {
                var area = ToDip(screen.WorkingArea, GetDpiForScreen(screen));
                if (area.Contains(left, top))
                    return area;
            }
        }
        catch
        {
            // 屏幕信息不可用时回退到当前窗口所在屏幕
        }
        return GetWorkArea();
    }

    private static Rect ToDip(Drawing.Rectangle work, uint dpi)
    {
        var scale = 96.0 / Math.Max(96u, dpi);
        return new Rect(work.Left * scale, work.Top * scale, work.Width * scale, work.Height * scale);
    }

    private static bool DetectPerMonitorDpi()
    {
        try
        {
            return GetProcessDpiAwareness(GetCurrentProcess(), out var awareness) == 0 && awareness >= 2;
        }
        catch
        {
            return false;
        }
    }

    private static uint ReadSystemDpi()
    {
        try
        {
            var dpi = GetDpiForSystem();
            if (dpi > 0)
                return dpi;
        }
        catch
        {
            // 旧系统回退 96 DPI
        }
        return 96;
    }

    private static uint GetDpiForScreen(Forms.Screen screen)
    {
        if (!PerMonitorDpiAware)
            return SystemDpi;
        try
        {
            var point = new Drawing.Point(screen.Bounds.Left + 1, screen.Bounds.Top + 1);
            var monitor = MonitorFromPoint(point, 2);
            if (monitor != IntPtr.Zero &&
                GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0)
                return dpi;
        }
        catch
        {
            // 旧系统回退 96 DPI
        }
        return 96;
    }

    private uint GetWindowDpi(IntPtr hwnd)
    {
        if (!PerMonitorDpiAware)
            return SystemDpi;
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            if (dpi > 0)
                return dpi;
        }
        catch
        {
            // 回退到 WPF DPI
        }
        var wpfDpi = VisualTreeHelper.GetDpi(this);
        return (uint)Math.Round(96.0 * wpfDpi.DpiScaleX);
    }

    private Rect GetWorkArea()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var screen = hwnd != IntPtr.Zero
                ? Forms.Screen.FromHandle(hwnd)
                : Forms.Screen.PrimaryScreen;
            if (screen is null)
                return SystemParameters.WorkArea;
            var dpi = hwnd != IntPtr.Zero
                ? GetWindowDpi(hwnd)
                : GetDpiForScreen(screen);
            return ToDip(screen.WorkingArea, dpi);
        }
        catch
        {
            return SystemParameters.WorkArea;
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;
        _dragging = true;
        _dragMoved = false;
        _dragStartScreen = PointToScreen(e.GetPosition(this));
        _dragStartWindow = new Point(Left, Top);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
            return;
        var current = PointToScreen(e.GetPosition(this));
        var dx = current.X - _dragStartScreen.X;
        var dy = current.Y - _dragStartScreen.Y;
        if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < 4)
            return;
        _dragMoved = true;
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = _dragStartWindow.X + dx / dpi.DpiScaleX;
        Top = _dragStartWindow.Y + dy / dpi.DpiScaleY;
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_dragging)
            return;
        _dragging = false;
        ReleaseMouseCapture();
        if (_dragMoved)
        {
            ClampToWorkArea();
            UiPrefs.SetDouble("pet.left", Left);
            UiPrefs.SetDouble("pet.top", Top);
            _target = null;
            _waitTicks = NextWaitTicks();
            SetAction(PetAction.Idle);
        }
        else
        {
            SetAction(PetAction.Click);
            _clickFrames = 4;
            OpenAiAssistant(_aiState == PetAiState.WaitingApproval ? _aiSessionId : null);
        }
        e.Handled = true;
    }

    private void OnBubbleClick(object sender, MouseButtonEventArgs e)
    {
        if (_aiState != PetAiState.WaitingApproval)
            return;
        OpenAiAssistant(_aiSessionId);
        e.Handled = true;
    }

    private void OpenAiAssistant(long? sessionId = null)
    {
        try
        {
            if (Application.Current.Windows.OfType<AppLockWindow>().FirstOrDefault() is { } lockWindow)
            {
                lockWindow.Activate();
                return;
            }
            if (Application.Current.MainWindow is MainWindow main)
                main.OpenAiAssistantFromPet(sessionId);
        }
        catch
        {
            // 主窗口尚未创建时忽略
        }
    }

    private void OnOpenAi(object sender, RoutedEventArgs e) => OpenAiAssistant();

    private void OnHidePet(object sender, RoutedEventArgs e)
    {
        UiPrefs.SetDouble("pet.left", Left);
        UiPrefs.SetDouble("pet.top", Top);
        Hide();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();
        _systemTimer.Stop();
        _bubbleTimer.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        UiPrefs.SetDouble("pet.left", Left);
        UiPrefs.SetDouble("pet.top", Top);
    }
}
