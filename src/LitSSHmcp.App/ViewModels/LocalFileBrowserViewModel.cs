using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace LitSSHmcp.App.ViewModels;

/// <summary>本地文件系统项。</summary>
public sealed class LocalFsItem
{
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime LastModified { get; init; }

    public string Glyph => IsDirectory ? "📁" : "📄";
    public string SizeText => IsDirectory ? string.Empty : FormatSize(Size);
    public string ModifiedText => LastModified == default ? string.Empty : LastModified.ToString("yyyy-MM-dd HH:mm");

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.#") + " MB";
        return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.#") + " GB";
    }
}

/// <summary>本地文件浏览（SFTP 双栏的本地栏）。</summary>
public sealed class LocalFileBrowserViewModel : INotifyPropertyChanged
{
    public LocalFileBrowserViewModel(string? startPath = null)
    {
        CurrentPath = startPath ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Load();
    }

    public ObservableCollection<LocalFsItem> Items { get; } = new();

    private LocalFsItem? _selectedItem;
    public LocalFsItem? SelectedItem
    {
        get => _selectedItem;
        set { _selectedItem = value; OnPropertyChanged(); }
    }

    private string _currentPath = string.Empty;
    public string CurrentPath
    {
        get => _currentPath;
        set { _currentPath = value; OnPropertyChanged(); }
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public void Load(string? path = null)
    {
        if (!string.IsNullOrWhiteSpace(path))
            CurrentPath = path!;

        Items.Clear();
        try
        {
            var dir = new DirectoryInfo(CurrentPath);
            if (!dir.Exists)
            {
                StatusMessage = "目录不存在";
                return;
            }

            foreach (var sub in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                Items.Add(new LocalFsItem { Name = sub.Name, FullName = sub.FullName, IsDirectory = true, LastModified = sub.LastWriteTime });

            foreach (var file in dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                Items.Add(new LocalFsItem { Name = file.Name, FullName = file.FullName, IsDirectory = false, Size = file.Length, LastModified = file.LastWriteTime });

            StatusMessage = $"{Items.Count} 项";
            // 驱动器切换：根目录时把父级设为盘符列表
        }
        catch (Exception ex)
        {
            StatusMessage = "读取失败: " + ex.Message;
        }
    }

    public void GoUp()
    {
        var parent = Directory.GetParent(CurrentPath);
        if (parent is not null)
            Load(parent.FullName);
    }

    public void NewFolder(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        try
        {
            Directory.CreateDirectory(Path.Combine(CurrentPath, name));
            Load();
        }
        catch (Exception ex)
        {
            StatusMessage = "新建失败: " + ex.Message;
        }
    }

    public void Delete(LocalFsItem item)
    {
        try
        {
            if (item.IsDirectory)
                Directory.Delete(item.FullName, recursive: true);
            else
                File.Delete(item.FullName);
            Load();
        }
        catch (Exception ex)
        {
            StatusMessage = "删除失败: " + ex.Message;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
