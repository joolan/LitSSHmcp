using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public class ApplicationManageViewModel : INotifyPropertyChanged, IConfigReloadable
{
    private readonly IConfigService _configService;
    private readonly Window? _owner;
    private readonly ITopologyStore _topologyStore;
    private readonly IUserDialogService _dialogs;
    private readonly IWindowService _windows;
    private ApplicationConfig? _selectedApplication;
    private string _statusMessage = string.Empty;

    public ApplicationManageViewModel(IConfigService configService, Window? owner, ITopologyStore? topologyStore = null,
        IUserDialogService? dialogs = null, IWindowService? windows = null)
    {
        _configService = configService;
        _owner = owner;
        _dialogs = dialogs ?? new UserDialogService();
        _windows = windows ?? new WpfWindowService();
        _topologyStore = topologyStore ?? new TopologyStore();

        RefreshCommand = new RelayCommand(_ => Load());
        AddCommand = new RelayCommand(_ => Add());
        EditCommand = new RelayCommand(_ => Edit(), _ => SelectedApplication != null);
        DeleteCommand = new RelayCommand(_ => Delete(), _ => SelectedApplication != null);

        Load();
    }

    public ObservableCollection<ApplicationConfig> Applications { get; } = new();

    public ApplicationConfig? SelectedApplication
    {
        get => _selectedApplication;
        set
        {
            _selectedApplication = value;
            OnPropertyChanged();
            (EditCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public ICommand RefreshCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }

    public void ReloadConfig() => Load();

    private void Load() => AsyncOperation.Run("加载应用", async () =>
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            Applications.Clear();
            foreach (var app in config.Applications)
                Applications.Add(app);
            StatusMessage = $"已加载 {Applications.Count} 个应用";
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败: {ex.Message}";
        }
    });

    private void Add()
    {
        var window = _windows.CreateApplicationEditWindow(_owner);
        var vm = new ApplicationEditViewModel(_configService);
        window.DataContext = vm;
        vm.DialogClosed += (_, ok) => { window.Close(); if (ok) Load(); };
        window.ShowDialog();
    }

    private void Edit()
    {
        if (SelectedApplication == null) return;

        var window = _windows.CreateApplicationEditWindow(_owner);
        var vm = new ApplicationEditViewModel(_configService, SelectedApplication);
        window.DataContext = vm;
        vm.DialogClosed += (_, ok) => { window.Close(); if (ok) Load(); };
        window.ShowDialog();
    }

    private void Delete() => AsyncOperation.Run("删除应用", async () =>
    {
        if (SelectedApplication == null) return;

        try
        {
            var config = await _configService.LoadConfigAsync();
            var nodeId = AssetNode.App(SelectedApplication.Id);
            var references = RelationHelper.Referencing(config, nodeId);

            var message = $"确定删除应用 \"{SelectedApplication.Name}\" 吗？";
            message += references.Length > 0
                ? $"\n\n存在 {references.Length} 条关系记录引用它，将一并删除：\n{RelationHelper.Describe(references)}"
                : "\n\n（未发现引用它的关系记录）";

            if (!_dialogs.Confirm(message, "确认删除"))
                return;

            config.Applications = config.Applications.Where(a => a.Id != SelectedApplication.Id).ToArray();
            config.Relations = config.Relations.Where(r => r.From != nodeId && r.To != nodeId).ToArray();
            await _configService.SaveConfigAsync(config);
            await CleanupDiscoveredEdgesAsync(nodeId);

            StatusMessage = $"已删除应用: {SelectedApplication.Name}";
            SelectedApplication = null;
            Load();
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败: {ex.Message}";
        }
    });

    private async Task CleanupDiscoveredEdgesAsync(string nodeId)
    {
        try { await _topologyStore.InitializeAsync(); await _topologyStore.RemoveEdgesByNodeAsync(nodeId); }
        catch { /* 清理失败不阻断 */ }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
