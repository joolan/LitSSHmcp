using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.Storage;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

/// <summary>统一设置窗口：安全设置 / 工具分组 / 配置导入导出（多 Tab）。</summary>
public partial class AppSettingsWindow : FluentWindow
{
    public AppSettingsWindow(IConfigService configService)
    {
        InitializeComponent();
        TransferView.DataContext = new ConfigTransferViewModel(configService);
        WindowLayout.Attach(this, "app-settings");
    }
}
