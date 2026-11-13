using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Views;

public partial class AppearanceSettingsView : UserControl
{
    private AppearanceSettingsViewModel? _viewModel;

    public AppearanceSettingsView()
    {
        InitializeComponent();
        // 独立使用（未调用 Initialize）时，等进入可视树再兜底创建 ViewModel；
        // 设置窗口会在挂载前调用 Initialize，避免重复加载配置/重复应用主题。
        Loaded += OnLoaded;
    }

    public void Initialize(IAppServices? services)
    {
        _viewModel = new AppearanceSettingsViewModel(services?.Config);
        DataContext = _viewModel;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is null)
        {
            _viewModel ??= new AppearanceSettingsViewModel();
            DataContext = _viewModel;
        }
    }

    private AppearanceSettingsViewModel ViewModel => _viewModel ??= new AppearanceSettingsViewModel();

    private void OnReload(object sender, RoutedEventArgs e) => ViewModel.Load();

    private void OnSave(object sender, RoutedEventArgs e) => ViewModel.Save();
}
