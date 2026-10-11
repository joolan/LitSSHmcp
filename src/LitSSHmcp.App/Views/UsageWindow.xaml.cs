using System.Windows;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

/// <summary>当前会话的用量 / 成本报告（只读展示 + 复制）。</summary>
public partial class UsageWindow : FluentWindow
{
    public UsageWindow(string report)
    {
        InitializeComponent();
        ReportBox.Text = report;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ReportBox.Text); } catch { /* ignore */ }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
