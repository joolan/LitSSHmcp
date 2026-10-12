using System.Data;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
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

        // AvalonEdit 的 Text 不是依赖属性，无法绑定：这里在编辑器与 VM 之间做双向同步。
        _vm.PropertyChanged += OnVmPropertyChanged;
        SqlBox.TextChanged += OnEditorTextChanged;
        SqlBox.TextArea.TextEntered += OnEditorTextEntered;
        SqlBox.TextArea.TextEntering += OnEditorTextEntering;
        SqlBox.Text = _vm.SqlText ?? string.Empty;

        SqlBox.Options.HighlightCurrentLine = true;
        SqlBox.Options.IndentationSize = 4;
        SqlBox.Options.ConvertTabsToSpaces = true;
        ICSharpCode.AvalonEdit.Search.SearchPanel.Install(SqlBox.TextArea);   // Ctrl+F 查找/替换

        if (initial is not null)
        {
            _requestedId = initial.Id;
            _vm.RequestSelect(initial.Id);
        }
    }

    private string? _requestedId;

    /// <summary>当前或请求打开的数据源 Id（供“同一数据源只开一个窗口”去重）。</summary>
    public string? CurrentDatasourceId => _vm.SelectedDatasource?.Id ?? _requestedId;

    // ---------- SQL 补全 ----------

    private CompletionWindow? _completionWindow;

    private static readonly string[] SqlKeywords =
    {
        "SELECT", "FROM", "WHERE", "JOIN", "LEFT JOIN", "RIGHT JOIN", "INNER JOIN", "OUTER JOIN", "ON",
        "GROUP BY", "ORDER BY", "HAVING", "LIMIT", "OFFSET", "UNION", "UNION ALL", "WITH",
        "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE", "CREATE", "ALTER", "DROP", "TRUNCATE",
        "TABLE", "VIEW", "INDEX", "PRIMARY KEY", "FOREIGN KEY", "REFERENCES",
        "AS", "AND", "OR", "NOT", "NULL", "IS NULL", "IN", "LIKE", "BETWEEN", "CASE", "WHEN", "THEN", "ELSE", "END",
        "COUNT", "SUM", "AVG", "MIN", "MAX", "DISTINCT", "COALESCE", "CAST", "EXPLAIN", "DESC", "ASC"
    };

    private void OnEditorTextEntered(object? sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length != 1)
            return;
        var ch = e.Text[0];
        if (!char.IsLetterOrDigit(ch) && ch != '_')
            return;

        var doc = SqlBox.Document;
        var caret = SqlBox.TextArea.Caret.Offset;
        var start = caret;
        while (start > 0)
        {
            var prev = doc.GetCharAt(start - 1);
            if (char.IsLetterOrDigit(prev) || prev == '_' || prev == '.')
                start--;
            else
                break;
        }
        var word = doc.GetText(start, caret - start);
        if (word.Length < 2)
        {
            CloseCompletion();
            return;
        }
        ShowCompletion(start, word);
    }

    private void OnEditorTextEntering(object? sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length > 0 && _completionWindow is not null)
        {
            var ch = e.Text[0];
            if (!char.IsLetterOrDigit(ch) && ch != '_' && ch != '.')
                _completionWindow.CompletionList.RequestInsertion(e);
        }
    }

    private void ShowCompletion(int startOffset, string prefix)
    {
        CloseCompletion();
        var words = new List<string>(SqlKeywords);
        words.AddRange(_vm.GetCompletionWords());

        var window = new CompletionWindow(SqlBox.TextArea) { StartOffset = startOffset };
        var list = window.CompletionList.CompletionData;
        foreach (var w in words.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (w.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !w.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                list.Add(new SqlCompletionData(w));
        }
        if (list.Count == 0)
            return;

        window.Closed += (_, _) => _completionWindow = null;
        _completionWindow = window;
        window.Show();
    }

    private void CloseCompletion()
    {
        _completionWindow?.Close();
        _completionWindow = null;
    }

    private sealed class SqlCompletionData : ICompletionData
    {
        public SqlCompletionData(string text) => Text = text;

        public System.Windows.Media.ImageSource? Image => null;
        public string Text { get; }
        public object Content => Text;
        public object Description => Text;
        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
            => textArea.Document.Replace(completionSegment, Text);
    }

    private bool _syncingSql;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DbWorkbenchViewModel.Results))
            ResetColumnFilters();

        if (e.PropertyName != nameof(DbWorkbenchViewModel.SqlText) || _syncingSql)
            return;
        _syncingSql = true;
        try
        {
            var text = _vm.SqlText ?? string.Empty;
            if (!string.Equals(SqlBox.Text, text, StringComparison.Ordinal))
                SqlBox.Text = text;
        }
        finally { _syncingSql = false; }
    }

    // ---------- 结果网格：行号 / 冻结首列 / 列筛选 ----------

    private readonly Dictionary<string, string> _columnFilters = new(StringComparer.OrdinalIgnoreCase);

    private void OnResultLoadingRow(object sender, DataGridRowEventArgs e)
        => e.Row.Header = (e.Row.GetIndex() + 1).ToString();

    private void OnAutoGeneratedColumns(object? sender, EventArgs e)
        => ResultGrid.FrozenColumnCount = ResultGrid.Columns.Count > 0 ? 1 : 0;

    private void OnColumnFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb || tb.Tag is not string column)
            return;
        if (string.IsNullOrEmpty(tb.Text))
            _columnFilters.Remove(column);
        else
            _columnFilters[column] = tb.Text;
        ApplyColumnFilters();
    }

    private void ApplyColumnFilters()
    {
        var view = _vm.Results;
        if (view is null)
            return;
        if (_columnFilters.Count == 0)
        {
            view.RowFilter = string.Empty;
            return;
        }
        var parts = _columnFilters.Select(kv =>
            $"[{kv.Key.Replace("]", "]]")}] LIKE '%{EscapeLike(kv.Value)}%'");
        try { view.RowFilter = string.Join(" AND ", parts); }
        catch { /* 非法筛选表达式忽略 */ }
    }

    private void ResetColumnFilters()
    {
        _columnFilters.Clear();
        for (var i = 0; i < FilterRow.Items.Count; i++)
        {
            if (FilterRow.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject container &&
                FindDescendant<System.Windows.Controls.TextBox>(container) is { } tb)
                tb.Text = string.Empty;
        }
    }

    private static string EscapeLike(string s)
        => s.Replace("[", "[[]").Replace("%", "[%]").Replace("*", "[*]").Replace("'", "''");

    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null)
            return null;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            var found = FindDescendant<T>(child);
            if (found is not null)
                return found;
        }
        return null;
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_syncingSql)
            return;
        _syncingSql = true;
        try { _vm.SqlText = SqlBox.Text; }
        finally { _syncingSql = false; }
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
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                // Ctrl+Shift+Enter：执行全部
                if (_vm.ExecuteCommand.CanExecute(null))
                    _vm.ExecuteCommand.Execute(null);
            }
            else if (sender is ICSharpCode.AvalonEdit.TextEditor editor)
            {
                // Ctrl+Enter：有选区执行选区，否则执行光标所在语句
                _ = _vm.ExecuteFromEditorAsync(editor.SelectionStart, editor.SelectionLength, editor.CaretOffset);
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_vm.CancelCommand.CanExecute(null))
                _vm.CancelCommand.Execute(null);
            e.Handled = true;
        }
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
        // 双击列头：该列宽自适应
        var header = FindAncestor<System.Windows.Controls.Primitives.DataGridColumnHeader>(e.OriginalSource as DependencyObject);
        if (header?.Column is { } column)
        {
            column.Width = DataGridLength.Auto;
            e.Handled = true;
            return;
        }

        if (ResultGrid.CurrentCell.IsValid)
            OnViewCell(sender, e);
    }

    /// <summary>结果列使用 NULL 显示转换器（仅影响显示，复制/导出仍为原始值）。</summary>
    private void OnAutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.Column is DataGridTextColumn text)
        {
            var path = (text.Binding as Binding)?.Path?.Path;
            var bindPath = string.IsNullOrEmpty(path) ? "[" + e.PropertyName + "]" : path;
            text.Binding = new Binding(bindPath) { Converter = LitSSHmcp.App.Controls.NullToNullTextConverter.Instance };
        }
    }

    /// <summary>把当前行复制为 INSERT 语句（表名取对象浏览器选中表，否则占位 table）。</summary>
    private void OnCopyInsert(object sender, RoutedEventArgs e)
    {
        if (ResultGrid.CurrentItem is not DataRowView row)
            return;
        var table = row.Row.Table;
        var columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
        var tableName = _vm.SelectedTable?.Name;
        if (string.IsNullOrWhiteSpace(tableName))
            tableName = "table";

        var cols = string.Join(", ", columns);
        var vals = string.Join(", ", columns.Select(c => SqlLiteral(row[c])));
        SafeSetClipboard($"INSERT INTO {tableName} ({cols}) VALUES ({vals});");
    }

    private static string SqlLiteral(object? value)
    {
        if (value is null || value == DBNull.Value)
            return "NULL";
        return value switch
        {
            string s => "'" + s.Replace("'", "''") + "'",
            bool b => b ? "1" : "0",
            DateTime dt => "'" + dt.ToString("yyyy-MM-dd HH:mm:ss") + "'",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "NULL"
        };
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
