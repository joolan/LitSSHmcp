using System.Data;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class DbWorkbenchWindow : FluentWindow
{
    private readonly DbWorkbenchViewModel _vm;

    public DbWorkbenchWindow(DataSourceConfig? initial = null)
    {
        InitializeComponent();
        _vm = new DbWorkbenchViewModel();
        DataContext = _vm;
        _vm.ConfirmWrite = info => DbWritePreviewWindow.Show(this, info);
        WindowLayout.Attach(this, "dbworkbench");

        if (initial is not null)
            _vm.RequestSelect(initial.Id);
    }

    /// <summary>已打开的工作台切换到指定数据源（数据源管理里"打开工作台"复用同一窗口）。</summary>
    public void SelectDatasource(DataSourceConfig ds) => _vm.RequestSelect(ds.Id);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ---------- SQL 片段下拉 ----------

    private void OnToggleSnippetPanel(object sender, RoutedEventArgs e)
        => _vm.IsSnippetPanelOpen = !_vm.IsSnippetPanelOpen;

    private void OnUseSnippetClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DbSqlSnippet snippet })
            _vm.UseSnippet(snippet);
    }

    private void OnDeleteSnippetClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DbSqlSnippet snippet })
            _vm.DeleteSnippet(snippet);
    }

    /// <summary>点击窗口其它区域关闭片段下拉（下拉内部的点击不关闭）。</summary>
    private void OnRootPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.IsSnippetPanelOpen)
            return;

        var src = e.OriginalSource as DependencyObject;
        // 点击「SQL片段」按钮本身或下拉内部（Popup.Child 内容树）时，不在此关闭
        if (IsDescendantOrSelf(src, SnippetButton) ||
            (SnippetPopup.Child is DependencyObject popupContent && IsDescendantOrSelf(src, popupContent)))
            return;
        _vm.IsSnippetPanelOpen = false;
    }

    /// <summary>
    /// src 是否为 ancestor 的后代或自身。视觉树向上查找；到弹出层根部（视觉父为空）时回退逻辑树，
    /// 以便识别 Popup 内容（Popup 内容的逻辑父链会到达 Popup 元素）。
    /// </summary>
    private static bool IsDescendantOrSelf(DependencyObject? src, DependencyObject ancestor)
    {
        while (src is not null)
        {
            if (ReferenceEquals(src, ancestor))
                return true;
            var visual = src is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(src)
                : null;
            src = visual ?? LogicalTreeHelper.GetParent(src);
        }
        return false;
    }

    private void OnTableDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.PreviewCommand.CanExecute(null))
            _vm.PreviewCommand.Execute(null);
    }

    private void OnKeyDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox { SelectedItem: string key } &&
            _vm.UseKeyCommand.CanExecute(key))
            _vm.UseKeyCommand.Execute(key);
    }

    private void OnKeyPatternKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (_vm.ScanKeysCommand.CanExecute(null))
                _vm.ScanKeysCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnSqlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_vm.ExecuteCommand.CanExecute(null))
                _vm.ExecuteCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // 显式处理编辑快捷键：部分环境下 Ctrl+X 只复制不删除，这里直接操作 TextBox 保证剪贴并移除选中。
        if (Keyboard.Modifiers == ModifierKeys.Control && sender is System.Windows.Controls.TextBox tb)
        {
            switch (e.Key)
            {
                case Key.X when tb.SelectionLength > 0:
                    SafeSetClipboard(tb.SelectedText);
                    RemoveSelection(tb);
                    e.Handled = true;
                    break;
                case Key.C when tb.SelectionLength > 0:
                    SafeSetClipboard(tb.SelectedText);
                    e.Handled = true;
                    break;
                case Key.V:
                    if (Clipboard.ContainsText())
                        InsertText(tb, Clipboard.GetText());
                    e.Handled = true;
                    break;
                case Key.A:
                    tb.SelectAll();
                    e.Handled = true;
                    break;
            }
        }
    }

    private static void RemoveSelection(System.Windows.Controls.TextBox tb)
    {
        var start = tb.SelectionStart;
        tb.Text = tb.Text.Remove(start, tb.SelectionLength);
        tb.SelectionStart = start;
        tb.SelectionLength = 0;
    }

    private static void InsertText(System.Windows.Controls.TextBox tb, string text)
    {
        var start = tb.SelectionStart;
        tb.Text = tb.Text.Remove(start, tb.SelectionLength).Insert(start, text);
        tb.SelectionStart = start + text.Length;
        tb.SelectionLength = 0;
    }

    // ---------- 结果复制 ----------

    /// <summary>右键先把光标所在单元格设为当前单元格，保证复制/查看作用在点中的那个。</summary>
    private void OnResultRightDown(object sender, MouseButtonEventArgs e)
    {
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell is not null)
        {
            cell.IsSelected = true;
            ResultGrid.CurrentCell = new DataGridCellInfo(cell);
        }
    }

    private void OnCopyCell(object sender, RoutedEventArgs e)
    {
        var text = CurrentCellText();
        if (!string.IsNullOrEmpty(text))
            SafeSetClipboard(text);
    }

    private void OnCopyRow(object sender, RoutedEventArgs e)
    {
        if (ResultGrid.CurrentItem is DataRowView row)
            SafeSetClipboard(string.Join("\t", row.Row.ItemArray.Select(v => v?.ToString() ?? string.Empty)));
    }

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        var view = _vm.Results;
        var table = view?.Table;
        if (view is null || table is null) return;

        var sb = new StringBuilder();
        var columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
        sb.AppendLine(string.Join("\t", columns));
        foreach (DataRowView row in view)
            sb.AppendLine(string.Join("\t", columns.Select(c => row[c]?.ToString() ?? string.Empty)));
        SafeSetClipboard(sb.ToString());
    }

    private void OnViewCell(object sender, RoutedEventArgs e)
    {
        var text = CurrentCellText();
        if (text is null) return;
        var cell = ResultGrid.SelectedCells.FirstOrDefault();
        if (!cell.IsValid) cell = ResultGrid.CurrentCell;
        var name = cell.Column?.Header?.ToString() ?? "单元格";
        ResultDialog.Show(this, "单元格内容", name, text, isError: false);
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultGrid.CurrentCell.IsValid)
            OnViewCell(sender, e);
    }

    private string? CurrentCellText()
    {
        var cell = ResultGrid.SelectedCells.FirstOrDefault();
        if (!cell.IsValid)
            cell = ResultGrid.CurrentCell;
        if (!cell.IsValid || cell.Item is not DataRowView row || cell.Column is null)
            return null;
        var name = cell.Column.Header?.ToString();
        if (string.IsNullOrEmpty(name) || !row.Row.Table.Columns.Contains(name))
            return null;
        return row[name]?.ToString();
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private static void SafeSetClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch { /* 剪贴板被占用时忽略 */ }
    }
}
