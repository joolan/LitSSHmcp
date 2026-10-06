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
