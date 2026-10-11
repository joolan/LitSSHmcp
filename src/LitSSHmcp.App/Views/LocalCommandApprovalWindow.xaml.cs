using System.Windows;
using LitSSHmcp.App.Services.LocalTerminal;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

/// <summary>AI 请求在本机终端执行命令时的人工审批对话框。</summary>
public partial class LocalCommandApprovalWindow : FluentWindow
{
    public LocalApprovalDecision Decision { get; private set; } = LocalApprovalDecision.Deny;

    public LocalCommandApprovalWindow(string command, string workingDirectory)
    {
        InitializeComponent();
        CommandText.Text = command ?? string.Empty;
        CwdText.Text = string.IsNullOrWhiteSpace(workingDirectory) ? string.Empty : "工作目录：" + workingDirectory;
    }

    private void OnAllow(object sender, RoutedEventArgs e)
    {
        Decision = LocalApprovalDecision.Allow;
        Close();
    }

    private void OnAlways(object sender, RoutedEventArgs e)
    {
        Decision = LocalApprovalDecision.AlwaysAllow;
        Close();
    }

    private void OnDeny(object sender, RoutedEventArgs e)
    {
        Decision = LocalApprovalDecision.Deny;
        Close();
    }

    /// <summary>在 UI 线程弹出审批对话框并返回用户选择（非 UI 线程会切到 UI 线程）。</summary>
    public static LocalApprovalDecision Prompt(string command, string workingDirectory)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            return dispatcher.Invoke(() => Prompt(command, workingDirectory));

        var window = new LocalCommandApprovalWindow(command, workingDirectory) { Topmost = true };
        window.ShowDialog();
        return window.Decision;
    }
}
