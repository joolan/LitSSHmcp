using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

/// <summary>SSH 会话管理窗口中的一个标签（交互式终端或本机终端）。</summary>
public interface ISshTab
{
    /// <summary>标签显示名（默认服务器名，可被重命名覆盖）。</summary>
    string Title { get; }

    /// <summary>所属服务器。</summary>
    SshServerConfig Server { get; }

    /// <summary>独立窗口布局记忆键（同一服务器多终端需区分）。</summary>
    string LayoutKey { get; }

    /// <summary>连接状态（终端标签状态点）。</summary>
    bool IsConnected { get; }

    /// <summary>重命名标签（仅本次会话内有效）。</summary>
    void Rename(string title);
}
