using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    private void OnRelatedTargetMenuRequested(AgentRelatedTarget target, FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        switch (target.Kind)
        {
            case AgentRelatedTargetKind.Server when target.Server is { } server:
                menu.Items.Add(CreateRelatedMenuItem("插入会话", () => InsertRelatedPrompt(target)));
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateRelatedMenuItem("打开终端", () => ConnectTerminal(server)));
                menu.Items.Add(CreateRelatedMenuItem("SFTP 文件管理", () => OpenSftpManager(server)));
                menu.Items.Add(CreateRelatedMenuItem("资源监控", () => OpenMonitor(server)));
                menu.Items.Add(CreateRelatedMenuItem("Docker 查看/管理", () => OpenDockerManager(server)));
                menu.Items.Add(CreateRelatedMenuItem("系统防火墙", () => OpenFirewallManager(server)));
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateRelatedMenuItem("采集快照", () => RefreshSnapshot(server)));
                menu.Items.Add(CreateRelatedMenuItem("快照历史", () => OpenSnapshotHistory(server)));
                menu.Items.Add(CreateRelatedMenuItem("远程互传", () => OpenRemoteCopy(server)));
                break;

            case AgentRelatedTargetKind.DataSource when target.DataSource is { } datasource:
                var page = DatasourcePage ??= new DatasourceManageViewModel(_configService, services: _services);
                menu.Items.Add(CreateRelatedMenuItem("插入会话", () => InsertRelatedPrompt(target)));
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateRelatedMenuItem("打开数据库工作台", () => page.OpenWorkbenchItemCommand.Execute(datasource)));
                menu.Items.Add(CreateRelatedMenuItem("查看", () => page.ViewItemCommand.Execute(datasource)));
                break;

            case AgentRelatedTargetKind.Document when target.DocumentPath is { } path:
                var folder = Path.GetDirectoryName(path);
                menu.Items.Add(CreateRelatedMenuItem("打开文件", () => NativeShell.OpenFile(path), File.Exists(path)));
                menu.Items.Add(CreateRelatedMenuItem("打开所在文件夹",
                    () => NativeShell.OpenFolder(folder!), !string.IsNullOrWhiteSpace(folder)));
                menu.Items.Add(CreateRelatedMenuItem("复制路径", () => Clipboard.SetText(path)));
                break;
        }

        if (menu.Items.Count == 0)
            return;

        anchor.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void InsertRelatedPrompt(AgentRelatedTarget target)
    {
        var text = target.Kind switch
        {
            AgentRelatedTargetKind.Server when target.Server is { } server =>
                $"SSH服务器「{server.Name}」(serverId={server.Id}, {server.Host}:{server.Port}) ",
            AgentRelatedTargetKind.DataSource when target.DataSource is { } datasource =>
                $"数据源「{datasource.Name}」(datasourceId={datasource.Id}, {datasource.Type} {datasource.Host}:{datasource.Port}) ",
            _ => null
        };

        if (text is not null)
            _agentView?.InsertTextAtCursor(text);
    }

    private static MenuItem CreateRelatedMenuItem(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }
}
