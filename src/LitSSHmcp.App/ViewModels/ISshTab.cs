using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

/// <summary>SSH 会话管理窗口中的一个标签（命令会话或交互式终端）。</summary>
public interface ISshTab
{
    /// <summary>标签显示名（默认服务器名，可被重命名覆盖）。</summary>
    string Title { get; }

    /// <summary>所属服务器。</summary>
    SshServerConfig Server { get; }

    /// <summary>重命名标签（仅本次会话内有效）。</summary>
    void Rename(string title);
}
