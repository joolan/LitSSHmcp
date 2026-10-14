using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
namespace LitSSHmcp.App.Controls;

/// <summary>终端标签内容：左侧远程文件浏览器 + 右侧终端；分栏宽度记忆、可显示/隐藏。</summary>
public partial class TerminalTabView : UserControl
{
    private const string WidthKey = "ssh.filepanel.width";
    private const double DefaultWidth = 300;
    private const double MinPanelWidth = 120;

    public TerminalTabView()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyLayout();
        DataContextChanged += (_, _) => Hook();
    }

    private TerminalSessionViewModel? Vm => DataContext as TerminalSessionViewModel;

    /// <summary>聚焦终端输入（切换标签后调用）。</summary>
    public void FocusInput() => Terminal.FocusInput();

    private void Hook()
    {
        if (Vm is { } vm)
        {
            vm.PropertyChanged -= OnVmPropertyChanged;
            vm.PropertyChanged += OnVmPropertyChanged;
        }
        ApplyLayout();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalSessionViewModel.ShowFileBrowser))
            ApplyLayout();
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
        item.Click += (_, _) => Vm?.SendInput(command + "\r");
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
        Vm?.SendInput(text + "\r");
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
