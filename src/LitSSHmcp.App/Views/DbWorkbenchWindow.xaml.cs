using System.Data;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
using WpfButton = System.Windows.Controls.Button;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace LitSSHmcp.App.Views;

public partial class DbWorkbenchWindow : FluentWindow
{
    private readonly DbWorkbenchViewModel _vm;

    public DbWorkbenchWindow(DataSourceConfig? initial = null, IAppServices? services = null)
    {
        InitializeComponent();
        _vm = new DbWorkbenchViewModel(services: services);
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
    private readonly Dictionary<string, WpfButton> _filterButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<DataGridColumn, string> _columnNames = new();
    private Popup? _filterPopup;

    private void OnResultLoadingRow(object sender, DataGridRowEventArgs e)
        => e.Row.Header = (e.Row.GetIndex() + 1).ToString();

    private void OnAutoGeneratedColumns(object? sender, EventArgs e)
        => ResultGrid.FrozenColumnCount = ResultGrid.Columns.Count > 0 ? 1 : 0;

    private void AttachFilterHeader(DataGridColumn column, string fallback)
    {
        var headerText = column.Header?.ToString() ?? fallback;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new WpfTextBlock
        {
            Text = headerText,
            VerticalAlignment = VerticalAlignment.Center
        });
        var button = new WpfButton
        {
            Content = "▾",
            Tag = headerText,
            ToolTip = "筛选此列",
            FontSize = 10,
            Padding = new Thickness(2, 0, 2, 0),
            Margin = new Thickness(5, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += OnFilterButtonClick;
        panel.Children.Add(button);
        column.Header = panel;
        _columnNames[column] = headerText;
        _filterButtons[headerText] = button;
        UpdateFilterButton(headerText);
    }

    private string? GetColumnName(DataGridColumn? column)
    {
        if (column is null)
            return null;
        if (_columnNames.TryGetValue(column, out var name))
            return name;
        return string.IsNullOrWhiteSpace(column.SortMemberPath) ? null : column.SortMemberPath;
    }

    private void UpdateFilterButton(string header)
    {
        if (!_filterButtons.TryGetValue(header, out var button))
            return;
        var active = _columnFilters.TryGetValue(header, out var value) && !string.IsNullOrEmpty(value);
        button.Content = active ? "▼" : "▾";
        button.Background = active
            ? TryFindResource("AppAccentSubtleBrush") as Brush ?? Brushes.Transparent
            : Brushes.Transparent;
        button.Foreground = active
            ? TryFindResource("AppAccentBrush") as Brush ?? Brushes.DodgerBlue
            : TryFindResource("AppTextSecondaryBrush") as Brush ?? Brushes.Gray;
        button.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
        button.ToolTip = active ? $"筛选：{value}" : "筛选此列";
    }

    private void OnFilterButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: string header } button)
            return;

        if (_filterPopup is { IsOpen: true })
            _filterPopup.IsOpen = false;

        var box = new WpfTextBox
        {
            Text = _columnFilters.TryGetValue(header, out var current) ? current : string.Empty,
            Width = 220,
            Height = 28,
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(0, 6, 0, 6)
        };
        box.TextChanged += (_, _) =>
        {
            if (string.IsNullOrEmpty(box.Text))
                _columnFilters.Remove(header);
            else
                _columnFilters[header] = box.Text;
            UpdateFilterButton(header);
            ApplyColumnFilters();
        };

