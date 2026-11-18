using System.Windows;
using LitSSHmcp.Core.Services.Security;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class TerminalAiCommandApprovalWindow : FluentWindow
{
    public TerminalAiCommandApprovalWindow(
        string serverName,
        string command,
        string workingDirectory,
        bool sudo,
        CommandFilterResult filter,
        bool hasConfiguredPassword,
        bool allowManualPassword = true)
    {
        InitializeComponent();
        IntroText.Text = sudo
            ? $"AI 请求在服务器「{serverName}」上执行提权命令："
            : $"AI 请求在服务器「{serverName}」的当前终端执行命令：";
        ScopeText.Text = string.IsNullOrWhiteSpace(workingDirectory)
            ? "当前终端工作目录未知"
            : "当前终端工作目录：" + workingDirectory;
        CommandText.Text = command;

        if (sudo && allowManualPassword)
        {
            ManualPasswordPanel.Visibility = Visibility.Visible;
            ManualPasswordBox.IsChecked = true;
            ManualPasswordHint.Text = hasConfiguredPassword
                ? "默认勾选：确认后会填入左侧终端并等待你输入密码，完成后 AI 自动读取结果；取消勾选才会使用服务器配置密码自动执行。"
                : "服务器未配置提权密码；确认后会填入左侧终端并等待你输入密码，完成后 AI 自动读取结果。";
        }

        var warnings = new List<string>();
        if (sudo)
            warnings.Add("这是提权命令，可能修改系统级配置。");
        if (filter == CommandFilterResult.Sensitive)
            warnings.Add("命令命中敏感规则（删除、重启、权限修改等）。");
        if (warnings.Count > 0)
        {
            WarnText.Text = string.Join("\n", warnings);
            WarnBorder.Visibility = Visibility.Visible;
        }
    }

    public bool Approved { get; private set; }
    public bool ManualPassword => ManualPasswordBox.IsChecked == true;

    private void OnAllow(object sender, RoutedEventArgs e)
    {
        Approved = true;
        Close();
    }

    private void OnDeny(object sender, RoutedEventArgs e)
    {
        Approved = false;
        Close();
    }
}
