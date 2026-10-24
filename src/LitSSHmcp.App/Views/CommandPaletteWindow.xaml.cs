using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class CommandPaletteWindow : FluentWindow
{
    private readonly List<PaletteAction> _all;
    private readonly ObservableCollection<PaletteAction> _filtered = new();

    public CommandPaletteWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _all = BuildActions(viewModel);
        ActionList.ItemsSource = _filtered;
        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            Refresh(string.Empty);
        };
    }

    private static List<PaletteAction> BuildActions(MainViewModel vm) => new()
    {
        new("主页", "页面 · 快捷入口 / 数据看板", SymbolRegular.Home24, () => vm.NavigateHomeCommand.Execute(null)),
        new("SSH 服务器", "页面 · 服务器管理", SymbolRegular.Server24, () => vm.NavigateServersCommand.Execute(null)),
        new("数据源", "页面 · MySQL / PostgreSQL / Redis", SymbolRegular.Database24, () => vm.NavigateDataSourcesCommand.Execute(null)),
        new("应用", "页面 · app: 拓扑节点", SymbolRegular.Apps24, () => vm.NavigateApplicationsCommand.Execute(null)),
        new("远程桌面", "页面 · RDP / VNC", SymbolRegular.Desktop24, () => vm.NavigateRemoteDesktopsCommand.Execute(null)),
        new("SSH 会话管理", "工具 · 打开 / 切换终端", SymbolRegular.Server24, () => vm.OpenSessionManagerCommand.Execute(null)),
        new("资产拓扑", "工具 · 查看资产关系", SymbolRegular.Share24, () => vm.OpenTopologyCommand.Execute(null)),
        new("审计日志", "工具 · 查询操作审计", SymbolRegular.History24, () => vm.OpenAuditCommand.Execute(null)),
        new("AI 运维助手", "AI · 对话与工具调用", SymbolRegular.Chat24, () => vm.OpenAgentCommand.Execute(null)),
        new("端口转发", "工具 · 本地 / 远程 / SOCKS", SymbolRegular.Share24, () => vm.OpenPortForwardCommand.Execute(null)),
        new("文件 / 文件夹同步", "工具 · 同步任务", SymbolRegular.ArrowSync24, () => vm.OpenSyncCommand.Execute(null)),
        new("触发事件", "工具 · Git / Webhook / 备份", SymbolRegular.Flash24, () => vm.OpenTriggerCommand.Execute(null)),
        new("设置", "应用 · 安全 / 工具 / 导入导出", SymbolRegular.Settings24, () => vm.OpenSettingsCommand.Execute(null)),
        new("打开选中服务器终端", "SSH · 先在服务器页选中服务器", SymbolRegular.Play24, () => vm.OpenTerminalCommand.Execute(null)),
        new("批量执行", "SSH · 向多台服务器发送命令", SymbolRegular.Play24, () => vm.OpenBatchExecCommand.Execute(null))
    };

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => Refresh(SearchBox.Text);

    private void Refresh(string? query)
    {
        var keyword = query?.Trim() ?? string.Empty;
        var matches = keyword.Length == 0
            ? _all
            : _all.Where(a => a.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                           || a.Subtitle.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                  .ToList();

        _filtered.Clear();
        foreach (var action in matches)
            _filtered.Add(action);

        ActionList.SelectedIndex = _filtered.Count > 0 ? 0 : -1;
        if (_filtered.Count > 0)
            ActionList.ScrollIntoView(_filtered[0]);
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Enter:
                ExecuteSelected();
                e.Handled = true;
                break;
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_filtered.Count == 0)
            return;

        var index = Math.Clamp(ActionList.SelectedIndex + delta, 0, _filtered.Count - 1);
        ActionList.SelectedIndex = index;
        ActionList.ScrollIntoView(_filtered[index]);
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => ExecuteSelected();

    private void ExecuteSelected()
    {
        if (ActionList.SelectedItem is not PaletteAction action)
            return;

        Close();
        action.Execute();
    }
}

public sealed record PaletteAction(string Title, string Subtitle, SymbolRegular Icon, Action Execute);
