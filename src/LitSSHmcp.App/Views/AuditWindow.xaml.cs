using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using Microsoft.Win32;

namespace LitSSHmcp.App.Views;

public partial class AuditWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly AuditViewModel _viewModel = new();
    private string? _lastCellText;

    public AuditWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += (_, _) => _viewModel.Load();
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        _viewModel.FilterText = FilterBox.Text;
        _viewModel.Keyword = KeywordBox.Text;
        _viewModel.SessionFilter = SessionBox.Text;
        _viewModel.LimitText = LimitBox.Text;
        _viewModel.Load();
        FilterBox.Text = _viewModel.FilterText;
        KeywordBox.Text = _viewModel.Keyword;
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        OnRefresh(sender, e);
        e.Handled = true;
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        FilterBox.Text = string.Empty;
        KeywordBox.Text = string.Empty;
        SessionBox.Text = string.Empty;
        LimitBox.Text = "200";
        _viewModel.FilterText = string.Empty;
        _viewModel.Keyword = string.Empty;
        _viewModel.SessionFilter = string.Empty;
        _viewModel.LimitText = "200";
        _viewModel.CategoryIndex = 0;
        _viewModel.IncludeHistory = false;
        _viewModel.Load();
    }

    private void OnAuditSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView listView)
            _viewModel.SelectedAuditItem = listView.SelectedItem;
    }

    private void OnAuditTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        CommandList.SelectedItem = null;
        SqlList.SelectedItem = null;
        SessionsList.SelectedItem = null;
        _viewModel.SelectedAuditItem = null;
    }

    private void OnCloseAuditDetail(object sender, RoutedEventArgs e) => _viewModel.SelectedAuditItem = null;

    private void OnCopyAuditDetail(object sender, RoutedEventArgs e)
    {
        var text = _viewModel.SelectedAuditItem switch
        {
            CommandAuditLog c => string.Join('\n',
                $"时间: {c.Timestamp:yyyy-MM-dd HH:mm:ss}",
                $"会话: {c.SessionId}",
                $"用户: {c.UserDisplay}",
                $"工具: {c.Tool}",
                $"类型: {c.Category}",
                $"服务器: {c.ServerName}",
                $"状态: {c.Status}",
                $"决策: {c.Decision}",
                $"退出码: {c.ExitCode}",
                $"文件: {c.FilePath}",
                $"命令:\n{c.Command}",
                $"结果:\n{c.Result}"),
            SqlAuditLog s => string.Join('\n',
                $"时间: {s.Timestamp:yyyy-MM-dd HH:mm:ss}",
                $"会话: {s.SessionId}",
                $"用户: {s.UserDisplay}",
                $"工具: {s.Tool}",
                $"数据源: {s.DataSourceName}",
                $"类型: {s.Category}",
                $"操作: {s.Operation}",
                $"状态: {s.Status}",
                $"决策: {s.Decision}",
                $"影响行数: {s.RowsAffected}",
                $"耗时: {s.DurationMs:F1} ms",
                $"SQL/Redis 命令:\n{s.Sql}",
                $"结果:\n{s.Result}"),
            AuditSession a => string.Join('\n',
                $"会话 ID: {a.SessionId}",
                $"客户端: {a.ClientName}",
                $"版本: {a.ClientVersion}",
                $"归属用户: {a.OwnerUserId}",
                $"首次活动: {a.StartedAt:yyyy-MM-dd HH:mm:ss}",
                $"最近活动: {a.LastSeenAt:yyyy-MM-dd HH:mm:ss}"),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            Clipboard.SetText(text);
            _viewModel.StatusMessage = "已复制审计详情";
        }
        catch
        {
            _viewModel.StatusMessage = "复制失败";
        }
    }

    private void OnVerify(object sender, RoutedEventArgs e) => _viewModel.VerifyIntegrity();

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"litssh-audit-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            Filter = "CSV 文件|*.csv|所有文件|*.*"
        };

        if (dialog.ShowDialog() != true)
            return;

        var csv = SqlList.IsVisible
            ? _viewModel.BuildSqlCsv()
            : SessionsList.IsVisible
                ? _viewModel.BuildSessionsCsv()
                : _viewModel.BuildCommandsCsv();

        File.WriteAllText(dialog.FileName, csv, new System.Text.UTF8Encoding(true));
        _viewModel.StatusMessage = $"已导出: {dialog.FileName}";
    }

    // 记录右键点击的单元格文本，并选中所在行，便于“复制整行/复制单元格”
    private void OnListPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView listView)
            return;

        var source = e.OriginalSource as DependencyObject;
        _lastCellText = FindAncestor<TextBlock>(source)?.Text;

        var item = FindAncestor<ListViewItem>(source);
        if (item != null)
            listView.SelectedItem = item.DataContext;
    }

    private void OnCopyRow(object sender, RoutedEventArgs e)
    {
        var listView = GetOwnerListView(sender);
        var text = listView?.SelectedItem switch
        {
            CommandAuditLog c => string.Join('\t',
                c.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"), c.SessionId ?? "", c.Tool ?? "", c.ServerName, c.Command, c.Status.ToString(), c.ExitCode?.ToString() ?? ""),
            SqlAuditLog s => string.Join('\t',
                s.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"), s.SessionId ?? "", s.Tool ?? "", s.DataSourceName, s.Operation.ToString(), s.Sql, s.Status.ToString()),
            AuditSession a => string.Join('\t',
                a.SessionId, a.ClientName ?? "", a.ClientVersion ?? "", a.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"), a.LastSeenAt.ToString("yyyy-MM-dd HH:mm:ss")),
            _ => null
        };

        CopyToClipboard(text ?? string.Empty, "整行");
    }

    private void OnCopyCell(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastCellText))
        {
            CopyToClipboard(_lastCellText!, "单元格");
            return;
        }

        OnCopyRow(sender, e);
    }

    private void CopyToClipboard(string text, string what)
    {
        if (string.IsNullOrEmpty(text))
        {
            _viewModel.StatusMessage = "没有可复制的内容";
            return;
        }

        try
        {
            Clipboard.SetText(text);
            _viewModel.StatusMessage = $"已复制{what}到剪贴板";
        }
        catch (Exception ex)
        {
            _viewModel.StatusMessage = $"复制失败: {ex.Message}";
        }
    }

    private static ListView? GetOwnerListView(object sender) =>
        (sender as MenuItem)?.Parent is ContextMenu menu ? menu.PlacementTarget as ListView : null;

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match)
                return match;

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }
}
