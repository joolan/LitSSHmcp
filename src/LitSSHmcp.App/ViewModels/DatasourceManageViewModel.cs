using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public class DatasourceManageViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService;
    private readonly ITopologyStore _topologyStore;
    private DataSourceConfig? _selectedDataSource;
    private string _statusMessage = string.Empty;

    public DatasourceManageViewModel(IConfigService configService, ITopologyStore? topologyStore = null)
    {
        _configService = configService;
        _topologyStore = topologyStore ?? new TopologyStore();

        RefreshCommand = new RelayCommand(_ => Load());
        AddCommand = new RelayCommand(_ => Add());
        EditCommand = new RelayCommand(_ => Edit(), _ => SelectedDataSource != null);
        DeleteCommand = new RelayCommand(_ => Delete(), _ => SelectedDataSource != null);
        TestCommand = new RelayCommand(_ => Test(), _ => SelectedDataSource != null);

        Load();
    }

    public ObservableCollection<DataSourceConfig> DataSources { get; } = new();

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

    public ICommand RefreshCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand TestCommand { get; }

    private void RaiseCanExecuteChanged()
    {
        (EditCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (TestCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private async void Load()
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
        }
    }

    private async void Add()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();

            var window = new DatasourceEditWindow { Owner = Application.Current.MainWindow };
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
    }

    private async void Edit()
    {
        if (SelectedDataSource == null) return;

        try
        {
            var config = await _configService.LoadConfigAsync();
            var ds = config.DataSources.FirstOrDefault(d => d.Id == SelectedDataSource.Id) ?? SelectedDataSource;

            var window = new DatasourceEditWindow { Owner = Application.Current.MainWindow };
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
    }

    private async void Delete()
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

            if (MessageBox.Show(message, "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            config.DataSources = config.DataSources.Where(d => d.Id != SelectedDataSource.Id).ToArray();
            config.Relations = config.Relations.Where(r => r.From != nodeId && r.To != nodeId).ToArray();
            await _configService.SaveConfigAsync(config);
            await CleanupDiscoveredEdgesAsync(nodeId);

            StatusMessage = $"已删除数据源: {SelectedDataSource.Name}";
            SelectedDataSource = null;
            Load();
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败: {ex.Message}";
        }
    }

    private async Task CleanupDiscoveredEdgesAsync(string nodeId)
    {
        try { await _topologyStore.InitializeAsync(); await _topologyStore.RemoveEdgesByNodeAsync(nodeId); }
        catch { /* 清理失败不阻断 */ }
    }

    private async void Test()
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

            var registry = AppServiceFactory.CreateDriverRegistry(_configService);
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
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
