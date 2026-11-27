using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Controls;

public partial class DatasourceAiDrawer : UserControl
{
    private DatasourceAiViewModel? _viewModel;
    private bool _scrollPending;
    private bool _autoFollow = true;
    private bool _hasUnseenContent;

    public event Action? CloseRequested;

    public DatasourceAiDrawer()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();

        ChatScroll.ScrollChanged += OnChatScrollChanged;
        // 滚轮事件可能来自 MarkdownBox / 消息文本的转发；handledEventsToo 保证能及时识别上翻。
        ChatScroll.AddHandler(MouseWheelEvent, new MouseWheelEventHandler(OnChatScrollMouseWheel),
            handledEventsToo: true);
        // 文档内控件获得焦点时不要带着整个聊天区滚动（避免首次点“复制”页面跳动）。
        // 在 ChatScroll 之前拦下，否则 ScrollViewer 的类处理器已经先滚动过了。
        if (ChatScroll.Content is UIElement chatContent)
            chatContent.AddHandler(RequestBringIntoViewEvent,
                new RequestBringIntoViewEventHandler((_, e) => e.Handled = true));
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        if (e.NewValue is DatasourceAiViewModel vm)
        {
            _viewModel = vm;
            vm.Turns.CollectionChanged += OnTurnsChanged;
            foreach (var turn in vm.Turns)
                turn.PropertyChanged += OnTurnPropertyChanged;

            _autoFollow = true;
            _hasUnseenContent = false;
            ScheduleScroll();
        }
    }

    private void Detach()
    {
        if (_viewModel is not null)
        {
            _viewModel.Turns.CollectionChanged -= OnTurnsChanged;
            foreach (var turn in _viewModel.Turns)
                turn.PropertyChanged -= OnTurnPropertyChanged;
        }
        _viewModel = null;
    }

    private void OnTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<DatasourceAiTurn>())
                item.PropertyChanged -= OnTurnPropertyChanged;
        }
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<DatasourceAiTurn>())
                item.PropertyChanged += OnTurnPropertyChanged;
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _autoFollow = true;
            _hasUnseenContent = false;
            ScheduleScroll();
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Add && !_autoFollow)
        {
            _hasUnseenContent = true;
            UpdateScrollToBottomButton();
            return;
        }

        ScheduleScroll();
    }

    private void OnTurnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var isContentUpdate = e.PropertyName is nameof(DatasourceAiTurn.AssistantText)
            or nameof(DatasourceAiTurn.Note)
            or nameof(DatasourceAiTurn.IsRunning);
        if (!isContentUpdate)
            return;

        if (!_autoFollow)
        {
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
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _scrollPending = false;
            if (_autoFollow)
                ScrollToEnd();
            UpdateScrollToBottomButton();
        }));
    }

    private void ScrollToEnd()
    {
        try { ChatScroll.ScrollToEnd(); } catch { /* ignore */ }
    }

    private void OnChatScrollMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta <= 0)
            return;
        if (ChatScroll.ExtentHeight <= ChatScroll.ViewportHeight + 1)
            return;

        _autoFollow = false;
        UpdateScrollToBottomButton();
    }

    private void OnChatScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var wasFollowing = _autoFollow;
        _autoFollow = ChatScrollPolicy.Resolve(
            _autoFollow,
            e.VerticalChange,
            ChatScroll.VerticalOffset,
            ChatScroll.ViewportHeight,
            ChatScroll.ExtentHeight);

        if (_autoFollow && !wasFollowing)
            _hasUnseenContent = false;

        if (_autoFollow && e.ExtentHeightChange != 0)
            ScrollToEnd();

        UpdateScrollToBottomButton();
    }

    private void UpdateScrollToBottomButton()
    {
        var show = !_autoFollow && ChatScroll.ExtentHeight > ChatScroll.ViewportHeight + 1;
        ScrollToBottomButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ScrollToBottomButton.Content = _hasUnseenContent ? "回到底部 · 有新内容" : "回到底部";
    }

    private void OnScrollToBottom(object sender, RoutedEventArgs e)
    {
        _autoFollow = true;
        _hasUnseenContent = false;
        ScrollToEnd();
        UpdateScrollToBottomButton();
    }

    private void OnClose(object sender, RoutedEventArgs e)
        => CloseRequested?.Invoke();

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            return;
        e.Handled = true;
        if (DataContext is DatasourceAiViewModel vm)
            vm.SendOrStopCommand.Execute(null);
    }

    public void FocusComposer()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            try { InputBox.Focus(); } catch { /* ignore */ }
        }));
    }
}
