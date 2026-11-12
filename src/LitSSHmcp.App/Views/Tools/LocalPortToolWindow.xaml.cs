using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Toolbox;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views.Tools;

public partial class LocalPortToolWindow : FluentWindow
{
    private static readonly Encoding CsvEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private readonly LocalPortProcessService _service = new();
    private readonly ObservableCollection<PortProcessEntry> _items = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private IReadOnlyList<PortProcessEntry> _all = Array.Empty<PortProcessEntry>();
    private bool _refreshing;

    public LocalPortToolWindow()
    {
        InitializeComponent();
        PortGrid.ItemsSource = _items;
        _refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        Closed += (_, _) => _refreshTimer.Stop();
        Loaded += (_, _) => _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
            return;

        _refreshing = true;
        StatusText.Text = "正在刷新...";
        try
        {
            _all = await _service.GetEntriesAsync();
            ApplyFilter();
            StatusText.Text = $"刷新于 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "刷新失败：" + ex.Message;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplyFilter()
    {
        var listeningOnly = FilterBox.SelectedIndex <= 0;
        var search = SearchBox.Text.Trim();
        var filtered = _all
            .Where(item => !listeningOnly || item.IsListening)
            .Where(item => search.Length == 0 || Matches(item, search))
            .ToList();

        _items.Clear();
        foreach (var item in filtered)
            _items.Add(item);

        var processCount = filtered.Select(item => item.ProcessId).Where(pid => pid > 0).Distinct().Count();
        SummaryText.Text = $"显示 {filtered.Count} 条 · 进程 {processCount} 个";
    }

    private static bool Matches(PortProcessEntry item, string search)
    {
        return item.LocalEndpoint.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.RemoteEndpoint.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.ProcessName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.ProcessPath.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.State.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.ProcessId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void OnAutoRefreshChanged(object sender, RoutedEventArgs e)
    {
        if (AutoRefreshCheck.IsChecked == true)
            _refreshTimer.Start();
        else
            _refreshTimer.Stop();
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            ApplyFilter();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
            ApplyFilter();
    }

    private void OnKillProcess(object sender, RoutedEventArgs e)
    {
        if (PortGrid.SelectedItem is not PortProcessEntry entry)
        {
            StatusText.Text = "请先选择一条记录。";
            return;
        }
        if (entry.ProcessId <= 4 || entry.ProcessId == Environment.ProcessId)
        {
            StatusText.Text = "出于安全考虑，不能结束系统进程或当前 LitSSH 进程。";
            return;
        }

        var target = string.IsNullOrWhiteSpace(entry.ProcessPath)
            ? $"{entry.ProcessName} (PID {entry.ProcessId})"
            : $"{entry.ProcessName} (PID {entry.ProcessId})\n{entry.ProcessPath}";
        if (!new UserDialogService().Confirm(
                $"确定结束以下进程？此操作不可撤销。\n\n{target}",
                "结束进程", warning: true))
            return;

        try
        {
            using var process = Process.GetProcessById(entry.ProcessId);
            process.Kill(entireProcessTree: true);
            StatusText.Text = $"已结束进程 {entry.ProcessName} ({entry.ProcessId})。";
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "结束进程失败：" + ex.Message;
        }
    }

    private void OnOpenLocation(object sender, RoutedEventArgs e)
    {
        if (PortGrid.SelectedItem is not PortProcessEntry entry)
        {
            StatusText.Text = "请先选择一条记录。";
            return;
        }
        if (string.IsNullOrWhiteSpace(entry.ProcessPath))
        {
            StatusText.Text = "当前记录没有可访问的进程路径（可能需要管理员权限）。";
            return;
        }

        NativeShell.RevealInExplorer(entry.ProcessPath);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (PortGrid.SelectedItem is not PortProcessEntry entry)
        {
            StatusText.Text = "请先选择一条记录。";
            return;
        }

        try
        {
            Clipboard.SetText(entry.ToCopyText());
            StatusText.Text = "已复制当前记录。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0)
        {
            StatusText.Text = "没有可导出的记录。";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出本地端口 / 进程",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"local-ports-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var csv = new StringBuilder();
            csv.AppendLine("protocol,local_address,local_port,remote_address,remote_port,state,pid,process,path");
            foreach (var item in _items)
            {
                csv.AppendLine(string.Join(",",
                    Csv(item.Protocol), Csv(item.LocalAddress),
                    item.LocalPort.ToString(), Csv(item.RemoteAddress),
                    item.RemotePort.ToString(), Csv(item.State),
                    item.ProcessId.ToString(), Csv(item.ProcessName), Csv(item.ProcessPath)));
            }

            File.WriteAllText(dialog.FileName, csv.ToString(), CsvEncoding);
            StatusText.Text = "已导出：" + dialog.FileName;
        }
        catch (Exception ex)
        {
            StatusText.Text = "导出失败：" + ex.Message;
        }
    }

    private static string Csv(string? value)
        => "\"" + (value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
