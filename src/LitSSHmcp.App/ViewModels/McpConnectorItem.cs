using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LitSSHmcp.App.ViewModels;

/// <summary>会话窗口「连接器」列表里的一条 MCP 服务器（内置 LitSSH MCP 或外部 stdio/http）。</summary>
public sealed class McpConnectorItem : INotifyPropertyChanged
{
    private bool _isEnabled;
    private string _statusText = string.Empty;

    /// <summary>服务器 Id（与配置里的 <c>agent.mcpServers</c> 对应，内置固定 litssh）。</summary>
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>是否内置 LitSSH MCP（只能启停，不能在本列表编辑传输配置）。</summary>
    public bool Builtin { get; init; }

    /// <summary>传输方式（stdio / http）；内置恒为 stdio。</summary>
    public string Transport { get; init; } = "stdio";

    /// <summary>是否启用（勾选即写回配置并重建连接）。</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => Set(ref _isEnabled, value);
    }

    /// <summary>展示名：内置加「内置」标注。</summary>
    public string DisplayName => Builtin ? $"{Name}（内置）" : Name;

    /// <summary>状态文本：已连接 · N 个工具 / 未连接: 原因 / 已停用。</summary>
    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
