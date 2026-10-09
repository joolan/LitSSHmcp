using System.Windows;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class PortForwardWindow : FluentWindow
{
    private readonly PortForwardViewModel _vm;

    public PortForwardWindow(PortForwardViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        WindowLayout.Attach(this, "port-forward");
        Closed += (_, _) =>
        {
            // 取消服务事件订阅（服务是进程级单例，隧道继续后台运行）
            _vm.Dispose();
            // 关窗兜底保存（脏检查 + 同步等待写盘，定义不丢）
            _vm.SaveOnClose();
        };
    }

    private static PortForwardRow? RowOf(object sender)
        => (sender as FrameworkElement)?.DataContext as PortForwardRow;

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            _ = _vm.StartAsync(row);
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            _ = _vm.StopAsync(row);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        if (System.Windows.MessageBox.Show(this, $"确定删除端口转发规则「{row.Name}」？此操作不可恢复。",
                "删除", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            return;
        _vm.Remove(row);
    }
}
