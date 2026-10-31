using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

public sealed class BatchProbeServerOption : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public required SshServerConfig Server { get; init; }
    public string Name => Server.Name;
    public string Host => $"{Server.Host}:{Server.Port}";
    public bool Disabled => Server.Disabled;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Disabled) return;
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class BatchProbeSelectionViewModel : INotifyPropertyChanged
{
    private readonly IUserDialogService _dialogs;

    public BatchProbeSelectionViewModel(IEnumerable<SshServerConfig> servers,
        IUserDialogService? dialogs = null)
    {
        _dialogs = dialogs ?? new UserDialogService();
        foreach (var server in servers)
        {
            var option = new BatchProbeServerOption { Server = server };
            option.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(BatchProbeServerOption.IsSelected))
                    RefreshSummary();
            };
            Servers.Add(option);
        }
        SelectAllCommand = new RelayCommand(_ => SetAll(true));
        ClearSelectionCommand = new RelayCommand(_ => SetAll(false));
    }

    public ObservableCollection<BatchProbeServerOption> Servers { get; } = new();
    public ICommand SelectAllCommand { get; }
    public ICommand ClearSelectionCommand { get; }

    public int SelectedCount => Servers.Count(item => item.IsSelected);
    public int TotalCount => Servers.Count;
    public string Summary => $"已选择 {SelectedCount} / {TotalCount} 台服务器";

    public IReadOnlyList<SshServerConfig> SelectedServers =>
        Servers.Where(item => item.IsSelected && !item.Disabled)
            .Select(item => item.Server)
            .ToList();

    public bool EnsureSelection()
    {
        if (SelectedCount > 0)
            return true;
        _dialogs.Info("请至少选择一台服务器。", "批量探测");
        return false;
    }

    private void SetAll(bool value)
    {
        foreach (var item in Servers)
            item.IsSelected = value;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(Summary));
    }

    public void RefreshSummary()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(Summary));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
