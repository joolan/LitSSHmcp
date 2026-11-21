using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LitSSHmcp.App.ViewModels;

public sealed class SessionTreeNode : INotifyPropertyChanged
{
    private bool _isExpanded = true;
    private bool _isSelected;

    public SessionTreeNode(string title, object? session = null, bool isGroup = false, bool isServer = false,
        LitSSHmcp.Core.Models.SshServerConfig? server = null)
    {
        Title = title;
        Session = session;
        IsGroup = isGroup;
        IsServer = isServer;
        Server = server;
    }

    public string Title { get; }
    public object? Session { get; }
    public LitSSHmcp.Core.Models.SshServerConfig? Server { get; }
    public bool IsGroup { get; }
    public bool IsServer { get; }
    public bool IsSession => Session is not null;
    public bool IsIconVisible => !IsSession;
    public string Icon => IsGroup ? "📁" : IsServer ? "🖥" : string.Empty;
    public ObservableCollection<SessionTreeNode> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
