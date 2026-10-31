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

public class DatasourceManageViewModel : INotifyPropertyChanged, IConfigReloadable
{
    private readonly IConfigService _configService;
    private readonly IUserDialogService _dialogs;
    private readonly IWindowService _windows;
    private readonly IAppServices? _services;
    private DataSourceConfig? _selectedDataSource;
    private string _statusMessage = string.Empty;

    public DatasourceManageViewModel(IConfigService configService,
        IUserDialogService? dialogs = null, IWindowService? windows = null, IAppServices? services = null)
    {
        _configService = configService;
        _dialogs = dialogs ?? new UserDialogService();
        _windows = windows ?? new WpfWindowService();
        _services = services;

        RefreshCommand = new RelayCommand(_ => Load());
        AddCommand = new RelayCommand(_ => Add());
        EditCommand = new RelayCommand(_ => Edit(), _ => SelectedDataSource != null);
        DeleteCommand = new RelayCommand(_ => Delete(), _ => SelectedDataSource != null);
        TestCommand = new RelayCommand(_ => Test(), _ => SelectedDataSource != null);
        WorkbenchCommand = new RelayCommand(_ => OpenWorkbench(SelectedDataSource));
        OpenWorkbenchItemCommand = new RelayCommand(p => OpenWorkbench(p as DataSourceConfig));
        EditItemCommand = new RelayCommand(p => { SelectedDataSource = p as DataSourceConfig; Edit(); });
        TestItemCommand = new RelayCommand(p => { SelectedDataSource = p as DataSourceConfig; Test(); });
        DeleteItemCommand = new RelayCommand(p => { SelectedDataSource = p as DataSourceConfig; Delete(); });

        FilteredDataSources = System.Windows.Data.CollectionViewSource.GetDefaultView(DataSources);
        FilteredDataSources.Filter = FilterDataSource;
        GroupedDataSources = new System.Windows.Data.CollectionViewSource { Source = DataSources }.View;
        GroupedDataSources.Filter = FilterDataSource;
        GroupedDataSources.GroupDescriptions.Add(
            new System.Windows.Data.PropertyGroupDescription(nameof(DataSourceConfig.Group)));

        Load();
    }

    public ObservableCollection<DataSourceConfig> DataSources { get; } = new();

    /// <summary>按搜索关键字过滤后的数据源视图。</summary>
    public System.ComponentModel.ICollectionView FilteredDataSources { get; }

