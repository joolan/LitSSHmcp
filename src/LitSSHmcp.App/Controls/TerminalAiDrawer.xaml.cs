using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Controls;

public partial class TerminalAiDrawer : UserControl
{
    /// <summary>
    /// 按终端 AI 会话保存滚动位置。TabControl 切换标签时会重建 TerminalTabView / 本控件，
    /// 只靠控件自身字段会把所有会话都带回顶部；这里以 ViewModel 为键保留各自的位置与跟随状态。
    /// </summary>
    private static readonly ConditionalWeakTable<TerminalAiViewModel, SessionScrollState> SessionScrollStates = new();

    private sealed class SessionScrollState
    {
        public double Offset { get; set; }
        public bool Following { get; set; } = true;
    }

    private TerminalAiViewModel? _viewModel;
    private readonly HashSet<TerminalAiTurn> _hookedTurns = new();
    private bool _scrollPending;
    private bool _autoFollow = true;
    private bool _hasUnseenContent;
    private int _unseenTurns;
    private bool _restoringScroll;
    private int _restoreVersion;

    public TerminalAiDrawer()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook();
        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => Detach();

        ChatScroll.ScrollChanged += OnChatScrollChanged;
        // 滚轮事件可能来自 MarkdownBox / 用户消息的转发；handledEventsToo 保证能及时识别上翻。
        ChatScroll.AddHandler(MouseWheelEvent, new MouseWheelEventHandler(OnChatScrollMouseWheel),
            handledEventsToo: true);
        // 聊天记录里的控件（代码块“复制”按钮、可选代码框）获得焦点时，WPF 会请求把它滚进视口，
        // 首次点击可能让整个聊天区跳一下。聊天区只应由用户滚动或“回到底部”改变位置。
        // 必须在 ChatScroll 之前拦下，否则 ScrollViewer 的类处理器已经先滚动过了。
        if (ChatScroll.Content is UIElement chatContent)
            chatContent.AddHandler(RequestBringIntoViewEvent,
                new RequestBringIntoViewEventHandler((_, e) => e.Handled = true));
    }

    private void Hook()
    {
        Detach();
        if (DataContext is not TerminalAiViewModel vm)
            return;

        _viewModel = vm;
        vm.Turns.CollectionChanged += OnTurnsChanged;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        foreach (var turn in vm.Turns)
            HookTurn(turn);

        // 恢复该会话上次的滚动位置：新会话默认回到底部；翻过历史的会话恢复原位置。
        var state = SessionScrollStates.GetOrCreateValue(vm);
        _autoFollow = state.Following;
        _unseenTurns = 0;
        _hasUnseenContent = false;
        RestoreScrollState(vm, state);
    }

    private void RestoreScrollState(TerminalAiViewModel vm, SessionScrollState state)
    {
        _restoringScroll = true;
        var version = ++_restoreVersion;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // 等 ItemsControl 生成容器、完成一次布局后再恢复，否则 ScrollToEnd 会被 0 高度吞掉。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (version != _restoreVersion)
                    return;

                _restoringScroll = false;
                if (!ReferenceEquals(_viewModel, vm))
                    return;

                if (state.Following)
                    ChatScroll.ScrollToEnd();
                else
                    ChatScroll.ScrollToVerticalOffset(state.Offset);
                UpdateScrollToBottomButton();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void SaveScrollState()
    {
        if (_viewModel is null)
            return;

        var state = SessionScrollStates.GetOrCreateValue(_viewModel);
        state.Offset = ChatScroll.VerticalOffset;
        state.Following = _autoFollow;
    }

    private void Detach()
    {
        // 不用在这里保存：滚动过程中已实时写入 SessionScrollStates，
        // 而 DataContextChanged/Unloaded 时内容可能已经切到新会话，现读 VerticalOffset 会存成 0。
        if (_viewModel is not null)
        {
            _viewModel.Turns.CollectionChanged -= OnTurnsChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        foreach (var turn in _hookedTurns)
            turn.PropertyChanged -= OnTurnPropertyChanged;
        _hookedTurns.Clear();
        _viewModel = null;
        _restoringScroll = false;
        _restoreVersion++;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 通过点击发送按钮开始新一轮对话时，也恢复跟随（键盘 Enter 路径已在 OnInputKeyDown 处理）。
        if (e.PropertyName != nameof(TerminalAiViewModel.IsBusy))
            return;
        if (_viewModel is not { IsBusy: true })
            return;

        _autoFollow = true;
        _unseenTurns = 0;
        _hasUnseenContent = false;
        SaveScrollState();
        ScheduleScroll();
        UpdateScrollToBottomButton();
    }

    private void OnTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (TerminalAiTurn turn in e.OldItems)
                UnhookTurn(turn);
        if (e.NewItems is not null)
            foreach (TerminalAiTurn turn in e.NewItems)
                HookTurn(turn);

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _autoFollow = true;
            _unseenTurns = 0;
            _hasUnseenContent = false;
            ScheduleScroll();
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Add && !_autoFollow && e.NewItems is { Count: > 0 })
        {
            _unseenTurns += e.NewItems.Count;
            _hasUnseenContent = true;
            UpdateScrollToBottomButton();
            return;
        }

        ScheduleScroll();
    }

    private void HookTurn(TerminalAiTurn turn)
    {
        if (_hookedTurns.Add(turn))
            turn.PropertyChanged += OnTurnPropertyChanged;
    }

    private void UnhookTurn(TerminalAiTurn turn)
    {
        if (_hookedTurns.Remove(turn))
            turn.PropertyChanged -= OnTurnPropertyChanged;
    }

    private void OnTurnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var isContentUpdate = e.PropertyName is nameof(TerminalAiTurn.AssistantText)
            or nameof(TerminalAiTurn.Note)
            or nameof(TerminalAiTurn.IsRunning)
            or nameof(TerminalAiTurn.IsStepsExpanded)
            or nameof(TerminalAiTurn.StepsHeader)
            or nameof(TerminalAiTurn.StepsVisibility);
        if (!isContentUpdate)
            return;

        if (!_autoFollow)
        {
            // 用户正在翻历史：内容照常更新，但不再把视图拉回底部。
            if (!_hasUnseenContent)
            {
                _hasUnseenContent = true;
                UpdateScrollToBottomButton();
            }
            return;
        }

        ScheduleScroll();
    }

    private void ScheduleScroll()
    {
        if (_scrollPending)
            return;
        _scrollPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _scrollPending = false;
            if (_autoFollow)
                ChatScroll.ScrollToEnd();
            UpdateScrollToBottomButton();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnChatScrollMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta <= 0)
            return;
        if (ChatScroll.ExtentHeight <= ChatScroll.ViewportHeight + 1)
            return;

        _autoFollow = false;
        SaveScrollState();
        UpdateScrollToBottomButton();
    }

    private void OnChatScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_restoringScroll)
        {
            UpdateScrollToBottomButton();
            return;
        }

        var wasFollowing = _autoFollow;
        _autoFollow = ChatScrollPolicy.Resolve(
            _autoFollow,
            e.VerticalChange,
            ChatScroll.VerticalOffset,
            ChatScroll.ViewportHeight,
            ChatScroll.ExtentHeight);

        if (_autoFollow && !wasFollowing)
        {
            _unseenTurns = 0;
            _hasUnseenContent = false;
        }

        if (_autoFollow && e.ExtentHeightChange != 0)
            ChatScroll.ScrollToEnd();

        SaveScrollState();
        UpdateScrollToBottomButton();
    }

    private void UpdateScrollToBottomButton()
    {
        var show = !_autoFollow && ChatScroll.ExtentHeight > ChatScroll.ViewportHeight + 1;
        ScrollToBottomButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ScrollToBottomButton.Content = _unseenTurns > 0
            ? $"回到底部 · {_unseenTurns} 条新消息"
            : _hasUnseenContent ? "回到底部 · 有新内容" : "回到底部";
    }

    private void OnScrollToBottom(object sender, RoutedEventArgs e)
    {
        _autoFollow = true;
        _unseenTurns = 0;
        _hasUnseenContent = false;
        ChatScroll.ScrollToEnd();
        SaveScrollState();
        UpdateScrollToBottomButton();
    }

    public void FocusInput()
    {
        InputBox.Focus();
        Keyboard.Focus(InputBox);
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            return;

        if (_viewModel is { IsBusy: false } && _viewModel.SendOrStopCommand.CanExecute(null))
        {
            _autoFollow = true;
            _unseenTurns = 0;
            _hasUnseenContent = false;
            SaveScrollState();
            _viewModel.SendOrStopCommand.Execute(null);
            e.Handled = true;
        }
    }
}
