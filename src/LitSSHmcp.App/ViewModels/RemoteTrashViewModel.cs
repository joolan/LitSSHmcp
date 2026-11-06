using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.ViewModels;

public sealed class RemoteTrashViewModel : INotifyPropertyChanged
{
    private readonly RemoteTrashService _trash;
    private bool _busy;
    private string _statusMessage = string.Empty;
    private string _summaryText = "回收站为空";
    private RemoteTrashItem? _selectedItem;

    public RemoteTrashViewModel(RemoteTrashService trash)
    {
        _trash = trash;
    }

    public ObservableCollection<RemoteTrashItem> Items { get; } = new();

    public RemoteTrashItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (ReferenceEquals(_selectedItem, value))
                return;
            _selectedItem = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => _selectedItem is not null;
    public bool HasItems => Items.Count > 0;

    public bool Busy
    {
        get => _busy;
        private set { if (_busy == value) return; _busy = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set { if (_statusMessage == value) return; _statusMessage = value; OnPropertyChanged(); }
    }

    public string SummaryText
    {
        get => _summaryText;
        private set { if (_summaryText == value) return; _summaryText = value; OnPropertyChanged(); }
    }

    public async Task RefreshAsync()
    {
        if (Busy)
            return;
        Busy = true;
        StatusMessage = "正在读取远程回收站…";
        try
        {
            var items = await _trash.ListAsync();
            Items.Clear();
            foreach (var item in items)
                Items.Add(item);
            SelectedItem = Items.FirstOrDefault();
            var total = items.Sum(item => item.Size);
            var oldest = items.Count == 0 ? null : items.MinBy(item => item.DeletedAt);
            SummaryText = items.Count == 0
                ? "回收站为空"
                : $"{items.Count} 项 · {RemoteTrashItem.FormatSize(total)} · 最早 {oldest?.DeletedAtText}";
            StatusMessage = $"已读取 {items.Count} 项；回收站不会自动清理。清空前不会释放远程空间。";
            OnPropertyChanged(nameof(HasItems));
        }
        catch (Exception ex)
        {
            Items.Clear();
            SelectedItem = null;
            SummaryText = "回收站为空";
            StatusMessage = ex.Message;
            OnPropertyChanged(nameof(HasItems));
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task<RemoteTrashRestoreResult> RestoreAsync()
    {
        if (_selectedItem is null)
            return new RemoteTrashRestoreResult(false, "请选择要恢复的条目。");
        Busy = true;
        try
        {
            var result = await _trash.RestoreAsync(_selectedItem);
            StatusMessage = result.Message;
            return result;
        }
        catch (Exception ex)
        {
            var result = new RemoteTrashRestoreResult(false, ex.Message);
            StatusMessage = ex.Message;
            return result;
        }
        finally
        {
            Busy = false;
            await RefreshAsync();
        }
    }

    public async Task DeletePermanentlyAsync()
    {
        if (_selectedItem is null)
            return;
        Busy = true;
        try
        {
            await _trash.DeletePermanentlyAsync(_selectedItem);
            StatusMessage = "已彻底删除所选条目。";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            Busy = false;
            await RefreshAsync();
        }
    }

    public async Task EmptyAsync()
    {
        Busy = true;
        try
        {
            await _trash.EmptyAsync();
            StatusMessage = "回收站已清空，远程空间已释放。";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            Busy = false;
            await RefreshAsync();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
