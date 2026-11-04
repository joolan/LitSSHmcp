using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.Storage;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

/// <summary>统一设置窗口：安全设置 / 工具分组 / 配置导入导出（多 Tab）。</summary>
public partial class AppSettingsWindow : FluentWindow
{
    private readonly IConfigService _configService;
    private readonly IAppServices? _services;
    private readonly Dictionary<int, FrameworkElement> _tabContent = new();

    public AppSettingsWindow(IConfigService configService, IAppServices? services = null)
    {
        _configService = configService;
        _services = services;
        InitializeComponent();
        WindowLayout.Attach(this, "app-settings");
        EnsureTabContent(SettingsTabs.SelectedIndex);
    }

    /// <summary>设置页按需创建：打开窗口只加载当前 Tab，避免一次性解析全部设置页造成白屏等待。</summary>
    private void OnSettingsTabChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = SettingsTabs.SelectedIndex;
        if (index < 0)
            return;

        EnsureTabContent(index);
        if (index == 6 && _tabContent.TryGetValue(index, out var view) && view is StorageSettingsView storage)
            storage.EnsureStatisticsLoaded();
    }

    private void EnsureTabContent(int index)
    {
        if (!_tabContent.TryGetValue(index, out var view))
        {
            view = CreateTabContent(index);
            _tabContent[index] = view;
        }

        var host = HostFor(index);
        if (host is not null && !ReferenceEquals(host.Content, view))
            host.Content = view;
    }

    private FrameworkElement CreateTabContent(int index)
    {
        switch (index)
        {
            case 0:
            {
                var view = new AppearanceSettingsView();
                view.Initialize(_services);
                return view;
            }
            case 1:
                return new TerminalSettingsView();
            case 2:
                return new SnippetEditorView();
            case 3:
            {
                var view = new SecuritySettingsView();
                view.Initialize(_services);
                return view;
            }
            case 4:
            {
                var view = new ToolGroupsView();
                view.Initialize(_services);
                return view;
            }
            case 5:
            {
                var view = new ConfigTransferView
                {
                    DataContext = new ConfigTransferViewModel(_configService, _services?.Dialogs)
                };
                return view;
            }
            case 6:
            {
                var view = new StorageSettingsView();
                view.Initialize(_services);
                return view;
            }
            default:
                return new AboutView();
        }
    }

    private ContentControl? HostFor(int index) => index switch
    {
        0 => AppearanceHost,
        1 => TerminalHost,
        2 => SnippetHost,
        3 => SecurityHost,
        4 => ToolGroupsHost,
        5 => TransferHost,
        6 => StorageHost,
        7 => AboutHost,
        _ => null
    };
}
