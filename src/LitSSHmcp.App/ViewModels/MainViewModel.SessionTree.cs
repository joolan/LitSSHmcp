using System.Collections.ObjectModel;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    private bool _isSshTreeNavigation = string.Equals(
        UiPrefs.GetString("ssh.navigation", "tabs"), "tree", StringComparison.OrdinalIgnoreCase);

    public bool IsSshTreeNavigation
    {
        get => _isSshTreeNavigation;
        set
        {
            if (_isSshTreeNavigation == value)
                return;
            _isSshTreeNavigation = value;
            if (value)
                SshLayout = SessionLayout.Tabs;
            UiPrefs.SetString("ssh.navigation", value ? "tree" : "tabs");
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSshTabNavigation));
            OnPropertyChanged(nameof(IsSshTabViewVisible));
        }
    }

    public bool IsSshTabNavigation => !IsSshTreeNavigation;
    public bool IsSshTabViewVisible => IsSshTabNavigation && IsSshTabbed;

    public ObservableCollection<SessionTreeNode> SessionTree { get; } = new();

    private void RebuildSessionTree()
    {
        SessionTree.Clear();

        var terminals = DockedSessions.OfType<TerminalSessionViewModel>().ToList();
        var groups = terminals
            .GroupBy(t => string.IsNullOrWhiteSpace(t.Server.Group) ? "未分组" : t.Server.Group)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var groupNode = new SessionTreeNode(group.Key, isGroup: true);
            var servers = group
                .GroupBy(t => t.Server)
                .OrderBy(g => g.Key.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var server in servers)
            {
                var serverNode = new SessionTreeNode(server.Key.Name, isServer: true, server: server.Key);
                foreach (var terminal in server.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase))
                    serverNode.Children.Add(new SessionTreeNode(terminal.Title, terminal));
                groupNode.Children.Add(serverNode);
            }

            if (groupNode.Children.Count > 0)
                SessionTree.Add(groupNode);
        }

        var locals = DockedSessions.OfType<LocalTerminalSessionViewModel>()
            .OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (locals.Count > 0)
        {
            var localNode = new SessionTreeNode("本机终端", isGroup: true);
            foreach (var local in locals)
                localNode.Children.Add(new SessionTreeNode(local.Title, local));
            SessionTree.Add(localNode);
        }
    }

    public void CloseSessionsForServer(SshServerConfig? server)
    {
        if (server is null)
            return;

        var targets = Sessions.OfType<TerminalSessionViewModel>()
            .Where(session => string.Equals(session.Server.Id, server.Id, StringComparison.Ordinal))
            .Cast<object>()
            .ToList();
        if (targets.Count == 0)
            return;

        if (!_dialogs.Confirm(
                $"确定关闭服务器「{server.Name}」下全部 {targets.Count} 个会话？终端连接将断开。",
                "关闭服务器会话", warning: true))
            return;

        int? selectedIndex = SelectedSession is null ? null : Sessions.IndexOf(SelectedSession);
        foreach (var session in targets)
            CloseSessionCore(session);
        EnsureDockedSelection(selectedIndex);
        StatusMessage = $"已关闭服务器「{server.Name}」下的 {targets.Count} 个会话";
    }

    private void CloseSessionCore(object session)
    {
        var index = Sessions.IndexOf(session);
        var wasSelected = ReferenceEquals(SelectedSession, session) || SelectedSession == session;

        if (_floatWindows.TryGetValue(session, out var floatWin))
        {
            _floatWindows.Remove(session);
            floatWin.SuppressDock();
            floatWin.Close();
        }

        if (session is TerminalSessionViewModel term)
            _ = term.DisposeAsync();
        else if (session is LocalTerminalSessionViewModel local)
        {
            AiTerminals.CloseTerminal(local);
            _ = local.DisposeAsync();
        }

        Sessions.Remove(session);
        RebuildDockedSessions();
        EnsureDockedSelection(wasSelected ? index : null);
    }

}
