using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using LitSSHmcp.App.Services.RemoteFiles;

namespace LitSSHmcp.App.ViewModels;

public sealed class RemoteFileEditTab : INotifyPropertyChanged
{
    public RemoteFileEditTab(string directory)
    {
        Directory = directory;
    }

    public string Directory { get; }

    private bool _isNew;
    public bool IsNew
    {
        get => _isNew;
        set { if (_isNew == value) return; _isNew = value; Raise(); }
    }

    private bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        set { if (_isPinned == value) return; _isPinned = value; Raise(); }
    }

    private string? _remotePath;
    public string? RemotePath
    {
        get => _remotePath;
        set
        {
            if (_remotePath == value) return;
            _remotePath = value;
            Raise();
            Raise(nameof(DisplayName));
            Raise(nameof(Title));
        }
    }

    public string DisplayName
        => string.IsNullOrWhiteSpace(RemotePath)
            ? "未命名"
            : Path.GetFileName(RemotePath);

    private bool _isDirty;
    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            if (_isDirty == value) return;
            _isDirty = value;
            Raise();
            Raise(nameof(Title));
        }
    }

    public string Title => DisplayName + (IsDirty ? " *" : string.Empty);

    private string _content = string.Empty;
    public string Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            _content = value;
            Raise();
        }
    }

    public byte[] OriginalBytes { get; set; } = Array.Empty<byte>();
    public Encoding Encoding { get; set; } = new UTF8Encoding(false);
    public bool HasBom { get; set; }
    public string LineEnding { get; set; } = "LF";
    public RemoteTextFileSnapshot? Snapshot { get; set; }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set { if (_isLoading == value) return; _isLoading = value; Raise(); Raise(nameof(IsBusy)); }
    }

    private bool _isSaving;
    public bool IsSaving
    {
        get => _isSaving;
        set { if (_isSaving == value) return; _isSaving = value; Raise(); Raise(nameof(IsBusy)); }
    }

    public bool IsBusy => IsLoading || IsSaving;

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        set { if (_status == value) return; _status = value; Raise(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
