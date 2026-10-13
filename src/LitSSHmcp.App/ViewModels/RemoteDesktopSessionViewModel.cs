using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

/// <summary>一个内嵌远程桌面标签页（RDP / VNC）的会话状态。</summary>
public sealed class RemoteDesktopSessionViewModel : INotifyPropertyChanged
{
    public RemoteDesktopSessionViewModel(RemoteDesktopConfig config, SshServerConfig? tunnelServer)
    {
        Config = config;
        TunnelServer = tunnelServer;
        _status = config.ProtocolText == "VNC" ? "准备连接…" : "准备连接…";
    }

    public RemoteDesktopConfig Config { get; }

    /// <summary>承载隧道的 SSH 服务器（为空 = 直连）。</summary>
    public SshServerConfig? TunnelServer { get; }

    public string Title => Config.DisplayName;

    public string ProtocolText => Config.ProtocolText;

    private string _status;
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>内嵌远程桌面窗口（多标签）的视图模型。</summary>
public sealed class RemoteDesktopSessionWindowViewModel : INotifyPropertyChanged
{
    /// <summary>全部会话（含已分离为独立窗口的）。</summary>
    public ObservableCollection<RemoteDesktopSessionViewModel> Sessions { get; } = new();

    /// <summary>主窗口标签栏显示的会话（= Sessions 去掉已分离的）。</summary>
    public ObservableCollection<RemoteDesktopSessionViewModel> DockedSessions { get; } = new();

    private RemoteDesktopSessionViewModel? _selectedSession;
    public RemoteDesktopSessionViewModel? SelectedSession
    {
        get => _selectedSession;
        set { _selectedSession = value; OnPropertyChanged(); }
    }

    public bool HasSessions => Sessions.Count > 0;

    public bool HasDockedSessions => DockedSessions.Count > 0;

    public void RaiseCounts()
    {
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(HasDockedSessions));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
