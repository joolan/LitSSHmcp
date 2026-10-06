using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LitSSHmcp.App.ViewModels;

/// <summary>命令片段（快捷命令）。</summary>
public sealed class CommandSnippet : INotifyPropertyChanged
{
    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    private string _command = string.Empty;
    public string Command
    {
        get => _command;
        set { _command = value; OnPropertyChanged(); }
    }

    public CommandSnippet() { }

    public CommandSnippet(string name, string command)
    {
        _name = name;
        _command = command;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
