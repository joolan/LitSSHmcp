using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Views;

/// <summary>会话日志查看（回放）：列出 terminal-logs 下的记录并展示（去除 ANSI 控制序列）。</summary>
public partial class TerminalLogWindow : Wpf.Ui.Controls.FluentWindow
{
    private static readonly Regex AnsiRegex = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07]*\x07|\x1B[@-Z\\-_]", RegexOptions.Compiled);
    private readonly string _dir = Path.Combine(ConfigPaths.AppDataDir, "terminal-logs");

    public TerminalLogWindow()
    {
        InitializeComponent();
        WindowLayout.Attach(this, "terminal-logs");
        Refresh();
    }

    private void Refresh()
    {
        var items = new List<LogFileItem>();
        try
        {
            if (Directory.Exists(_dir))
            {
                foreach (var file in Directory.GetFiles(_dir, "*.log").OrderByDescending(File.GetLastWriteTime))
                    items.Add(new LogFileItem(file, Path.GetFileName(file), File.GetLastWriteTime(file)));
            }
        }
        catch
        {
            // 读取失败
        }
        LogList.ItemsSource = items;
        StatusText.Text = items.Count == 0 ? "暂无日志（需在 设置→终端 开启「记录会话日志」）" : $"{items.Count} 个日志";
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void OnSelect(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LogList.SelectedItem is not LogFileItem item)
            return;
        try
        {
            var text = File.ReadAllText(item.Path);
            ContentBox.Text = AnsiRegex.Replace(text, string.Empty);
        }
        catch (Exception ex)
        {
            ContentBox.Text = "读取失败: " + ex.Message;
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            Process.Start(new ProcessStartInfo(_dir) { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is not LogFileItem item)
            return;
        if (MessageBox.Show($"删除日志「{item.Name}」？", "删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try { File.Delete(item.Path); } catch { /* ignore */ }
        Refresh();
    }

    private sealed record LogFileItem(string Path, string Name, DateTime Modified)
    {
        public string ModifiedText => Modified.ToString("yyyy-MM-dd HH:mm");
    }
}
