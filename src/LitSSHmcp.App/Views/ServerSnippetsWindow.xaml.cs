using LitSSHmcp.Core.Models;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

/// <summary>命令片段管理窗口：编辑全局或某服务器的专属片段。</summary>
public partial class ServerSnippetsWindow : FluentWindow
{
    public ServerSnippetsWindow(string? serverId, string title)
    {
        InitializeComponent();
        Title = title;
        Editor.Configure(serverId, title);
    }

    public ServerSnippetsWindow(SshServerConfig server)
        : this(server.Id, $"服务器命令片段 - {server.Name}")
    {
    }
}