    /// <summary>按「分组」字段分组的数据源视图（列表视图用）。</summary>
    public System.ComponentModel.ICollectionView GroupedDataSources { get; }

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
            OnPropertyChanged(nameof(ShowClearSearch));
            FilteredDataSources.Refresh();
            GroupedDataSources.Refresh();
        }
    }

    private bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    public string EmptyTitle => IsSearching ? "没有匹配的数据源" : "还没有数据源";

    public string EmptyHint => IsSearching
        ? "尝试调整关键字，或清空搜索框后重试。"
        : "添加 MySQL / PostgreSQL / Redis 数据源后即可打开数据库工作台。";

    public bool ShowEmptyAction => !IsSearching;

    public bool ShowClearSearch => IsSearching;

    private bool FilterDataSource(object item)
    {
        if (item is not DataSourceConfig ds || string.IsNullOrWhiteSpace(SearchText))
            return true;

        var keyword = SearchText.Trim();
        return Contains(ds.Name, keyword)
            || Contains(ds.Host, keyword)
            || Contains(ds.Username, keyword)
            || Contains(ds.DefaultDatabase, keyword)
            || Contains(ds.Type, keyword)
            || Contains(ds.Group, keyword)
            || Contains(ds.Description, keyword)
            || ds.Tags.Any(tag => Contains(tag, keyword));

        static bool Contains(string? value, string keyword)
            => value?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true;
    }

    public DataSourceConfig? SelectedDataSource
    {
        get => _selectedDataSource;
        set { _selectedDataSource = value; OnPropertyChanged(); RaiseCanExecuteChanged(); }
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
    public ICommand TestCommand { get; }
    public ICommand WorkbenchCommand { get; }
    public ICommand OpenWorkbenchItemCommand { get; }
    public ICommand EditItemCommand { get; }
    public ICommand TestItemCommand { get; }
    public ICommand DeleteItemCommand { get; }

    private bool _isCardView = string.Equals(
        UiPrefs.GetString("datasource.cardView", "false"), "true", StringComparison.OrdinalIgnoreCase);

    public bool IsCardView
    {
        get => _isCardView;
        set
        {
            if (_isCardView == value) return;
            _isCardView = value;
            UiPrefs.SetString("datasource.cardView", value ? "true" : "false");
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsListView));
        }
    }

    public bool IsListView => !IsCardView;

    private readonly List<DbWorkbenchWindow> _workbenches = new();

    private void OpenWorkbench(DataSourceConfig? target)
    {
        // 同一数据源已打开则激活该窗口；否则新开一个工作台窗口（支持多窗口/多数据源并行）。
        if (target is not null)
        {
            var existing = _workbenches.FirstOrDefault(w => w.IsLoaded && w.CurrentDatasourceId == target.Id);
            if (existing is not null)
            {
                existing.Activate();
                return;
            }
        }

        var window = _windows.CreateDbWorkbenchWindow(target, Application.Current.MainWindow);
        _workbenches.Add(window);
        window.Closed += (_, _) => _workbenches.Remove(window);
        window.Show();
    }

    private void RaiseCanExecuteChanged()
    {
        (EditCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (TestCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public void ReloadConfig() => Load();

    private void Load()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        AsyncOperation.Run("加载数据源", async () =>
        {
            try
            {
                var config = await _configService.LoadConfigAsync();

                DataSources.Clear();
                foreach (var ds in config.DataSources)
                    DataSources.Add(ds);

                StatusMessage = $"已加载 {DataSources.Count} 个数据源";
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

    private void Add() => AsyncOperation.Run("打开新增数据源", async () =>
    {
        try
        {
            var config = await _configService.LoadConfigAsync();

            var window = _windows.CreateDatasourceEditWindow(Application.Current.MainWindow);
            var vm = new DatasourceEditViewModel(_configService, window, config.Servers);
            window.DataContext = vm;

            vm.DialogClosed += (_, result) =>
            {
                window.Close();
                if (result) Load();
            };

            window.ShowDialog();
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开添加窗口失败: {ex.Message}";
        }
    });

    private void Edit() => AsyncOperation.Run("打开编辑数据源", async () =>
    {
        if (SelectedDataSource == null) return;

        try
        {
            var config = await _configService.LoadConfigAsync();
            var ds = config.DataSources.FirstOrDefault(d => d.Id == SelectedDataSource.Id) ?? SelectedDataSource;

            var window = _windows.CreateDatasourceEditWindow(Application.Current.MainWindow);
            var vm = new DatasourceEditViewModel(_configService, window, config.Servers, ds);
            window.DataContext = vm;

            vm.DialogClosed += (_, result) =>
            {
                window.Close();
                if (result) Load();
            };

            window.ShowDialog();
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开编辑窗口失败: {ex.Message}";
        }
    });

    private void Delete() => AsyncOperation.Run("删除数据源", async () =>
    {
        if (SelectedDataSource == null) return;

        try
        {
            var config = await _configService.LoadConfigAsync();
            var nodeId = AssetNode.Ds(SelectedDataSource.Id);
            var references = RelationHelper.Referencing(config, nodeId);

            var message = $"确定删除数据源 \"{SelectedDataSource.Name}\" 吗？";
            message += references.Length > 0
                ? $"\n\n存在 {references.Length} 条关系记录引用它，将一并删除：\n{RelationHelper.Describe(references)}"
                : "\n\n（未发现引用它的关系记录）";

            if (!_dialogs.Confirm(message, "确认删除"))
                return;

            config.DataSources = config.DataSources.Where(d => d.Id != SelectedDataSource.Id).ToArray();
            config.Relations = config.Relations.Where(r => r.From != nodeId && r.To != nodeId).ToArray();
            await _configService.SaveConfigAsync(config);

            StatusMessage = $"已删除数据源: {SelectedDataSource.Name}";
            SelectedDataSource = null;
            Load();
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败: {ex.Message}";
        }
    });

    private void Test() => AsyncOperation.Run("测试数据源连接", async () =>
    {
        if (SelectedDataSource == null) return;
        StatusMessage = "测试连接中...";

        try
        {
            var config = await _configService.LoadConfigAsync();
            var ds = config.DataSources.FirstOrDefault(d => d.Id == SelectedDataSource.Id);
            if (ds == null)
            {
                StatusMessage = "数据源不存在, 请刷新";
                return;
            }

            var registry = _services?.CreateDriverRegistry() ?? AppServiceFactory.CreateDriverRegistry(_configService);
            var driver = registry.Get(ds.Type);
            if (driver == null)
            {
                StatusMessage = $"暂不支持的类型: {ds.Type}";
                return;
            }

            var result = await driver.TestAsync(ds);
            StatusMessage = result.Success
                ? $"连接成功 ({result.DurationMs:F0}ms) {result.Version}" +
                  (result.ViaTunnelServer != null ? $" 经隧道 {result.ViaTunnelServer}" : "")
                : $"连接失败: {result.Error}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"连接失败: {ex.Message}";
        }
    });

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