        var clear = new WpfButton
        {
            Content = "清除筛选",
            Padding = new Thickness(10, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        clear.Click += (_, _) => box.Text = string.Empty;

        var panel = new StackPanel { Margin = new Thickness(10) };
        panel.Children.Add(new WpfTextBlock
        {
            Text = $"筛选「{header}」",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2)
        });
        panel.Children.Add(box);
        panel.Children.Add(clear);

        var background = TryFindResource("AppSurfaceBrush") as Brush ?? Brushes.White;
        var borderBrush = TryFindResource("AppBorderBrush") as Brush ?? Brushes.Gray;
        var card = new Border
        {
            Background = background,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = panel
        };

        _filterPopup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = card
        };
        _filterPopup.IsOpen = true;
        box.Focus();
        box.SelectAll();
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
        foreach (var header in _filterButtons.Keys.ToList())
            UpdateFilterButton(header);
        if (_filterPopup is not null)
        {
            _filterPopup.IsOpen = false;
            _filterPopup = null;
        }
    }

    private static string EscapeLike(string s)
        => s.Replace("[", "[[]").Replace("%", "[%]").Replace("*", "[*]").Replace("'", "''");

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

    /// <summary>点击「导出 ▾」/「更多 ▾」时打开其下拉菜单。</summary>
    private void OnExportMenu(object sender, RoutedEventArgs e) => OpenButtonMenu(sender);

    private void OnMoreMenu(object sender, RoutedEventArgs e) => OpenButtonMenu(sender);

    // ---- AI 分析 SQL：打开独立的临时聊天并预填提示词（与 AI 运维助手的临时聊天相互独立） ----
    private TemporaryChatWindow? _sqlAiChat;

    private void OnAnalyzeSql(object sender, RoutedEventArgs e)
    {
        var sql = _vm.SqlText?.Trim();
        if (string.IsNullOrWhiteSpace(sql))
            return;

        // 只预填 SQL 代码块（完整分析要求由「数据库分析」智能体的系统提示承担），并切到该智能体
        var prompt = "```sql\n" + sql + "\n```";

        if (_sqlAiChat is { IsLoaded: true })
        {
            _sqlAiChat.Activate();
            _sqlAiChat.SelectAgent("db");
            _sqlAiChat.Prefill(prompt);
            return;
        }

        var window = new TemporaryChatWindow(new LitSSHmcp.Core.Services.Storage.ConfigService(), null, initialAgentId: "db")
        {
            Owner = this,
            Topmost = false
        };
        window.Closed += (_, _) => _sqlAiChat = null;
        _sqlAiChat = window;
        window.Show();
        window.Prefill(prompt);
    }

    private static void OpenButtonMenu(object sender)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    // ---------- SQL 片段下拉 ----------

    private void OnToggleSnippetPanel(object sender, RoutedEventArgs e)
        => _vm.IsSnippetPanelOpen = !_vm.IsSnippetPanelOpen;

    private void OnToggleHistoryPanel(object sender, RoutedEventArgs e)
        => _vm.IsHistoryPanelOpen = !_vm.IsHistoryPanelOpen;

    /// <summary>点击历史下拉以外区域时关闭它（点历史按钮本身由 Click 切换，不在此处理）。</summary>
    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.IsHistoryPanelOpen)
            return;
        var src = e.OriginalSource as DependencyObject;
        if ((HistoryPopup.Child is DependencyObject content && IsDescendantOf(content, src)) ||
            IsDescendantOf(HistoryButton, src))
            return;
        _vm.IsHistoryPanelOpen = false;
    }

    private static bool IsDescendantOf(DependencyObject ancestor, DependencyObject? node)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
                return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

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

    // ---------- 对象树 ----------

    private void OnObjectTreeExpanded(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.TreeViewItem)?.DataContext is DbTreeNode node)
            _ = _vm.LoadNodeChildrenAsync(node);
    }

    private void OnObjectTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is DbTreeNode node)
            _vm.OnTreeNodeSelected(node);
    }

    private void OnObjectTreeRightDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<System.Windows.Controls.TreeViewItem>(e.OriginalSource as DependencyObject) is { } item)
        {
            item.IsSelected = true;
            _vm.OnTreeNodeSelected(item.DataContext as DbTreeNode);
        }
    }

    /// <summary>仅在右键命中“表/视图/存储过程”对象节点或分类文件夹时弹出菜单；空白处/列/索引/外键不弹。</summary>
    private void OnObjectTreeMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = FindAncestor<System.Windows.Controls.TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not DbTreeNode { Kind: DbTreeNodeKind.Folder or DbTreeNodeKind.Table or DbTreeNodeKind.View or DbTreeNodeKind.Procedure })
            e.Handled = true;
    }

    private void OnObjectTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<System.Windows.Controls.TreeViewItem>(e.OriginalSource as DependencyObject)
                is { DataContext: DbTreeNode { Kind: DbTreeNodeKind.Table or DbTreeNodeKind.View } node })
        {
            _vm.OnTreeNodeSelected(node);
            if (_vm.PreviewCommand.CanExecute(null))
                _vm.PreviewCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>右键菜单：复制当前选中对象名（PG 为 schema.name）。</summary>
    private void OnCopyObjectName(object sender, RoutedEventArgs e)
    {
        var name = _vm.SelectedTable?.Display;
        if (!string.IsNullOrEmpty(name))
            SafeSetClipboard(name);
    }

    private void OnKeyDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListView { SelectedItem: RedisKeyRow row } &&
            _vm.UseKeyCommand.CanExecute(row))
            _vm.UseKeyCommand.Execute(row);
    }

    /// <summary>右键先选中行，保证右键菜单作用在点中的那一行。</summary>
    private void OnRedisKeyRightDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<System.Windows.Controls.ListViewItem>(e.OriginalSource as DependencyObject) is { } item)
            item.IsSelected = true;
    }

    /// <summary>键列表里 Esc 停止扫描/补全。</summary>
    private void OnRedisKeyListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        if (_vm.CancelCommand.CanExecute(null))
            _vm.CancelCommand.Execute(null);
        e.Handled = true;
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

    private DataGridCellInfo _contextCell;

    /// <summary>右键记录鼠标下的单元格，不立即改选择，避免大结果集重排造成菜单卡顿。</summary>
    private void OnResultRightDown(object sender, MouseButtonEventArgs e)
    {
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        _contextCell = cell is null ? default : new DataGridCellInfo(cell);
    }

    private void OnResultLeftDown(object sender, MouseButtonEventArgs e) => _contextCell = default;

    private DataGridCellInfo CurrentCellInfo()
    {
        if (_contextCell.IsValid)
            return _contextCell;
        var cell = ResultGrid.SelectedCells.FirstOrDefault();
        return cell.IsValid ? cell : ResultGrid.CurrentCell;
    }

    private void OnCopyCell(object sender, RoutedEventArgs e)
    {
        var text = CurrentCellText();
        if (!string.IsNullOrEmpty(text))
            SafeSetClipboard(text);
    }

    private void OnCopyRow(object sender, RoutedEventArgs e)
    {
        if (CurrentResultRow() is { } row)
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
        var cell = CurrentCellInfo();
        var name = GetColumnName(cell.Column) ?? "单元格";
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
        AttachFilterHeader(e.Column, e.PropertyName);
    }

    /// <summary>把当前行复制为 INSERT 语句（表名取对象浏览器选中表，否则占位 table）。</summary>
    private void OnCopyInsert(object sender, RoutedEventArgs e)
    {
        if (CurrentResultRow() is not { } row)
            return;
        var table = row.Row.Table;
        var columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
        var tableName = _vm.SelectedTable is { } selected ? _vm.QuoteTableName(selected) : "table";

        var cols = string.Join(", ", columns.Select(_vm.QuoteIdentifier));
        var vals = string.Join(", ", columns.Select(c => SqlLiteral(row[c])));
        SafeSetClipboard($"INSERT INTO {tableName} ({cols}) VALUES ({vals});");
    }

    private void OnCopyUpdate(object sender, RoutedEventArgs e)
    {
        if (CurrentResultRow() is not { } row)
            return;
        var table = row.Row.Table;
        var columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
        if (columns.Count == 0)
            return;

        var tableName = _vm.SelectedTable is { } selected ? _vm.QuoteTableName(selected) : "table";

        var primaryKeys = _vm.Columns
            .Where(c => c.PrimaryKey && table.Columns.Contains(c.Name))
            .Select(c => c.Name)
            .ToList();
        var note = string.Empty;
        if (primaryKeys.Count == 0)
        {
            primaryKeys.Add(columns[0]);
            note = $"/* 未识别到主键，已使用第一列 {columns[0]} 作为 WHERE 条件，请确认 */\n";
        }

        var sets = columns
            .Where(c => !primaryKeys.Contains(c, StringComparer.OrdinalIgnoreCase))
            .Select(c => $"{_vm.QuoteIdentifier(c)} = {SqlLiteral(row[c])}")
            .ToList();
        if (sets.Count == 0)
            return;

        var where = string.Join(" AND ", primaryKeys.Select(c =>
            $"{_vm.QuoteIdentifier(c)} = {SqlLiteral(row[c])}"));
        SafeSetClipboard($"{note}UPDATE {tableName} SET {string.Join(", ", sets)} WHERE {where};");
    }

    private string SqlLiteral(object? value)
    {
        if (value is null || value == DBNull.Value)
            return "NULL";
        var isMySql = _vm.SelectedDatasource?.Type.Equals("mysql", StringComparison.OrdinalIgnoreCase) == true;
        var isPostgres = _vm.SelectedDatasource?.Type.Equals("postgres", StringComparison.OrdinalIgnoreCase) == true;
        return value switch
        {
            string s => isMySql ? MySqlStringLiteral(s) : isPostgres ? PostgresStringLiteral(s) : "'" + s.Replace("'", "''") + "'",
            byte[] bytes => isMySql
                ? "X'" + Convert.ToHexString(bytes) + "'"
                : isPostgres ? "decode('" + Convert.ToHexString(bytes) + "','hex')" : "X'" + Convert.ToHexString(bytes) + "'",
            bool b => b ? "1" : "0",
            DateTime dt => "'" + dt.ToString("yyyy-MM-dd HH:mm:ss") + "'",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "NULL"
        };
    }

    private static string MySqlStringLiteral(string value)
    {
        var sb = new StringBuilder("'");
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("''"); break;
                case '\0': sb.Append("\\0"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\u001a': sb.Append("\\Z"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.Append('\'').ToString();
    }

    private static string PostgresStringLiteral(string value) =>
        "E'" + value.Replace("\\", "\\\\").Replace("'", "''").Replace("\n", "\\n").Replace("\r", "\\r") + "'";

    private DataRowView? CurrentResultRow()
    {
        var cell = CurrentCellInfo();
        return cell.IsValid && cell.Item is DataRowView row
            ? row
            : ResultGrid.CurrentItem as DataRowView;
    }

    private string? CurrentCellText()
    {
        var cell = CurrentCellInfo();
        if (!cell.IsValid || cell.Item is not DataRowView row || cell.Column is null)
            return null;
        var name = GetColumnName(cell.Column);
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
