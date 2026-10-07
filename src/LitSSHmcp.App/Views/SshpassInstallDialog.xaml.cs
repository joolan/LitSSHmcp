using System.Windows;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public enum SshpassInstallChoice
{
    Retry,
    Relay,
    Cancel
}

/// <summary>源服务器缺少 sshpass 时的提示框：给出各发行版安装命令，并让用户选择重试 / 内存中转 / 取消。</summary>
public partial class SshpassInstallDialog : FluentWindow
{
    private SshpassInstallChoice _choice = SshpassInstallChoice.Cancel;

    public SshpassInstallDialog()
    {
        InitializeComponent();
    }

    public static SshpassInstallChoice Show(Window? owner, string hint, string commands, bool allowRelay)
    {
        var dlg = new SshpassInstallDialog { Owner = owner };
        dlg.HintText.Text = hint;
        dlg.CmdBox.Text = commands;
        if (!allowRelay)
            dlg.RelayButton.Visibility = Visibility.Collapsed;
        dlg.ShowDialog();
        return dlg._choice;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(CmdBox.Text); } catch { /* ignore */ }
    }

    private void OnRetry(object sender, RoutedEventArgs e)
    {
        _choice = SshpassInstallChoice.Retry;
        Close();
    }

    private void OnRelay(object sender, RoutedEventArgs e)
    {
        _choice = SshpassInstallChoice.Relay;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _choice = SshpassInstallChoice.Cancel;
        Close();
    }
}
