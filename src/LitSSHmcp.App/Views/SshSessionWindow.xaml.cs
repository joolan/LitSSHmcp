using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace LitSSHmcp.App.Views;

/// <summary>SSH 会话管理：多标签窗口（每个服务器一个标签），非模态、不置顶。</summary>
public partial class SshSessionWindow : FluentWindow
{
    public SshSessionWindow()
    {
        InitializeComponent();
        WindowLayout.Attach(this, "ssh-session");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel { HasSessions: true } vm)
        {
            var result = System.Windows.MessageBox.Show(
                $"确定关闭 SSH 会话管理？当前 {vm.Sessions.Count} 个会话将断开。",
                "关闭", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (result != System.Windows.MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        base.OnClosing(e);
    }

    private void OnCommandKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (sender is TextBox { DataContext: SessionViewModel session } &&
            session.ExecuteCommand.CanExecute(null))
        {
            session.ExecuteCommand.Execute(null);
            e.Handled = true;
        }
    }
}
