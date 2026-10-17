using System.Windows;
using LitSSHmcp.App.Services.LocalTerminal;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

/// <summary>
/// AI 请求在本机终端执行命令时的人工审批对话框。
/// 支持三档放行范围（仅本次 / 本轮次内全部命令 / 本次会话全部命令），默认仅本次；
/// 「总是允许」仍为配置级永久记忆。
/// </summary>
public partial class LocalCommandApprovalWindow : FluentWindow
{
    public LocalApprovalDecision Decision { get; private set; } = LocalApprovalDecision.Deny;

    public LocalCommandApprovalWindow(string command, string workingDirectory, bool sessionAllGranted = false, string? sessionLabel = null)
    {
        InitializeComponent();
        CommandText.Text = command ?? string.Empty;
        CwdText.Text = string.IsNullOrWhiteSpace(workingDirectory) ? string.Empty : "工作目录：" + workingDirectory;

        // 并行会话后桌面可能同时出现多个审批弹窗：标明所属会话，避免审批错认（评估报告 §4.2）
        if (!string.IsNullOrWhiteSpace(sessionLabel))
        {
            SessionText.Text = "所属会话：" + sessionLabel;
            SessionText.Visibility = Visibility.Visible;
        }

        if (sessionAllGranted)
        {
            SessionHint.Text = "已设置：本次 AI 会话期间允许全部命令（本弹窗不会再出现；重启或新会话即失效）";
            SessionHint.Visibility = Visibility.Visible;
        }
    }

    private void OnRoundChecked(object sender, RoutedEventArgs e)
    {
        if (RoundBox.IsChecked == true)
            SessionBox.IsChecked = false;   // 二选一
    }

    private void OnSessionChecked(object sender, RoutedEventArgs e)
    {
        if (SessionBox.IsChecked == true)
            RoundBox.IsChecked = false;   // 二选一
    }

    private void OnAllow(object sender, RoutedEventArgs e)
    {
        Decision = SessionBox.IsChecked == true ? LocalApprovalDecision.AllowSession
            : RoundBox.IsChecked == true ? LocalApprovalDecision.AllowRound
            : LocalApprovalDecision.Allow;
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
    public static LocalApprovalDecision Prompt(string command, string workingDirectory, bool sessionAllGranted = false, string? sessionLabel = null)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            return dispatcher.Invoke(() => Prompt(command, workingDirectory, sessionAllGranted, sessionLabel));

        var window = new LocalCommandApprovalWindow(command, workingDirectory, sessionAllGranted, sessionLabel) { Topmost = true };
        window.ShowDialog();
        return window.Decision;
    }
}
