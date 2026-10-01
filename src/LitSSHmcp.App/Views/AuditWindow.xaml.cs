using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using Microsoft.Win32;

namespace LitSSHmcp.App.Views;

public partial class AuditWindow : Window
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
        _viewModel.LimitText = LimitBox.Text;
        _viewModel.Load();
        FilterBox.Text = _viewModel.FilterText;
        KeywordBox.Text = _viewModel.Keyword;
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

        var isSqlTab = SqlList.IsVisible;
        var csv = isSqlTab ? _viewModel.BuildSqlCsv() : _viewModel.BuildCommandsCsv();

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
                c.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"), c.ServerName, c.Command, c.Status.ToString(), c.ExitCode?.ToString() ?? ""),
            SqlAuditLog s => string.Join('\t',
                s.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"), s.DataSourceName, s.Operation.ToString(), s.Sql, s.Status.ToString()),
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
