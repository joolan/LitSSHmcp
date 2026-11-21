using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.TerminalAi;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
namespace LitSSHmcp.App.Controls;

/// <summary>终端标签内容：左侧远程文件浏览器 + 右侧终端；分栏宽度记忆、可显示/隐藏。</summary>
public partial class TerminalTabView : UserControl
{
    private const string WidthKey = "ssh.filepanel.width";
    private const string AiWidthKey = "ssh.ai.width";
    private const string FloatingXKey = "ssh.floatingBar.x.";
    private const string FloatingYKey = "ssh.floatingBar.y.";
    private const double DefaultWidth = 300;
    private const double MinPanelWidth = 120;
    private const double DefaultAiWidth = 400;
    private const double MinAiWidth = 300;

    private TerminalSessionViewModel? _hookedVm;
    private MainViewModel? _mainVm;
    private TerminalAiViewModel? _aiVm;

    public TerminalTabView()
    {
        InitializeComponent();
        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
        DataContextChanged += (_, _) => Hook();
        PreviewKeyDown += OnViewPreviewKeyDown;
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        HookWindowViewModel();
        ApplyLayout();
        ApplyFloatingPosition();
        UpdateSyncVisibility();
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        if (_mainVm is not null)
            _mainVm.PropertyChanged -= OnMainVmPropertyChanged;
        _mainVm = null;
        if (_hookedVm is { } vm)
        {
            vm.PropertyChanged -= OnVmPropertyChanged;
            vm.FileBrowser.TerminalCommandSent -= OnTerminalCommandSent;
        }
        _hookedVm = null;
        AttachAiVm(null);
        HideAiPaneLayout();
    }

    private void ShowAiPaneLayout()
    {
        AiSplitter.Visibility = Visibility.Visible;
        AiSplitterCol.Width = GridLength.Auto;
        AiCol.Width = new GridLength(Math.Max(MinAiWidth, UiPrefs.GetDouble(AiWidthKey, DefaultAiWidth)));
        AiPanel.Visibility = Visibility.Visible;
    }

    private void HideAiPaneLayout()
    {
        AiSplitter.Visibility = Visibility.Collapsed;
        AiCol.Width = new GridLength(0);
        AiPanel.Visibility = Visibility.Collapsed;
    }

