using System.Windows;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public enum CloseChoice
{
    MinimizeToTray,
    Exit,
    Cancel
}

/// <summary>关闭主窗口时的选择：最小化到托盘 / 退出程序 / 取消。</summary>
public partial class CloseChoiceDialog : FluentWindow
{
    private CloseChoice _choice = CloseChoice.Cancel;

    public CloseChoiceDialog() => InitializeComponent();

    public static CloseChoice Show(Window? owner)
    {
        var dlg = new CloseChoiceDialog { Owner = owner };
        dlg.ShowDialog();
        return dlg._choice;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) { _choice = CloseChoice.MinimizeToTray; Close(); }

    private void OnExit(object sender, RoutedEventArgs e) { _choice = CloseChoice.Exit; Close(); }

    private void OnCancel(object sender, RoutedEventArgs e) { _choice = CloseChoice.Cancel; Close(); }
}
