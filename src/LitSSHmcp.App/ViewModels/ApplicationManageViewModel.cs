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
        EditItemCommand = new RelayCommand(p => { SelectedApplication = p as ApplicationConfig; Edit(); });
        DeleteItemCommand = new RelayCommand(p => { SelectedApplication = p as ApplicationConfig; Delete(); });

        FilteredApplications = System.Windows.Data.CollectionViewSource.GetDefaultView(Applications);
        FilteredApplications.Filter = FilterApplication;
        GroupedApplications = new System.Windows.Data.CollectionViewSource { Source = Applications }.View;
        GroupedApplications.Filter = FilterApplication;
        GroupedApplications.GroupDescriptions.Add(
            new System.Windows.Data.PropertyGroupDescription(nameof(ApplicationConfig.Group)));

        Load();
    }

    public ObservableCollection<ApplicationConfig> Applications { get; } = new();

    /// <summary>按搜索关键字过滤后的应用视图。</summary>
    public System.ComponentModel.ICollectionView FilteredApplications { get; }

    /// <summary>按「分组」字段分组的应用视图（列表视图用）。</summary>
    public System.ComponentModel.ICollectionView GroupedApplications { get; }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyHint));
            OnPropertyChanged(nameof(ShowEmptyAction));
            FilteredApplications.Refresh();
            GroupedApplications.Refresh();
        }
    }

    private bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    public string EmptyTitle => IsSearching ? "没有匹配的应用" : "还没有应用";

    public string EmptyHint => IsSearching
        ? "尝试调整关键字，或清空搜索框后重试。"
        : "添加应用节点后，AI 与资产拓扑即可识别并管理它。";

    public bool ShowEmptyAction => !IsSearching;

    private bool FilterApplication(object item)
    {
        if (item is not ApplicationConfig app || string.IsNullOrWhiteSpace(SearchText))
            return true;

        var keyword = SearchText.Trim();
        return Contains(app.Name, keyword)
            || Contains(app.Host, keyword)
            || Contains(app.Type, keyword)
            || Contains(app.ContainerName, keyword)
            || Contains(app.Group, keyword)
            || Contains(app.Description, keyword)
            || app.Tags.Any(tag => Contains(tag, keyword));

        static bool Contains(string? value, string keyword)
            => value?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true;
    }

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

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set { _isLoading = value; OnPropertyChanged(); }
    }

    private string _errorMessage = string.Empty;
    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            _errorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => ErrorMessage.Length > 0;

    public ICommand RefreshCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand EditItemCommand { get; }
    public ICommand DeleteItemCommand { get; }

    private bool _isCardView = string.Equals(
        UiPrefs.GetString("application.cardView", "false"), "true", StringComparison.OrdinalIgnoreCase);

    public bool IsCardView
    {
        get => _isCardView;
        set
        {
            if (_isCardView == value) return;
            _isCardView = value;
            UiPrefs.SetString("application.cardView", value ? "true" : "false");
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsListView));
        }
    }

    public bool IsListView => !IsCardView;

    public void ReloadConfig() => Load();

    private void Load()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        AsyncOperation.Run("加载应用", async () =>
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
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsLoading = false;
            }
        });
    }

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