    private void OnAiSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (AiCol.ActualWidth >= MinAiWidth)
            UiPrefs.SetDouble(AiWidthKey, AiCol.ActualWidth);
    }

    private void HookWindowViewModel()
    {
        var main = Window.GetWindow(this)?.DataContext as MainViewModel;
        if (ReferenceEquals(_mainVm, main))
            return;
        if (_mainVm is not null)
            _mainVm.PropertyChanged -= OnMainVmPropertyChanged;
        _mainVm = main;
        if (_mainVm is not null)
            _mainVm.PropertyChanged += OnMainVmPropertyChanged;
    }

    private void OnMainVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsSshTiled) or nameof(MainViewModel.SshLayout))
            UpdateSyncVisibility();
    }

    /// <summary>命令同步开关只在平铺模式或独立会话窗口显示（本机终端不参与）。</summary>
    private void UpdateSyncVisibility()
    {
        var window = Window.GetWindow(this);
        var show = window is Views.SshSessionFloatWindow
            || (window?.DataContext is MainViewModel main && main.IsSshTiled);
        SyncPanel.Visibility = show && Vm is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private TerminalSessionViewModel? Vm => DataContext as TerminalSessionViewModel;

    /// <summary>聚焦终端输入（切换标签后调用）。</summary>
    public void FocusInput() => Terminal.FocusInput();

    private void Hook()
    {
        var previous = _hookedVm;
        if (previous is { } old)
        {
            old.PropertyChanged -= OnVmPropertyChanged;
            old.FileBrowser.TerminalCommandSent -= OnTerminalCommandSent;
        }

        _hookedVm = Vm;
        if (_hookedVm is { } vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            vm.FileBrowser.TerminalCommandSent += OnTerminalCommandSent;
        }

        if (!ReferenceEquals(previous, _hookedVm))
            SyncAiForCurrentTerminal();
        ApplyLayout();
        ApplyFloatingPosition();
        UpdateSyncVisibility();
    }

    private void SyncAiForCurrentTerminal()
    {
        AttachAiVm(Vm?.AiAssistant is { IsOpen: true } ai ? ai : null);
        if (_aiVm is null)
            HideAiPaneLayout();
        else
            ShowAiPaneLayout();
    }

    private void AttachAiVm(TerminalAiViewModel? vm)
    {
        if (ReferenceEquals(_aiVm, vm))
            return;
        if (_aiVm is not null)
        {
            _aiVm.FillTerminalRequested -= OnAiFillTerminal;
            _aiVm.AttachSelectionRequested -= OnAiAttachSelection;
        }
        _aiVm = vm;
        if (_aiVm is not null)
        {
            _aiVm.FillTerminalRequested += OnAiFillTerminal;
            _aiVm.AttachSelectionRequested += OnAiAttachSelection;
        }
        AiDrawer.DataContext = _aiVm;
    }

    private void OnAiAttachSelection()
    {
        var text = Terminal.GetSelectedText();
        _aiVm?.AppendContextSelection(text);
    }

    private void OnFloatingDrag(object sender, DragDeltaEventArgs e)
    {
        if (FloatingActions.RenderTransform is not System.Windows.Media.TranslateTransform transform)
        {
            transform = new System.Windows.Media.TranslateTransform();
            FloatingActions.RenderTransform = transform;
        }
        transform.X += e.HorizontalChange;
        transform.Y += e.VerticalChange;
    }

    private void OnFloatingDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (Vm is not { } vm ||
            FloatingActions.RenderTransform is not System.Windows.Media.TranslateTransform transform)
            return;

        UiPrefs.SetDouble(FloatingXKey + vm.LayoutKey, transform.X);
        UiPrefs.SetDouble(FloatingYKey + vm.LayoutKey, transform.Y);
    }

    private void ApplyFloatingPosition()
    {
        if (FloatingActions.RenderTransform is not System.Windows.Media.TranslateTransform transform)
        {
            transform = new System.Windows.Media.TranslateTransform();
            FloatingActions.RenderTransform = transform;
        }

        if (Vm is not { } vm)
        {
            transform.X = 0;
            transform.Y = 0;
            return;
        }

        transform.X = UiPrefs.GetDouble(FloatingXKey + vm.LayoutKey, 0);
        transform.Y = UiPrefs.GetDouble(FloatingYKey + vm.LayoutKey, 0);
    }

    private void OnToggleAi(object sender, RoutedEventArgs e)
    {
        if (Vm?.AiAssistant is { IsOpen: true })
            HideAiPane();
        else
            ShowAiPane();
    }

    private void OnViewPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) !=
            (ModifierKeys.Control | ModifierKeys.Shift) ||
            (modifiers & ModifierKeys.Alt) != 0)
            return;

        switch (e.Key)
        {
            case Key.A:
                OnToggleAi(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.N:
                ShowAiPane();
                _aiVm?.NewConversationCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.L:
                ShowAiPane();
                _aiVm?.AttachSelectionCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.I:
                ShowAiPane();
                AiDrawer.FocusInput();
                e.Handled = true;
                break;
        }
    }

    private void ShowAiPane()
    {
        var terminal = Vm;
        if (terminal is null)
            return;
        if (terminal.AiAssistant is null)
        {
            terminal.AiAssistant = new TerminalAiViewModel(
                terminal,
                AppServiceFactory.CreateConfigService(),
                new AuditLogService(),
                (command, sudo, cwd, filter, ct) =>
                    RequestAiApprovalAsync(terminal, command, sudo, cwd, filter, ct));
        }

        AttachAiVm(terminal.AiAssistant);
        ShowAiPaneLayout();
        terminal.AiAssistant.IsOpen = true;
        AiDrawer.FocusInput();
    }

    private void HideAiPane()
    {
        if (Vm?.AiAssistant is { } ai)
            ai.IsOpen = false;
        AttachAiVm(null);
        HideAiPaneLayout();
    }

    private Task<TerminalAiApprovalResult> RequestAiApprovalAsync(
        TerminalSessionViewModel terminal, string command, bool sudo, string workingDirectory,
        CommandFilterResult filter, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<TerminalAiApprovalResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Show()
        {
            try
            {
                var hasPassword = !string.IsNullOrWhiteSpace(terminal.Server.SudoPassword) ||
                                  !string.IsNullOrWhiteSpace(terminal.Server.Password);
                var allowManual = sudo &&
                                  terminal.Server.SudoType != LitSSHmcp.Core.Models.SudoType.None &&
                                  !string.Equals(terminal.Server.Username, "root", StringComparison.Ordinal);
                var window = new Views.TerminalAiCommandApprovalWindow(
                    terminal.Server.Name, command, workingDirectory, sudo, filter, hasPassword, allowManual)
                {
                    Owner = Window.GetWindow(this),
                    Topmost = true
                };
                window.ShowDialog();
                tcs.TrySetResult(new TerminalAiApprovalResult(window.Approved, window.ManualPassword));
            }
            catch
            {
                tcs.TrySetResult(new TerminalAiApprovalResult(false, false));
            }
        }

        Dispatcher.BeginInvoke(new Action(Show));
        return tcs.Task;
    }

    private void OnAiFillTerminal(string command)
    {
        SendBox.Text = command;
        SendBox.Focus();
        Keyboard.Focus(SendBox);
    }

    private void OnTerminalCommandSent()
        => Dispatcher.BeginInvoke(new Action(() => Terminal.FocusInput()), System.Windows.Threading.DispatcherPriority.Input);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalSessionViewModel.ShowFileBrowser))
            ApplyLayout();
        if (e.PropertyName == nameof(TerminalSessionViewModel.IsAiAwaitingManualInput) &&
            _hookedVm?.IsAiAwaitingManualInput == true)
        {
            Dispatcher.BeginInvoke(new Action(() => Terminal.FocusInput()),
                System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void ApplyLayout()
    {
        if (Vm is not { } vm)
            return;
        var saved = UiPrefs.GetDouble(WidthKey, DefaultWidth);
        FileCol.Width = vm.ShowFileBrowser ? new GridLength(Math.Max(MinPanelWidth, saved)) : new GridLength(0);
    }

    private void OnSnippets(object sender, RoutedEventArgs e)
    {
        var vm = Vm;
        var menu = new ContextMenu();

        menu.Items.Add(new MenuItem { Header = "全局片段", IsEnabled = false });
        var global = SnippetStore.LoadGlobal();
        if (global.Count == 0)
            menu.Items.Add(new MenuItem { Header = "（无）", IsEnabled = false });
        foreach (var snippet in global)
            AddSnippetItem(menu, snippet);

        menu.Items.Add(new Separator());
        var serverName = vm?.Server.Name ?? string.Empty;
        menu.Items.Add(new MenuItem { Header = $"服务器片段（{serverName}）", IsEnabled = false });
        var serverSnippets = vm is null ? new List<CommandSnippet>() : SnippetStore.LoadServer(vm.Server.Id);
        if (serverSnippets.Count == 0)
            menu.Items.Add(new MenuItem { Header = "（无）", IsEnabled = false });
        foreach (var snippet in serverSnippets)
            AddSnippetItem(menu, snippet);

        menu.Items.Add(new Separator());
        var manageServer = new MenuItem { Header = "管理服务器片段…", IsEnabled = vm is not null };
        if (vm is not null)
            manageServer.Click += (_, _) => OpenSnippetsWindow(vm.Server.Id, $"服务器命令片段 - {vm.Server.Name}");
        menu.Items.Add(manageServer);

        var manageGlobal = new MenuItem { Header = "管理全局片段…" };
        manageGlobal.Click += (_, _) => OpenSnippetsWindow(null, "全局命令片段");
        menu.Items.Add(manageGlobal);

        menu.PlacementTarget = sender as UIElement;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void AddSnippetItem(ContextMenu menu, CommandSnippet snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet.Command))
            return;
        var command = snippet.Command;
        var item = new MenuItem
        {
            Header = string.IsNullOrWhiteSpace(snippet.Name) ? command : snippet.Name,
            ToolTip = command
        };
        item.Click += (_, _) => Vm?.SendUserInput(command + "\r");
        menu.Items.Add(item);
    }

    private void OpenSnippetsWindow(string? serverId, string title)
        => new Views.ServerSnippetsWindow(serverId, title) { Owner = Window.GetWindow(this) }.ShowDialog();

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        var width = FileCol.ActualWidth;
        if (width >= MinPanelWidth)
            UiPrefs.SetDouble(WidthKey, width);
    }

    private void OnSendInput(object sender, RoutedEventArgs e) => SendBoxText();

    private void OnSendBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            // Shift+Enter=换行（多行命令）；Enter=发送
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0)
                return;
            SendBoxText();
            e.Handled = true;
        }
    }

    private void SendBoxText()
    {
        var text = SendBox.Text;
        if (string.IsNullOrEmpty(text))
            return;
        // 多行内容整段下发：统一换行为 \r，远端 shell 按行顺序执行（与粘贴脚本一致，保留跨行引号等结构）
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace('\n', '\r');
        Vm?.SendUserInput(text + "\r");
        SendBox.Text = string.Empty;
    }

    // ---- AI 分析命令：打开独立的临时聊天并预填（与数据库工作台「AI 分析此 SQL」同一模式） ----
    private Views.TemporaryChatWindow? _aiChat;

    private void OnAiAnalyze(object sender, RoutedEventArgs e)
    {
        var command = SendBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(command))
            return;

        // 只预填命令代码块（分析要求由「运维专家」智能体的系统提示承担），并切到该智能体
        var prompt = "```bash\n" + command + "\n```";

        if (_aiChat is { IsLoaded: true })
        {
            _aiChat.Activate();
            _aiChat.SelectAgent("ops");
            _aiChat.Prefill(prompt);
            return;
        }

        var window = new Views.TemporaryChatWindow(new LitSSHmcp.Core.Services.Storage.ConfigService(), null, initialAgentId: "ops")
        {
            Owner = Window.GetWindow(this),
            Topmost = false
        };
        window.Closed += (_, _) => _aiChat = null;
        _aiChat = window;
        window.Show();
        window.Prefill(prompt);
    }
}
