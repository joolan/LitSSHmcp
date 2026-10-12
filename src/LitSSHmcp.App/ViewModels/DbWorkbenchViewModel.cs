using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Data;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Sql;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

/// <summary>
/// 只读数据库工作台：对象浏览（库 → 表 → 列）+ SQL 查询（仅只读）+ 结果分页/导出 CSV。
/// 复用数据源驱动与安全过滤；不提供任何写操作入口。
/// </summary>
public sealed class DbWorkbenchViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService;
    private readonly IDatasourceDriverRegistry _registry;
    private readonly ISqlFilterService _sqlFilter = new SqlFilterService(new SecurityOptionsProvider());
    private readonly ISecurityOptionsProvider _security = new SecurityOptionsProvider();
    private readonly IAuditLogService _audit = new AuditLogService();
    private bool _auditReady;

    private AppConfig? _config;
    private DataSourceConfig? _selectedDatasource;
    private string? _selectedDatabase;
    private DbTableInfo? _selectedTable;
    private string _sqlText = string.Empty;
    private string _statusMessage = string.Empty;
    private DataView? _results;
    private int _pageSize = 100;
    private int _pageIndex;
    private long? _totalRows;
    private bool _paged;
    private string? _pageBaseSql;
    private bool _busy;
    private bool _isWriteMode;
    private bool _isSnippetPanelOpen;
    private string _keyPattern = "*";
    private string _redisKeyInfo = string.Empty;
    private string? _pendingSelectId;
    private string _objectFilter = string.Empty;
    private string _planText = string.Empty;
    private ResultSetRef? _selectedResultTab;
    private CancellationTokenSource? _queryCts;
    private bool _cancelRequested;
    private QueryTab? _selectedTab;
    private int _tabSeq;

    public DbWorkbenchViewModel(IConfigService? configService = null)
    {
        _configService = configService ?? new ConfigService();
        _registry = AppServiceFactory.CreateDriverRegistry(_configService);

        RefreshCommand = new RelayCommand(_ => _ = LoadAsync());
        LoadTablesCommand = new RelayCommand(_ => _ = LoadSchemaAsync(), _ => SelectedDatasource is not null && !string.IsNullOrWhiteSpace(SelectedDatabase));
        LoadColumnsCommand = new RelayCommand(_ => _ = LoadColumnsAsync(), _ => SelectedTable is not null);
        ExecuteCommand = new RelayCommand(_ => _ = ExecuteAsync(), _ => SelectedDatasource is not null && !Busy);
        CancelCommand = new RelayCommand(_ => CancelQuery(), _ => Busy);
        PreviewCommand = new RelayCommand(_ => _ = PreviewTableAsync(), _ => SelectedTable is not null && !Busy);
        NextPageCommand = new RelayCommand(_ => _ = ChangePageAsync(1), _ => CanPage(1) && !Busy);
        PrevPageCommand = new RelayCommand(_ => _ = ChangePageAsync(-1), _ => CanPage(-1) && !Busy);
        ExportCsvCommand = new RelayCommand(_ => ExportCsv(), _ => Results is not null && !Busy);
        SaveSnippetCommand = new RelayCommand(_ => SaveSnippet(), _ => !string.IsNullOrWhiteSpace(SqlText));
        UseSnippetCommand = new RelayCommand(p => UseSnippet(p as DbSqlSnippet));
        FormatSqlCommand = new RelayCommand(_ => SqlText = SqlScriptFormatter.Format(SqlText), _ => !string.IsNullOrWhiteSpace(SqlText));
        ToggleHistoryPanelCommand = new RelayCommand(_ => IsHistoryPanelOpen = !IsHistoryPanelOpen);
        UseHistoryCommand = new RelayCommand(p => { if (p is string s) { SqlText = s; IsHistoryPanelOpen = false; } });
        ClearHistoryCommand = new RelayCommand(_ => { History.Clear(); PersistHistory(); OnPropertyChanged(nameof(HistoryCount)); OnPropertyChanged(nameof(HistoryButtonLabel)); });        DeleteSnippetCommand = new RelayCommand(p => DeleteSnippet(p as DbSqlSnippet));
        ScanKeysCommand = new RelayCommand(_ => _ = ScanKeysAsync(), _ => IsRedisDb && !Busy);
        UseKeyCommand = new RelayCommand(p => _ = UseKeyAsync(p as string), _ => IsRedisDb && !Busy);
        AddTabCommand = new RelayCommand(_ => AddTab());
        CloseTabCommand = new RelayCommand(p => CloseTab(p as QueryTab));
        SelectTabCommand = new RelayCommand(p => { if (p is QueryTab t) SelectedTab = t; });
        ExplainCommand = new RelayCommand(_ => _ = ExplainAsync(), _ => IsSqlDb && !string.IsNullOrWhiteSpace(SqlText) && !Busy);
        ExportJsonCommand = new RelayCommand(_ => ExportJson(), _ => Results is not null && !Busy);
        GenerateSelectCommand = new RelayCommand(_ => GenerateObjectSql("select"), _ => IsSqlDb && SelectedTable is not null && !Busy);
        GenerateInsertCommand = new RelayCommand(_ => GenerateObjectSql("insert"), _ => IsSqlDb && SelectedTable is not null && !Busy);
        GenerateCountCommand = new RelayCommand(_ => GenerateObjectSql("count"), _ => IsSqlDb && SelectedTable is not null && !Busy);
        GenerateCallCommand = new RelayCommand(_ => GenerateObjectSql("call"), _ => IsSqlDb && SelectedTable is not null && !Busy);
        SelectResultTabCommand = new RelayCommand(p => { if (p is ResultSetRef t) SelectResultTab(t); });
        RefreshObjectsCommand = new RelayCommand(_ => _ = LoadSchemaAsync(), _ => IsSqlDb && !Busy);
        ClearMessagesCommand = new RelayCommand(_ => { Messages.Clear(); RebuildResultTabs(); OnPropertyChanged(nameof(HasMessages)); });
        OpenSqlFileCommand = new RelayCommand(_ => OpenSqlFile());
        SaveSqlFileCommand = new RelayCommand(_ => SaveSqlFile());
        GenerateDdlCommand = new RelayCommand(_ => GenerateDdl(), _ => IsSqlDb && SelectedTable is not null && Columns.Count > 0 && !Busy);
        ClosePlanCommand = new RelayCommand(_ => SelectResultTab(ResultSetTabs.FirstOrDefault(t => t.Kind == ResultTabKind.Result) ?? ResultSetTabs.FirstOrDefault()));

        _tabSeq = 1;
        Tabs.Add(new QueryTab { Title = "查询 1" });
        _selectedTab = Tabs[0];
        _selectedTab.IsSelected = true;

        _ = LoadAsync();
    }

    public ObservableCollection<DataSourceConfig> Datasources { get; } = new();
    public ObservableCollection<string> Databases { get; } = new();
    public ObservableCollection<DbTableInfo> Tables { get; } = new();
    public ObservableCollection<DbColumnInfo> Columns { get; } = new();

    /// <summary>视图 / 存储过程（用于对象树）。</summary>
    public ObservableCollection<DbObjectInfo> Views { get; } = new();
    public ObservableCollection<DbObjectInfo> Procedures { get; } = new();

    /// <summary>左栏对象树（表 / 视图 / 存储过程 → 列 / 索引）。</summary>
    public ObservableCollection<DbTreeNode> ObjectTree { get; } = new();

    /// <summary>当前选中表的索引（仅“表”类别）。</summary>
    public ObservableCollection<DbIndexInfo> Indexes { get; } = new();

    /// <summary>结果集标签（多结果集时显示子标签）。</summary>
    public ObservableCollection<ResultSetRef> ResultSetTabs { get; } = new();

    /// <summary>Redis 模式下 SCAN 出的键名。</summary>
    public ObservableCollection<string> RedisKeys { get; } = new();

    /// <summary>消息/错误面板（失败、警告等；供结果区“消息”标签）。</summary>
    public ObservableCollection<string> Messages { get; } = new();

    public bool HasMessages => Messages.Count > 0;

    /// <summary>查询标签（每个标签独立的 SQL/结果/分页/写模式）。</summary>
    public ObservableCollection<QueryTab> Tabs { get; } = new();

    /// <summary>当前活动标签；切换时在标签间存取 SQL/结果状态。</summary>
    public QueryTab? SelectedTab
    {
        get => _selectedTab;
        set => SelectTab(value);
    }

    /// <summary>当前「数据源 · 库」下的 SQL 片段。</summary>
    public ObservableCollection<DbSqlSnippet> Snippets { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand LoadTablesCommand { get; }
    public ICommand LoadColumnsCommand { get; }
    public ICommand ExecuteCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand PrevPageCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand SaveSnippetCommand { get; }
    public ICommand UseSnippetCommand { get; }
    public ICommand DeleteSnippetCommand { get; }
    public ICommand FormatSqlCommand { get; }
    public ICommand ToggleHistoryPanelCommand { get; }
    public ICommand UseHistoryCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand ScanKeysCommand { get; }
    public ICommand UseKeyCommand { get; }
    public ICommand AddTabCommand { get; }
    public ICommand CloseTabCommand { get; }
    public ICommand SelectTabCommand { get; }
    public ICommand ExplainCommand { get; }
    public ICommand ExportJsonCommand { get; }
    public ICommand GenerateSelectCommand { get; }
    public ICommand GenerateInsertCommand { get; }
    public ICommand GenerateCountCommand { get; }
    public ICommand GenerateCallCommand { get; }
    public ICommand SelectResultTabCommand { get; }
    public ICommand RefreshObjectsCommand { get; }
    public ICommand ClearMessagesCommand { get; }
    public ICommand OpenSqlFileCommand { get; }
    public ICommand SaveSqlFileCommand { get; }
    public ICommand GenerateDdlCommand { get; }
    public ICommand ClosePlanCommand { get; }

    /// <summary>是否有可用片段（供面板提示）。</summary>
    public bool HasSnippets => Snippets.Count > 0;

    /// <summary>片段数量（供按钮文案）。</summary>
    public int SnippetCount => Snippets.Count;

    /// <summary>片段下拉标题（数据源 · 库）。</summary>
    public string SnippetHeader =>
        $"{EditorLabel} 片段（{SelectedDatasource?.Name ?? "-"} · {(string.IsNullOrWhiteSpace(SelectedDatabase) ? "默认库" : SelectedDatabase)}）";

    /// <summary>编辑器标题：SQL 数据源为 "SQL"，Redis 为 "命令"。</summary>
    public string EditorLabel => IsRedisDb ? "命令" : "SQL";

    /// <summary>片段按钮文案。</summary>
    public string SnippetButtonLabel => IsRedisDb ? $"命令片段 ({SnippetCount})" : $"SQL片段 ({SnippetCount})";

    /// <summary>收藏按钮文案。</summary>
    public string SaveSnippetLabel => IsRedisDb ? "★ 收藏当前命令" : "★ 收藏当前 SQL";

    /// <summary>写入模式告警文案（SQL / Redis 不同）。</summary>
    public string WriteBannerText => IsRedisDb
        ? "⚠ 写入模式：命令将直接修改 Redis 数据。FLUSHALL/SHUTDOWN/REPLICAOF 等危险命令会被硬拒绝；执行前会弹窗确认。"
        : "⚠ 写入模式：SQL 将直接修改数据库且不可撤销。无 WHERE 的 UPDATE/DELETE、DROP/TRUNCATE/GRANT 等会被硬拦截；其余敏感语句执行前会弹窗确认。";

    /// <summary>片段下拉是否展开。</summary>
    public bool IsSnippetPanelOpen { get => _isSnippetPanelOpen; set => Set(ref _isSnippetPanelOpen, value); }

    /// <summary>查询历史（最近使用在前，最多 200 条，持久化到本机）。</summary>
    public ObservableCollection<string> History { get; } = new();

    public int HistoryCount => History.Count;
    public string HistoryButtonLabel => $"历史 ({HistoryCount})";

    private bool _isHistoryPanelOpen;
    public bool IsHistoryPanelOpen { get => _isHistoryPanelOpen; set => Set(ref _isHistoryPanelOpen, value); }

    /// <summary>写入前的预览确认回调（由窗口提供，返回 true=执行）。为 null 时用 MessageBox 兜底。</summary>
    public Func<WritePreviewInfo, bool>? ConfirmWrite { get; set; }

    public DataSourceConfig? SelectedDatasource
    {
        get => _selectedDatasource;
        set
        {
            if (!Set(ref _selectedDatasource, value)) return;
            Databases.Clear();
            Tables.Clear();
            Columns.Clear();
            RedisKeys.Clear();
            RedisKeyInfo = string.Empty;
            Results = null;
            if (_isWriteMode && value?.ReadOnly == true)
            {
                _isWriteMode = false;
                OnPropertyChanged(nameof(IsWriteMode));
                OnPropertyChanged(nameof(ShowWriteBanner));
                OnPropertyChanged(nameof(ShowWriteExecute));
                OnPropertyChanged(nameof(ShowReadExecute));
                OnPropertyChanged(nameof(SqlHint));
            }
            OnPropertyChanged(nameof(CanWrite));
            OnPropertyChanged(nameof(ModeTitle));
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(SqlHint));
            OnPropertyChanged(nameof(IsRedisDb));
            OnPropertyChanged(nameof(IsSqlDb));
            OnPropertyChanged(nameof(EditorLabel));
            OnPropertyChanged(nameof(SaveSnippetLabel));
            OnPropertyChanged(nameof(WriteBannerText));
            LoadSnippets();
            if (IsRedisDb)
                _ = ScanKeysAsync();
            else
                _ = LoadDatabasesAsync();
            RaiseCanExecute();
        }
    }

    /// <summary>当前数据源是否允许写入（数据源配置为只读时不允许）。</summary>
    public bool CanWrite => SelectedDatasource is { ReadOnly: false };

    /// <summary>当前数据源是否为 Redis。</summary>
    public bool IsRedisDb => SelectedDatasource?.Type.Equals("redis", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>当前数据源是否为 SQL（MySQL / PostgreSQL）。</summary>
    public bool IsSqlDb => SelectedDatasource is not null && !IsRedisDb;

    /// <summary>Redis 键扫描的 MATCH 模式。</summary>
    public string KeyPattern { get => _keyPattern; set => Set(ref _keyPattern, value); }

    /// <summary>当前查看的 Redis 键信息（类型 / 生存时间）。</summary>
    public string RedisKeyInfo
    {
        get => _redisKeyInfo;
        set { if (Set(ref _redisKeyInfo, value)) OnPropertyChanged(nameof(HasRedisKeyInfo)); }
    }

    public bool HasRedisKeyInfo => !string.IsNullOrEmpty(_redisKeyInfo);

    /// <summary>写入模式开关：默认只读；只读数据源不允许打开。</summary>
    public bool IsWriteMode
    {
        get => _isWriteMode;
        set
        {
            if (value && !CanWrite)
            {
                StatusMessage = "当前数据源已配置为只读，不能切换到写入模式。";
                return;
            }
            if (!Set(ref _isWriteMode, value)) return;
            Results = null;
            OnPropertyChanged(nameof(ModeTitle));
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(SqlHint));
            OnPropertyChanged(nameof(ShowWriteBanner));
            OnPropertyChanged(nameof(ShowWriteExecute));
            OnPropertyChanged(nameof(ShowReadExecute));
            StatusMessage = value
                ? "已切换到写入模式：SQL 将直接修改数据库。危险语句会被拦截，敏感语句会要求确认。"
                : "已切换到只读模式。";
        }
    }

    public bool ShowWriteBanner => _isWriteMode;
    public bool ShowWriteExecute => _isWriteMode;
    public bool ShowReadExecute => !_isWriteMode;

    public string ModeTitle => _isWriteMode ? "数据库工作台（写入）" : "数据库工作台（只读）";

    /// <summary>窗口标题：数据库工作台 · 数据源 · 库（·写入）。</summary>
    public string WindowTitle =>
        "数据库工作台"
        + (SelectedDatasource is null ? string.Empty : $" · {SelectedDatasource.Name}")
        + (IsSqlDb && !string.IsNullOrWhiteSpace(SelectedDatabase) ? $" · {SelectedDatabase}" : string.Empty)
        + (_isWriteMode ? "（写入）" : string.Empty);

    public string SqlHint => IsRedisDb
        ? (_isWriteMode
            ? "（Redis 命令写入：如 SET / DEL / EXPIRE；危险命令会被拦截）"
            : "（Redis 只读命令：如 GET / HGETALL / SCAN / INFO）")
        : (_isWriteMode
            ? "（写入：UPDATE / INSERT / DELETE / DDL；无 WHERE 的 UPDATE/DELETE、DROP 等会被拦截）"
            : "（只读：SELECT / SHOW / EXPLAIN / DESC / WITH）");

    public string? SelectedDatabase
    {
        get => _selectedDatabase;
        set { if (Set(ref _selectedDatabase, value)) { LoadSnippets(); _ = LoadSchemaAsync(); RaiseCanExecute(); OnPropertyChanged(nameof(WindowTitle)); } }
    }

    public DbTableInfo? SelectedTable
    {
        get => _selectedTable;
        set { if (Set(ref _selectedTable, value)) { _ = LoadColumnsAsync(); _ = LoadIndexesAsync(); RaiseCanExecute(); } }
    }

    /// <summary>对象树过滤关键字（过滤后仅保留匹配对象）。</summary>
    public string ObjectFilter
    {
        get => _objectFilter;
        set { if (Set(ref _objectFilter, value)) RebuildObjectTree(); }
    }

    /// <summary>是否存在多个结果集（显示结果子标签）。</summary>
    public bool HasMultipleResults => ResultSetTabs.Count > 1;

    public string SqlText { get => _sqlText; set { if (Set(ref _sqlText, value)) RaiseCanExecute(); } }

    /// <summary>EXPLAIN 计划的可读文本（PG 为缩进文本计划）。</summary>
    public string PlanText { get => _planText; set => Set(ref _planText, value); }

    /// <summary>结果区当前子标签（结果集 / 消息 / 计划）。</summary>
    public ResultSetRef? SelectedResultTab
    {
        get => _selectedResultTab;
        set => SelectResultTab(value);
    }

    public bool ShowResultGrid => _selectedResultTab is null || _selectedResultTab.Kind == ResultTabKind.Result;
    public bool ShowMessages => _selectedResultTab?.Kind == ResultTabKind.Messages;
    public bool ShowPlan => _selectedResultTab?.Kind == ResultTabKind.Plan;

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (Set(ref _statusMessage, value) && LooksLikeError(value))
                AddMessage(value);
        }
    }

    public DataView? Results
    {
        get => _results;
        set { Set(ref _results, value); OnPropertyChanged(nameof(ShowPaging)); OnPropertyChanged(nameof(PageLabel)); RaiseCanExecute(); }
    }

    public int PageSize
    {
        get => _pageSize;
        set { Set(ref _pageSize, Math.Clamp(value, 1, 10000)); RaiseCanExecute(); }
    }

    public bool Busy { get => _busy; set { Set(ref _busy, value); RaiseCanExecute(); } }

    public long? TotalRows { get => _totalRows; set { if (Set(ref _totalRows, value)) { RaiseCanExecute(); OnPropertyChanged(nameof(PageLabel)); } } }

    /// <summary>是否显示分页控件（仅表预览分页时）。</summary>
    public bool ShowPaging => _paged && Results is not null;

    public string PageLabel =>
        !ShowPaging ? string.Empty
        : TotalRows is long total
            ? $"第 {_pageIndex + 1} 页 · 共 {total} 行"
            : $"第 {_pageIndex + 1} 页";

    // ---------- 数据加载 ----------

    private async Task LoadAsync()
    {
        try
        {
            _config = await _configService.LoadConfigAsync();
            History.Clear();
            foreach (var h in WorkbenchHistoryStore.Load())
                History.Add(h);
            Datasources.Clear();
            foreach (var ds in _config.DataSources)
            {
                if (ds.Type.Equals("mysql", StringComparison.OrdinalIgnoreCase) ||
                    ds.Type.Equals("postgres", StringComparison.OrdinalIgnoreCase) ||
                    ds.Type.Equals("redis", StringComparison.OrdinalIgnoreCase))
                    Datasources.Add(ds);
            }

            // 优先定位到请求打开的数据源（右键进入时指定），否则用第一个
            var initial = _pendingSelectId is null
                ? Datasources.FirstOrDefault()
                : Datasources.FirstOrDefault(d => d.Id == _pendingSelectId) ?? Datasources.FirstOrDefault();
            _pendingSelectId = null;
            SelectedDatasource = initial;

            StatusMessage = Datasources.Count == 0
                ? "没有可用的 MySQL / PostgreSQL 数据源，请先在数据源管理中添加。"
                : $"已加载 {Datasources.Count} 个数据源（只读工作台）";
        }
        catch (Exception ex)
        {
            StatusMessage = "加载失败: " + ex.Message;
        }
    }

    /// <summary>请求打开时定位到指定数据源（先记住 id，待数据源列表加载完成后应用）。</summary>
    public void RequestSelect(string datasourceId)
    {
        var match = Datasources.FirstOrDefault(d => d.Id == datasourceId);
        if (match is not null)
            SelectedDatasource = match;
        else
            _pendingSelectId = datasourceId;
    }

    private async Task LoadDatabasesAsync()
    {
        var ds = SelectedDatasource;
        if (ds is null) return;
        var explorer = _registry.Get(ds.Type) as IDatabaseExplorer;
        if (explorer is null) { StatusMessage = $"{ds.Type} 不支持对象浏览"; return; }

        Busy = true;
        StatusMessage = "读取数据库列表…";
        try
        {
            var dbs = await explorer.ListDatabasesAsync(ds);
            Databases.Clear();
            foreach (var db in dbs)
                Databases.Add(db);
            SelectedDatabase = !string.IsNullOrWhiteSpace(ds.DefaultDatabase) && dbs.Contains(ds.DefaultDatabase!)
                ? ds.DefaultDatabase
                : Databases.FirstOrDefault();
            StatusMessage = $"库: {Databases.Count} 个";
        }
        catch (Exception ex)
        {
            StatusMessage = "读取数据库失败: " + ex.Message;
        }
        finally { Busy = false; }
    }

    /// <summary>加载当前库的 表/视图/存储过程 并重建对象树。</summary>
    private async Task LoadSchemaAsync()
    {
        var ds = SelectedDatasource;
        if (ds is null || string.IsNullOrWhiteSpace(SelectedDatabase)) return;
        var explorer = _registry.Get(ds.Type) as IDatabaseExplorer;
        if (explorer is null) return;

        Busy = true;
        try
        {
            var eff = Effective(ds, SelectedDatabase);
            Tables.Clear();
            foreach (var t in await explorer.ListTablesAsync(eff, SelectedDatabase!))
                Tables.Add(t);

            Views.Clear();
            Procedures.Clear();
            if (_registry.Get(ds.Type) is ISchemaObjectExplorer objExplorer)
            {
                foreach (var v in await objExplorer.ListViewsAsync(eff, SelectedDatabase!))
                    Views.Add(v);
                foreach (var p in await objExplorer.ListProceduresAsync(eff, SelectedDatabase!))
                    Procedures.Add(p);
            }

            RebuildObjectTree();
            StatusMessage = $"表 {Tables.Count} · 视图 {Views.Count} · 存储过程 {Procedures.Count}";
        }
        catch (Exception ex)
        {
            StatusMessage = "读取对象失败: " + ex.Message;
        }
        finally { Busy = false; }
    }

    /// <summary>按当前过滤关键字重建对象树（表/视图/存储过程三类）；保留原有展开/折叠状态，默认展开“表”。</summary>
    private void RebuildObjectTree()
    {
        var expanded = new Dictionary<string, bool>(StringComparer.Ordinal);
        CaptureExpansion(ObjectTree, expanded);

        ObjectTree.Clear();
        var filter = _objectFilter?.Trim() ?? string.Empty;
        bool Match(string name) => filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var tableFolder = new DbTreeNode { Name = $"表 ({Tables.Count})", Key = "folder:tables", Kind = DbTreeNodeKind.Folder, HasChildren = true };
        foreach (var t in Tables.Where(t => Match(t.Display)))
            tableFolder.Children.Add(new DbTreeNode { Name = t.Display, Key = "obj:" + t.Display, Kind = DbTreeNodeKind.Table, HasChildren = true, Tag = t });

        var viewFolder = new DbTreeNode { Name = $"视图 ({Views.Count})", Key = "folder:views", Kind = DbTreeNodeKind.Folder, HasChildren = true };
        foreach (var v in Views.Where(v => Match(v.Display)))
            viewFolder.Children.Add(new DbTreeNode
            {
                Name = v.Display,
                Key = "obj:" + v.Display,
                Kind = DbTreeNodeKind.View,
                HasChildren = true,
                Tag = new DbTableInfo { Schema = v.Schema, Name = v.Name }
            });

        var procFolder = new DbTreeNode { Name = $"存储过程 ({Procedures.Count})", Key = "folder:procs", Kind = DbTreeNodeKind.Folder, HasChildren = true };
        foreach (var p in Procedures.Where(p => Match(p.Display)))
            procFolder.Children.Add(new DbTreeNode
            {
                Name = p.Display,
                Key = "obj:" + p.Display,
                Kind = DbTreeNodeKind.Procedure,
                Tag = new DbObjectInfo { Schema = p.Schema, Name = p.Name }
            });

        if (tableFolder.Children.Count > 0 || filter.Length == 0) ObjectTree.Add(tableFolder);
        if (viewFolder.Children.Count > 0 || filter.Length == 0) ObjectTree.Add(viewFolder);
        if (procFolder.Children.Count > 0 || filter.Length == 0) ObjectTree.Add(procFolder);

        ApplyExpansion(ObjectTree, expanded);
    }

    private static void CaptureExpansion(IEnumerable<DbTreeNode> nodes, Dictionary<string, bool> into)
    {
        foreach (var n in nodes)
        {
            if (!string.IsNullOrEmpty(n.Key))
                into[n.Key] = n.IsExpanded;
            CaptureExpansion(n.Children, into);
        }
    }

    private static void ApplyExpansion(IEnumerable<DbTreeNode> nodes, IReadOnlyDictionary<string, bool> expanded)
    {
        foreach (var n in nodes)
        {
            n.IsExpanded = expanded.TryGetValue(n.Key, out var v) ? v : n.Key == "folder:tables";
            ApplyExpansion(n.Children, expanded);
        }
    }

    /// <summary>对象节点被选中：同步“表/视图”到 SelectedTable（供预览/生成 SQL）。</summary>
    public void OnTreeNodeSelected(DbTreeNode? node)
    {
        if (node?.Tag is DbTableInfo t)
            SelectedTable = t;
    }

    /// <summary>展开对象节点时懒加载其列/索引子节点。</summary>
    public async Task LoadNodeChildrenAsync(DbTreeNode node)
    {
        if (node.Children.Count > 0)
            return;
        if (node.Kind is not (DbTreeNodeKind.Table or DbTreeNodeKind.View))
            return;
        var ds = SelectedDatasource;
        if (node.Tag is not DbTableInfo table || ds is null || string.IsNullOrWhiteSpace(SelectedDatabase))
            return;
        if (_registry.Get(ds.Type) is not IDatabaseExplorer explorer)
            return;

        try
        {
            var cols = await explorer.ListColumnsAsync(Effective(ds, SelectedDatabase), SelectedDatabase!, table.Name, table.Schema);
            var colFolder = new DbTreeNode { Name = $"列 ({cols.Count})", Kind = DbTreeNodeKind.Folder, HasChildren = cols.Count > 0 };
            foreach (var c in cols)
                colFolder.Children.Add(new DbTreeNode
                {
                    Name = c.Name + " : " + c.Type + (c.PrimaryKey ? "  PK" : string.Empty) + (c.Nullable ? string.Empty : "  NOT NULL"),
                    Kind = DbTreeNodeKind.Column
                });
            node.Children.Add(colFolder);

            if (node.Kind == DbTreeNodeKind.Table && _registry.Get(ds.Type) is ISchemaObjectExplorer se)
            {
                var idx = await se.ListIndexesAsync(Effective(ds, SelectedDatabase), SelectedDatabase!, table.Name, table.Schema);
                var idxFolder = new DbTreeNode { Name = $"索引 ({idx.Count})", Kind = DbTreeNodeKind.Folder, HasChildren = idx.Count > 0 };
                foreach (var i in idx)
                    idxFolder.Children.Add(new DbTreeNode { Name = i.Display, Kind = DbTreeNodeKind.Index });
                node.Children.Add(idxFolder);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "读取子节点失败: " + ex.Message;
        }
    }

    private async Task LoadIndexesAsync()
    {
        Indexes.Clear();
        var ds = SelectedDatasource;
        var table = SelectedTable;
        if (ds is null || table is null || string.IsNullOrWhiteSpace(SelectedDatabase))
            return;
        if (_registry.Get(ds.Type) is not ISchemaObjectExplorer explorer)
            return;
        try
        {
            var idx = await explorer.ListIndexesAsync(Effective(ds, SelectedDatabase), SelectedDatabase!, table.Name, table.Schema);
            foreach (var i in idx)
                Indexes.Add(i);
        }
        catch (Exception ex)
        {
            StatusMessage = "读取索引失败: " + ex.Message;
        }
    }

    /// <summary>按选中对象生成 SQL（select / count / insert / call）。</summary>
    private void GenerateObjectSql(string kind)
    {
        var ds = SelectedDatasource;
        var table = SelectedTable;
        if (ds is null || table is null) return;
        var name = QuoteObject(ds, table);
        SqlText = kind switch
        {
            "count" => $"SELECT COUNT(*) FROM {name}",
            "call" => $"CALL {name}()",
            "insert" when Columns.Count > 0 =>
                $"INSERT INTO {name} ({string.Join(", ", Columns.Select(c => QuoteIdent(ds, c.Name)))}) " +
                $"VALUES ({ParamPlaceholders(ds, Columns.Count)})",
            _ => $"SELECT * FROM {name} LIMIT {PageSize}"
        };
    }

    private static string QuoteObject(DataSourceConfig ds, DbTableInfo table)
    {
        if (ds.Type.Equals("mysql", StringComparison.OrdinalIgnoreCase))
            return "`" + table.Name.Replace("`", "``") + "`";
        var schema = string.IsNullOrEmpty(table.Schema) ? "public" : table.Schema;
        return "\"" + schema.Replace("\"", "\"\"") + "\".\"" + table.Name.Replace("\"", "\"\"") + "\"";
    }

    private static string QuoteIdent(DataSourceConfig ds, string ident)
        => ds.Type.Equals("mysql", StringComparison.OrdinalIgnoreCase)
            ? "`" + ident.Replace("`", "``") + "`"
            : "\"" + ident.Replace("\"", "\"\"") + "\"";

    private static string ParamPlaceholders(DataSourceConfig ds, int count)
        => ds.Type.Equals("postgres", StringComparison.OrdinalIgnoreCase)
            ? string.Join(", ", Enumerable.Range(1, count).Select(i => "$" + i))
            : string.Join(", ", Enumerable.Range(0, count).Select(_ => "?"));

    /// <summary>切换结果区子标签（结果集 / 消息 / 计划）。</summary>
    private void SelectResultTab(ResultSetRef? tab)
    {
        _selectedResultTab = tab;
        foreach (var t in ResultSetTabs)
            t.IsSelected = ReferenceEquals(t, tab);
        Results = tab is { Kind: ResultTabKind.Result } ? tab.View : null;
        OnPropertyChanged(nameof(SelectedResultTab));
        OnPropertyChanged(nameof(ShowResultGrid));
        OnPropertyChanged(nameof(ShowMessages));
        OnPropertyChanged(nameof(ShowPlan));
        OnPropertyChanged(nameof(PageLabel));
        RaiseCanExecute();
    }

    /// <summary>根据当前结果集/消息/计划重建结果区子标签，并选中第一个结果集（或计划）。</summary>
    private void RebuildResultTabs(bool selectPlan = false)
    {
        var previousKind = _selectedResultTab?.Kind;
        var previousIndex = _selectedResultTab?.Index ?? -1;

        var resultTabs = ResultSetTabs.Where(t => t.Kind == ResultTabKind.Result).ToList();
        ResultSetTabs.Clear();
        foreach (var t in resultTabs)
            ResultSetTabs.Add(t);
        if (Messages.Count > 0)
            ResultSetTabs.Add(new ResultSetRef { Kind = ResultTabKind.Messages, Label = "消息" });
        if (PlanText.Length > 0)
            ResultSetTabs.Add(new ResultSetRef { Kind = ResultTabKind.Plan, Label = "计划" });

        var next = selectPlan
            ? ResultSetTabs.FirstOrDefault(t => t.Kind == ResultTabKind.Plan)
            : ResultSetTabs.FirstOrDefault(t => t.Kind == ResultTabKind.Result);
        next ??= ResultSetTabs.FirstOrDefault(t => t.Kind == previousKind && t.Index == previousIndex);
        next ??= ResultSetTabs.FirstOrDefault();
        SelectResultTab(next);
        OnPropertyChanged(nameof(HasMultipleResults));
    }

    private async Task LoadColumnsAsync()
    {
        var ds = SelectedDatasource;
        var table = SelectedTable;
        if (ds is null || table is null || string.IsNullOrWhiteSpace(SelectedDatabase)) return;
        var explorer = _registry.Get(ds.Type) as IDatabaseExplorer;
        if (explorer is null) return;

        try
        {
            var cols = await explorer.ListColumnsAsync(Effective(ds, SelectedDatabase), SelectedDatabase!, table.Name, table.Schema);
            Columns.Clear();
            foreach (var c in cols)
                Columns.Add(c);
        }
        catch (Exception ex)
        {
            StatusMessage = "读取列失败: " + ex.Message;
        }
    }

    // ---------- 查询 ----------

    /// <summary>SQL 补全候选：当前类别对象名 + 已加载列名（关键字由编辑器补充）。</summary>
    public IReadOnlyList<string> GetCompletionWords()
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tables)
            words.Add(t.Name);
        foreach (var c in Columns)
            words.Add(c.Name);
        return words.ToList();
    }

    private static bool LooksLikeError(string? text) =>
        !string.IsNullOrEmpty(text) &&
        (text.Contains("失败") || text.Contains("错误") || text.Contains("拒绝")
         || text.Contains("异常") || text.Contains("不支持") || text.Contains("超时"));

    /// <summary>记录一条查询历史（去重、置顶、限长、持久化）。</summary>
    private void RecordHistory(string? sql)
    {
        var text = sql?.Trim();
        if (string.IsNullOrEmpty(text))
            return;
        var existing = History.FirstOrDefault(h => string.Equals(h, text, StringComparison.Ordinal));
        if (existing is not null)
            History.Remove(existing);
        History.Insert(0, text);
        while (History.Count > 200)
            History.RemoveAt(History.Count - 1);
        PersistHistory();
        OnPropertyChanged(nameof(HistoryCount));
        OnPropertyChanged(nameof(HistoryButtonLabel));
    }

    private void PersistHistory() => WorkbenchHistoryStore.Save(History);

    private void AddMessage(string message)    {
        Messages.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        while (Messages.Count > 200)
            Messages.RemoveAt(0);
        OnPropertyChanged(nameof(HasMessages));

        // 结果区若无“消息”子标签则补一个（不自动切换，避免打断查看结果）。
        if (ResultSetTabs.All(t => t.Kind != ResultTabKind.Messages))
        {
            ResultSetTabs.Add(new ResultSetRef { Kind = ResultTabKind.Messages, Label = "消息" });
            OnPropertyChanged(nameof(HasMultipleResults));
        }
    }

    private void OpenSqlFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "SQL 文件 (*.sql)|*.sql|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            SqlText = File.ReadAllText(dialog.FileName);
            StatusMessage = $"已打开 {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = "打开失败: " + ex.Message;
        }
    }

    private void SaveSqlFile()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "SQL 文件 (*.sql)|*.sql|所有文件 (*.*)|*.*",
            FileName = "query.sql"
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            File.WriteAllText(dialog.FileName, SqlText ?? string.Empty, new UTF8Encoding(false));
            StatusMessage = $"已保存到 {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = "保存失败: " + ex.Message;
        }
    }

    /// <summary>用已加载的列/主键信息生成 CREATE TABLE SQL。</summary>
    private void GenerateDdl()
    {
        var ds = SelectedDatasource;
        var table = SelectedTable;
        if (ds is null || table is null)
            return;
        if (Columns.Count == 0)
        {
            StatusMessage = "请先选择表并加载列结构。";
            return;
        }

        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(QuoteObject(ds, table)).AppendLine(" (");
        var lines = new List<string>();
        foreach (var c in Columns)
        {
            var line = "  " + QuoteIdent(ds, c.Name) + " " + (string.IsNullOrWhiteSpace(c.Type) ? "?" : c.Type);
            if (!c.Nullable)
                line += " NOT NULL";
            if (!string.IsNullOrWhiteSpace(c.Default))
                line += " DEFAULT " + c.Default;
            lines.Add(line);
        }
        var pks = Columns.Where(c => c.PrimaryKey).ToList();
        if (pks.Count > 0)
            lines.Add("  PRIMARY KEY (" + string.Join(", ", pks.Select(c => QuoteIdent(ds, c.Name))) + ")");
        sb.Append(string.Join(",\n", lines));
        sb.AppendLine();
        sb.AppendLine(");");
        SqlText = sb.ToString();
        StatusMessage = "已生成 CREATE TABLE（可按需调整类型/约束）";
    }

    private async Task ExecuteAsync()
    {
        var ds = SelectedDatasource;
        if (ds is null) return;

        var sql = SqlText?.Trim() ?? string.Empty;
        if (sql.Length == 0) { StatusMessage = "请输入 SQL。"; return; }

        if (IsWriteMode)
            await ExecuteWriteAsync(ds, sql);
        else
            await ExecuteReadAsync(ds, sql);
    }

    private async Task ExecuteReadAsync(DataSourceConfig ds, string sql)
    {
        _paged = false;
        _pageBaseSql = null;
        if (IsRedisDb)
        {
            // Redis：driver 内部按只读白名单校验（非只读命令会被拒绝）
            await RunQueryAsync(ds, sql);
            return;
        }

        var statements = SqlScriptSplitter.Split(sql);
        if (statements.Count == 0)
        {
            StatusMessage = "没有可执行的语句。";
            return;
        }

        // 逐条执行（只读），显示最后一条结果；任一条被拒/取消则停止。
        for (var k = 0; k < statements.Count; k++)
        {
            var stmt = statements[k];
            var text = sql.Substring(stmt.Start, stmt.Length);
            if (_sqlFilter.CheckReadOnly(text) == SqlFilterResult.Blocked)
            {
                StatusMessage = statements.Count > 1
                    ? $"第 {k + 1} 条语句被只读策略拒绝（只读仅允许单条 SELECT/SHOW/EXPLAIN/DESC/WITH）。"
                    : "只读模式仅允许单条 SELECT / SHOW / EXPLAIN / DESC / WITH 查询（写语句请切到写入模式）。";
                return;
            }
            await RunQueryAsync(ds, text);
            if (_cancelRequested)
                return;
        }
    }

    /// <summary>从编辑器执行：有选区执行选区，否则执行光标所在语句（可含多条语句）。</summary>
    public async Task ExecuteFromEditorAsync(int selectionStart, int selectionLength, int caretIndex)
    {
        var ds = SelectedDatasource;
        if (ds is null || Busy)
            return;

        string sql;
        if (selectionLength > 0)
        {
            selectionStart = Math.Clamp(selectionStart, 0, SqlText.Length);
            selectionLength = Math.Clamp(selectionLength, 0, SqlText.Length - selectionStart);
            sql = SqlText.Substring(selectionStart, selectionLength);
        }
        else
        {
            var stmt = SqlScriptSplitter.StatementAt(SqlText, caretIndex);
            sql = stmt is { } s ? SqlText.Substring(s.Start, s.Length) : SqlText;
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            StatusMessage = "未选中或未定位到可执行语句。";
            return;
        }

        if (IsWriteMode)
            await ExecuteWriteAsync(ds, sql);
        else
            await ExecuteReadAsync(ds, sql);
    }

    private void CancelQuery()
    {
        _cancelRequested = true;
        try { _queryCts?.Cancel(); } catch { /* ignore */ }
    }

    // ---------- 查询标签 ----------

    private void SelectTab(QueryTab? tab)
    {
        if (ReferenceEquals(_selectedTab, tab))
            return;
        if (_selectedTab is not null)
            SaveActiveIntoTab(_selectedTab);
        _selectedTab = tab;
        if (tab is not null)
            LoadActiveFromTab(tab);
        foreach (var t in Tabs)
            t.IsSelected = ReferenceEquals(t, _selectedTab);
        OnPropertyChanged(nameof(SelectedTab));
        RaiseCanExecute();
    }

    private void AddTab()
    {
        _tabSeq++;
        var tab = new QueryTab
        {
            Title = $"查询 {_tabSeq}",
            PageSize = _pageSize,
            IsWriteMode = _isWriteMode
        };
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    private void CloseTab(QueryTab? tab)
    {
        if (tab is null || Tabs.Count <= 1)
            return;
        var wasSelected = ReferenceEquals(tab, _selectedTab);
        var idx = Tabs.IndexOf(tab);
        if (wasSelected)
            _selectedTab = null;   // 避免 SelectTab 再往回写
        Tabs.Remove(tab);
        if (wasSelected)
            SelectTab(Tabs[Math.Clamp(idx, 0, Tabs.Count - 1)]);
    }

    private void SaveActiveIntoTab(QueryTab tab)
    {
        tab.SqlText = _sqlText;
        tab.Results = _results;
        tab.PageSize = _pageSize;
        tab.PageIndex = _pageIndex;
        tab.TotalRows = _totalRows;
        tab.Paged = _paged;
        tab.PageBaseSql = _pageBaseSql;
        tab.IsWriteMode = _isWriteMode;
        tab.StatusMessage = _statusMessage;
    }

    private void LoadActiveFromTab(QueryTab tab)
    {
        _sqlText = tab.SqlText;
        _results = tab.Results;
        _pageSize = tab.PageSize;
        _pageIndex = tab.PageIndex;
        _totalRows = tab.TotalRows;
        _paged = tab.Paged;
        _pageBaseSql = tab.PageBaseSql;
        _isWriteMode = tab.IsWriteMode;
        _statusMessage = tab.StatusMessage;

        OnPropertyChanged(nameof(SqlText));
        OnPropertyChanged(nameof(Results));
        OnPropertyChanged(nameof(PageSize));
        OnPropertyChanged(nameof(TotalRows));
        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(ShowPaging));
        OnPropertyChanged(nameof(IsWriteMode));
        OnPropertyChanged(nameof(ShowWriteBanner));
        OnPropertyChanged(nameof(ShowWriteExecute));
        OnPropertyChanged(nameof(ShowReadExecute));
        OnPropertyChanged(nameof(ModeTitle));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(SqlHint));
        OnPropertyChanged(nameof(StatusMessage));
    }

    private async Task ExecuteWriteAsync(DataSourceConfig ds, string sql)
    {
        if (ds.ReadOnly)
        {
            StatusMessage = $"数据源 {ds.Name} 已配置为只读，拒绝写入。";
            await AuditAsync(ds, sql, CommandStatus.Blocked, "数据源为只读，拒绝写操作");
            MessageBox.Show(StatusMessage, "写入被拒绝", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (IsRedisDb)
        {
            await ExecuteRedisWriteAsync(ds, sql);
            return;
        }

        var filterResult = _sqlFilter.CheckWrite(sql);
        if (filterResult == SqlFilterResult.Blocked)
        {
            StatusMessage = "该 SQL 被安全策略禁止执行（无 WHERE 的 UPDATE/DELETE、DROP/TRUNCATE/GRANT 等，或只读语句/多语句）。";
            await AuditAsync(ds, sql, CommandStatus.Blocked, "命中SQL禁止规则");
            MessageBox.Show(StatusMessage, "写入被拒绝", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var driver = _registry.Get(ds.Type);
        if (driver is null) { StatusMessage = $"不支持的类型: {ds.Type}"; return; }

        // 执行前预览（影响行数 + 样本行）并确认；受信任数据源(AutoApprove)跳过确认
        if (ds.WriteApproval != WriteApprovalMode.AutoApprove)
        {
            var preview = await BuildPreviewAsync(ds, sql, driver);
            preview.IsSensitive = filterResult == SqlFilterResult.Sensitive;

            var proceed = ConfirmWrite?.Invoke(preview)
                ?? MessageBox.Show(
                    $"确认在数据源「{ds.Name}」上执行以下写操作？\n\n{sql}\n\n{preview.Describe()}",
                    "执行前预览", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

            if (!proceed)
            {
                StatusMessage = "已取消写操作。";
                await AuditAsync(ds, sql, CommandStatus.Rejected, "用户取消写操作");
                return;
            }
        }

        await AuditAsync(ds, sql, CommandStatus.Approved,
            ds.WriteApproval == WriteApprovalMode.AutoApprove ? "数据源策略自动放行(WriteApproval=AutoApprove)" : "用户确认写操作");

        Busy = true;
        StatusMessage = "执行写入…";
        try
        {
            var result = await driver.ExecuteAsync(Effective(ds, SelectedDatabase), sql);
            Results = null;
            TotalRows = null;
            OnPropertyChanged(nameof(PageLabel));
            StatusMessage = result.Success
                ? $"写入成功，影响 {result.RowsAffected} 行，{result.DurationMs:F0} ms"
                : "写入失败: " + result.Error;
            if (result.Success)
                RecordHistory(sql);
            await AuditAsync(ds, sql,
                result.Success ? CommandStatus.Executed : CommandStatus.Failed,
                result.Success ? $"rowsAffected={result.RowsAffected}" : Truncate(result.Error, 500),
                result.RowsAffected, result.DurationMs);

            // 执行结果一律弹窗提示（成功/失败）
            MessageBox.Show(
                result.Success
                    ? $"写入成功。\n\n影响行数：{result.RowsAffected}\n耗时：{result.DurationMs:F0} ms"
                    : $"写入失败：\n\n{result.Error}",
                result.Success ? "执行写入成功" : "执行写入失败",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            StatusMessage = "写入失败: " + ex.Message;
            MessageBox.Show("写入失败：\n\n" + ex.Message, "执行写入失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { Busy = false; }
    }

    /// <summary>Redis 写命令：确认后经 driver.ExecuteAsync 执行（危险命令由 RedisCommandPolicy 拒绝）。</summary>
    private async Task ExecuteRedisWriteAsync(DataSourceConfig ds, string command)
    {
        var driver = _registry.Get(ds.Type);
        if (driver is null) { StatusMessage = $"不支持的类型: {ds.Type}"; return; }

        if (ds.WriteApproval != WriteApprovalMode.AutoApprove)
        {
            var preview = new WritePreviewInfo
            {
                Sql = command,
                Note = "Redis 命令无法预估影响范围，请确认命令正确无误后再执行。"
            };
            var proceed = ConfirmWrite?.Invoke(preview)
                ?? MessageBox.Show($"确认在 Redis「{ds.Name}」上执行以下命令？\n\n{command}",
                    "执行前预览", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            if (!proceed)
            {
                StatusMessage = "已取消写操作。";
                await AuditAsync(ds, command, CommandStatus.Rejected, "用户取消写操作");
                return;
            }
        }

        await AuditAsync(ds, command, CommandStatus.Approved,
            ds.WriteApproval == WriteApprovalMode.AutoApprove ? "数据源策略自动放行(WriteApproval=AutoApprove)" : "用户确认写操作");

        Busy = true;
        StatusMessage = "执行命令…";
        try
        {
            var result = await driver.ExecuteAsync(Effective(ds, SelectedDatabase), command);
            Results = null;
            TotalRows = null;
            OnPropertyChanged(nameof(PageLabel));
            StatusMessage = result.Success
                ? $"命令执行成功，{result.DurationMs:F0} ms"
                : "命令执行失败: " + result.Error;
            await AuditAsync(ds, command,
                result.Success ? CommandStatus.Executed : CommandStatus.Failed,
                result.Success ? "ok" : Truncate(result.Error, 500),
                result.RowsAffected, result.DurationMs);

            MessageBox.Show(
                result.Success ? $"命令执行成功。\n\n耗时：{result.DurationMs:F0} ms" : $"命令执行失败：\n\n{result.Error}",
                result.Success ? "执行成功" : "执行失败",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            StatusMessage = "命令执行失败: " + ex.Message;
            MessageBox.Show("命令执行失败：\n\n" + ex.Message, "执行失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { Busy = false; }
    }

    /// <summary>执行前预览：尽力解析 UPDATE/DELETE 的 WHERE 并统计影响行数 + 样本行。</summary>
    private async Task<WritePreviewInfo> BuildPreviewAsync(DataSourceConfig ds, string sql, IDatasourceDriver driver)
    {
        var info = new WritePreviewInfo { Sql = sql };
        if (!TryBuildPreview(sql, out var countSql, out var sampleSql))
        {
            info.Note = "该语句不支持影响行数预览，请仔细核对后再执行。";
            return info;
        }

        try
        {
            var effective = Effective(ds, SelectedDatabase);
            var count = await driver.QueryAsync(effective, countSql!, 1);
            if (count.Success && count.Rows.Count > 0 &&
                count.Rows[0].TryGetValue("cnt", out var value) && value is not null)
                info.AffectedRows = Convert.ToInt64(value);

            var sample = await driver.QueryAsync(effective, sampleSql!, 100);
            if (sample.Success)
                info.Sample = BuildView(sample);
        }
        catch
        {
            info.Note = "预览执行失败（可能语法或权限限制），请仔细核对后再执行。";
        }
        return info;
    }

    /// <summary>把 UPDATE/DELETE 语句转换成预览 SELECT（count + 样本）；解析失败返回 false。</summary>
    public static bool TryBuildPreview(string sql, out string? countSql, out string? sampleSql)
    {
        countSql = null;
        sampleSql = null;

        var text = sql.Trim().TrimEnd(';').Trim();
        string? table = null;
        string? where = null;

        var update = System.Text.RegularExpressions.Regex.Match(text,
            @"^UPDATE\s+(?<table>[^\s]+)\s+SET\s+(?<rest>.+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        var delete = System.Text.RegularExpressions.Regex.Match(text,
            @"^DELETE\s+FROM\s+(?<table>[^\s]+)(?<rest>\s+.+)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

        if (update.Success)
        {
            table = update.Groups["table"].Value;
            where = ExtractWhere(update.Groups["rest"].Value);
        }
        else if (delete.Success)
        {
            table = delete.Groups["table"].Value;
            where = ExtractWhere(delete.Groups["rest"].Value);
        }
        else
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(where))
            return false;

        countSql = $"SELECT COUNT(*) AS cnt FROM {table} {where}";
        sampleSql = $"SELECT * FROM {table} {where} LIMIT 100";
        return true;
    }

    private static string? ExtractWhere(string rest)
    {
        var match = System.Text.RegularExpressions.Regex.Match(rest, @"\bWHERE\b(?<cond>.+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        return match.Success ? "WHERE" + match.Groups["cond"].Value : null;
    }

    private async Task AuditAsync(DataSourceConfig ds, string sql, CommandStatus status, string? result,
        long? rows = null, double? durationMs = null)
    {
        try
        {
            if (!_auditReady)
            {
                await _audit.InitializeAsync();
                _auditReady = true;
            }

            var audit = _security.Audit;
            var storedSql = audit.StoreSqlText
                ? (audit.MaskLiterals ? SqlRedactor.Mask(sql) : sql)
                : "[未记录: storeSqlText=false]";

            await _audit.LogSqlAsync(new SqlAuditLog
            {
                DataSourceId = ds.Id,
                DataSourceName = ds.Name,
                Operation = SqlOperation.Execute,
                Sql = storedSql.Length > 4000 ? storedSql[..4000] + "..." : storedSql,
                Status = status,
                Result = result,
                RowsAffected = rows,
                DurationMs = durationMs,
                Category = status is CommandStatus.Executed or CommandStatus.Failed ? AuditCategory.Exec : AuditCategory.Gate,
                Decision = status switch
                {
                    CommandStatus.Approved => "manual-approved",
                    CommandStatus.Rejected => "manual-rejected",
                    CommandStatus.Blocked => "blocked",
                    _ => null
                },
                Tool = "app_workbench"
            });
        }
        catch
        {
            // 审计失败不影响操作
        }
    }

    private static string Truncate(string? text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text ?? string.Empty : text[..max] + "...";

    private async Task PreviewTableAsync()
    {
        var ds = SelectedDatasource;
        var table = SelectedTable;
        if (ds is null || table is null) return;

        var quoted = QuoteObject(ds, table);
        _paged = true;
        _pageBaseSql = $"SELECT * FROM {quoted}";
        _pageIndex = 0;
        TotalRows = null;
        await RunPagedQueryAsync(ds);

        // 估算/统计行数（尽力而为）
        try
        {
            var countSql = $"SELECT COUNT(*) AS cnt FROM {quoted}";
            var count = await _registry.GetRequired(ds.Type).QueryAsync(Effective(ds, SelectedDatabase), countSql, 1);
            if (count.Success && count.Rows.Count > 0 && count.Rows[0].TryGetValue("cnt", out var value) && value is not null)
                TotalRows = Convert.ToInt64(value);
        }
        catch { /* 行数统计失败不影响预览 */ }
    }

    private async Task RunPagedQueryAsync(DataSourceConfig ds)
    {
        if (string.IsNullOrWhiteSpace(_pageBaseSql))
            return;
        var sql = $"{_pageBaseSql} LIMIT {PageSize} OFFSET {(long)_pageIndex * PageSize}";
        SqlText = sql;
        await RunQueryAsync(ds, sql);
    }

    private async Task ChangePageAsync(int delta)
    {
        var ds = SelectedDatasource;
        if (ds is null || !_paged || string.IsNullOrWhiteSpace(_pageBaseSql)) return;

        var target = _pageIndex + delta;
        if (target < 0) return;
        if (TotalRows is long total && (long)target * PageSize >= total)
            return;

        _pageIndex = target;
        await RunPagedQueryAsync(ds);
    }

    private async Task RunQueryAsync(DataSourceConfig ds, string sql)
    {
        var driver = _registry.Get(ds.Type);
        if (driver is null) { StatusMessage = $"不支持的类型: {ds.Type}"; return; }

        Busy = true;
        _cancelRequested = false;
        PlanText = string.Empty;
        StatusMessage = "查询中…";
        var cts = new CancellationTokenSource();
        _queryCts = cts;
        var timeoutSeconds = ds.TimeoutSeconds is > 0 ? ds.TimeoutSeconds!.Value : 0;
        if (timeoutSeconds > 0)
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var maxRows = Math.Max(PageSize, 1000);
            var sets = await driver.QueryMultiAsync(Effective(ds, SelectedDatabase), sql, maxRows, cts.Token);
            var failure = sets.FirstOrDefault(s => !s.Success);
            if (failure is not null)
            {
                StatusMessage = "查询失败: " + failure.Error;
                Results = null;
                ResultSetTabs.Clear();
                RebuildResultTabs();
                return;
            }

            ResultSetTabs.Clear();
            for (var i = 0; i < sets.Count; i++)
                ResultSetTabs.Add(new ResultSetRef
                {
                    Index = i,
                    Label = sets.Count > 1 ? $"结果 {i + 1}" : "结果",
                    View = BuildView(sets[i]),
                    Kind = ResultTabKind.Result
                });
            RebuildResultTabs();

            var totalRows = sets.Sum(s => s.RowCount);
            var truncated = sets.Any(s => s.Truncated);
            var ms = sets.Count > 0 ? sets[0].DurationMs : 0;
            StatusMessage = sets.Count > 1
                ? $"返回 {sets.Count} 个结果集，共 {totalRows} 行，{ms:F0} ms{(truncated ? "（已截断）" : string.Empty)}"
                : $"返回 {totalRows} 行，{ms:F0} ms{(truncated ? "（已截断）" : string.Empty)}";
            if (!_paged)
                RecordHistory(sql);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = _cancelRequested
                ? "查询已取消"
                : timeoutSeconds > 0 ? $"查询超时（> {timeoutSeconds}s）已取消" : "查询已取消";
            Results = null;
        }
        catch (Exception ex)
        {
            StatusMessage = "查询失败: " + ex.Message;
            Results = null;
        }
        finally
        {
            Busy = false;
            if (ReferenceEquals(_queryCts, cts))
                _queryCts = null;
            cts.Dispose();
        }
    }

    private static DataView BuildView(DatasourceQueryResult result)
    {
        var table = new DataTable();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columns = new List<string>();
        foreach (var col in result.Columns)
        {
            var name = col;
            var i = 1;
            while (!names.Add(name))
                name = $"{col}_{i++}";
            columns.Add(name);
            table.Columns.Add(name, typeof(object));
        }

        foreach (var row in result.Rows)
        {
            var dr = table.NewRow();
            for (var i = 0; i < result.Columns.Length && i < columns.Count; i++)
                dr[columns[i]] = row.TryGetValue(result.Columns[i], out var v) ? v ?? DBNull.Value : DBNull.Value;
            table.Rows.Add(dr);
        }
        return table.DefaultView;
    }

    // ---------- 导出 ----------

    private void ExportCsv()
    {
        var view = Results;
        var table = view?.Table;
        if (view is null || table is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出查询结果",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"query-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            using var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true));
            var columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
            writer.WriteLine(string.Join(",", columns.Select(Csv)));
            foreach (DataRowView row in view)
            {
                writer.WriteLine(string.Join(",", columns.Select(c => Csv(row[c]?.ToString() ?? string.Empty))));
            }
            StatusMessage = $"已导出 {view.Count} 行到 {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = "导出失败: " + ex.Message;
        }
    }

    private static string Csv(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>导出结果为 JSON（数组，元素为列名→值；NULL 为 null）。</summary>
    private void ExportJson()
    {
        var view = Results;
        var table = view?.Table;
        if (view is null || table is null)
            return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出查询结果",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = $"query-{DateTime.Now:yyyyMMdd-HHmmss}.json"
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
            var rows = new List<Dictionary<string, object?>>();
            foreach (DataRowView row in view)
            {
                var obj = new Dictionary<string, object?>();
                foreach (var c in columns)
                {
                    var v = row[c];
                    obj[c] = v is null || v == DBNull.Value ? null : v;
                }
                rows.Add(obj);
            }

            var json = System.Text.Json.JsonSerializer.Serialize(rows, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            File.WriteAllText(dialog.FileName, json, new UTF8Encoding(false));
            StatusMessage = $"已导出 {rows.Count} 行到 {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = "导出失败: " + ex.Message;
        }
    }

    /// <summary>对当前 SQL 执行 EXPLAIN，并在结果区显示执行计划。</summary>
    private async Task ExplainAsync()
    {
        var ds = SelectedDatasource;
        if (ds is null || !IsSqlDb)
            return;
        var sql = SqlText?.Trim() ?? string.Empty;
        if (sql.Length == 0) { StatusMessage = "请输入 SQL。"; return; }

        var driver = _registry.Get(ds.Type);
        if (driver is null) { StatusMessage = $"不支持的类型: {ds.Type}"; return; }

        _paged = false;
        _pageBaseSql = null;
        Busy = true;
        _cancelRequested = false;
        StatusMessage = "分析执行计划…";
        var cts = new CancellationTokenSource();
        _queryCts = cts;
        var timeoutSeconds = ds.TimeoutSeconds is > 0 ? ds.TimeoutSeconds!.Value : 0;
        if (timeoutSeconds > 0)
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var result = await driver.ExplainAsync(Effective(ds, SelectedDatabase), sql, cts.Token);
            if (!result.Success)
            {
                StatusMessage = "执行计划失败: " + result.Error;
                Results = null;
                return;
            }
            ResultSetTabs.Clear();
            if (result.Columns.Length == 1)
            {
                // 单列文本计划（PG）：以等宽文本面板展示，保留缩进更易读。
                PlanText = string.Join("\n", result.Rows.Select(r =>
                    r.TryGetValue(result.Columns[0], out var v) ? Convert.ToString(v) : string.Empty));
                RebuildResultTabs(selectPlan: true);
            }
            else
            {
                // 多列计划（MySQL）：作为“计划”结果集展示。
                PlanText = string.Empty;
                ResultSetTabs.Add(new ResultSetRef { Index = 0, Label = "计划", View = BuildView(result), Kind = ResultTabKind.Result });
                RebuildResultTabs();
            }

            StatusMessage = $"执行计划（{result.RowCount} 行，{result.DurationMs:F0} ms）";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消";
            Results = null;
        }
        catch (Exception ex)
        {
            StatusMessage = "执行计划失败: " + ex.Message;
            Results = null;
        }
        finally
        {
            Busy = false;
            if (ReferenceEquals(_queryCts, cts))
                _queryCts = null;
            cts.Dispose();
        }
    }

    // ---------- Redis 键浏览 ----------

    private async Task ScanKeysAsync()
    {
        var ds = SelectedDatasource;
        if (ds is null || !IsRedisDb) return;
        var explorer = _registry.Get(ds.Type) as IRedisKeyExplorer;
        if (explorer is null) { StatusMessage = "该数据源不支持键浏览"; return; }

        Busy = true;
        StatusMessage = "扫描键…";
        try
        {
            var keys = await explorer.ScanKeysAsync(ds, KeyPattern, 1000);
            RedisKeys.Clear();
            foreach (var key in keys)
                RedisKeys.Add(key);
            StatusMessage = $"匹配到 {RedisKeys.Count} 个键（模式 {(string.IsNullOrWhiteSpace(KeyPattern) ? "*" : KeyPattern)}）";
        }
        catch (Exception ex)
        {
            StatusMessage = "扫描键失败: " + ex.Message;
        }
        finally { Busy = false; }
    }

    private async Task UseKeyAsync(string? key)
    {
        var ds = SelectedDatasource;
        if (ds is null || string.IsNullOrWhiteSpace(key) || !IsRedisDb) return;
        var explorer = _registry.Get(ds.Type) as IRedisKeyExplorer;
        if (explorer is null) return;

        Busy = true;
        StatusMessage = $"读取键 {key}…";
        try
        {
            var result = await explorer.GetKeyValueAsync(ds, key!, 200);
            if (!result.Success)
            {
                StatusMessage = "读取键失败: " + result.Error;
                Results = null;
                RedisKeyInfo = string.Empty;
                return;
            }

            Results = result.Data is not null ? BuildView(result.Data) : null;
            RedisKeyInfo = $"键：{key}    类型：{result.Type}    生存时间：{FormatTtl(result.TtlSeconds)}";
            OnPropertyChanged(nameof(PageLabel));
            StatusMessage = result.Data is not null
                ? $"{result.Data.RowCount} 项，{result.Data.DurationMs:F0} ms"
                : (result.Error ?? "该键无值可显示");
        }
        catch (Exception ex)
        {
            StatusMessage = "读取键失败: " + ex.Message;
        }
        finally { Busy = false; }
    }

    private static string FormatTtl(long ttlSeconds) => ttlSeconds switch
    {
        -1 => "永不过期",
        -2 => "键不存在",
        _ => $"{ttlSeconds} 秒"
    };

    // ---------- SQL 片段 ----------

    private void LoadSnippets()
    {
        IsSnippetPanelOpen = false;
        Snippets.Clear();
        var ds = SelectedDatasource;
        if (ds is null) { NotifySnippetCount(); return; }

        foreach (var s in DbSqlSnippetStore.Load(ds.Id, SelectedDatabase))
            Snippets.Add(s);
        NotifySnippetCount();
    }

    private void NotifySnippetCount()
    {
        OnPropertyChanged(nameof(HasSnippets));
        OnPropertyChanged(nameof(SnippetCount));
        OnPropertyChanged(nameof(SnippetHeader));
        OnPropertyChanged(nameof(SnippetButtonLabel));
    }

    /// <summary>把当前 SQL 输入框的语句收藏为当前「数据源·库」的片段（去重）。</summary>
    public void SaveSnippet()
    {
        var ds = SelectedDatasource;
        if (ds is null) { StatusMessage = "请先选择数据源。"; return; }

        var sql = SqlText?.Trim();
        if (string.IsNullOrWhiteSpace(sql)) { StatusMessage = "SQL 输入框为空，无法收藏。"; return; }

        if (Snippets.Any(s => string.Equals(s.Sql.Trim(), sql, StringComparison.Ordinal)))
        {
            StatusMessage = "该 SQL 已在片段中。";
            return;
        }

        Snippets.Add(new DbSqlSnippet(sql!));
        DbSqlSnippetStore.Save(ds.Id, SelectedDatabase, Snippets);
        NotifySnippetCount();
        StatusMessage = $"已收藏 SQL 片段（{SelectedDatabase ?? "默认库"}）。";
    }

    /// <summary>把片段追加到 SQL 输入框。</summary>
    public void UseSnippet(DbSqlSnippet? snippet)
    {
        if (snippet is null || string.IsNullOrWhiteSpace(snippet.Sql)) return;

        SqlText = AppendSql(SqlText, snippet.Sql);
        IsSnippetPanelOpen = false;
        StatusMessage = "已追加 SQL 片段。";
    }

    /// <summary>把片段追加到当前 SQL：为空时直接使用，否则换行分隔追加。</summary>
    public static string AppendSql(string? current, string snippet)
        => string.IsNullOrWhiteSpace(current) ? snippet : current.TrimEnd() + Environment.NewLine + snippet;

    /// <summary>删除片段（二次确认）。</summary>
    public void DeleteSnippet(DbSqlSnippet? snippet)
    {
        var ds = SelectedDatasource;
        if (ds is null || snippet is null) return;

        if (MessageBox.Show($"确定删除该 SQL 片段？\n\n{snippet.Display}", "删除片段",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        Snippets.Remove(snippet);
        DbSqlSnippetStore.Save(ds.Id, SelectedDatabase, Snippets);
        NotifySnippetCount();
        StatusMessage = "已删除 SQL 片段。";
    }

    // ---------- 工具 ----------

    private static DataSourceConfig Effective(DataSourceConfig ds, string? database)
    {
        if (string.IsNullOrWhiteSpace(database) || string.Equals(ds.DefaultDatabase, database, StringComparison.Ordinal))
            return ds;

        return new DataSourceConfig
        {
            Id = ds.Id,
            Name = ds.Name,
            Type = ds.Type,
            Host = ds.Host,
            Port = ds.Port,
            Username = ds.Username,
            Password = ds.Password,
            DefaultDatabase = database,
            AccessMode = ds.AccessMode,
            TunnelServerId = ds.TunnelServerId,
            Description = ds.Description,
            MaxRows = ds.MaxRows,
            TimeoutSeconds = ds.TimeoutSeconds,
            ReadOnly = ds.ReadOnly,
            WriteApproval = ds.WriteApproval,
            UsePooling = ds.UsePooling
        };
    }

    private bool CanPage(int delta)
    {
        if (Results is null || !_paged || string.IsNullOrWhiteSpace(_pageBaseSql)) return false;
        if (delta < 0) return _pageIndex > 0;
        return TotalRows is not long total || (long)(_pageIndex + delta) * PageSize < total;
    }

    private void RaiseCanExecute()
    {
        (LoadTablesCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (LoadColumnsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExecuteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExplainCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportJsonCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (GenerateSelectCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (GenerateInsertCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (GenerateCountCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (GenerateDdlCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (GenerateCallCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PreviewCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (NextPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PrevPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportCsvCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveSnippetCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ScanKeysCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (UseKeyCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>写入执行前的预览数据（影响行数 + 样本行），供确认对话框展示。</summary>
public sealed class WritePreviewInfo
{
    public string Sql { get; set; } = string.Empty;

    /// <summary>预计影响行数（无法预估时为 null）。</summary>
    public long? AffectedRows { get; set; }

    /// <summary>受影响样本行（最多 100 行，仅 UPDATE/DELETE 可预览）。</summary>
    public DataView? Sample { get; set; }

    /// <summary>预览说明（不支持/失败时的提示）。</summary>
    public string? Note { get; set; }

    public bool IsSensitive { get; set; }

    public string Describe()
    {
        if (Note is not null)
            return Note;
        var rows = AffectedRows is long n ? $"预计影响 {n} 行。" : "无法预估影响行数。";
        return rows + (Sample is not null ? $"以下为受影响样本（最多 100 行）。" : string.Empty);
    }
}
