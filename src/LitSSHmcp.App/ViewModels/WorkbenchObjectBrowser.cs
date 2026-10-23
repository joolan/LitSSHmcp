using System.Collections.ObjectModel;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;

namespace LitSSHmcp.App.ViewModels;

/// <summary>对象树宿主：由 DbWorkbenchViewModel 提供当前数据源/库、忙碌状态与驱动注册表。</summary>
internal interface IWorkbenchObjectBrowserHost
{
    DataSourceConfig? SelectedDatasource { get; }
    string? SelectedDatabase { get; }
    DbTableInfo? SelectedTable { get; set; }
    bool Busy { get; set; }
    string StatusMessage { set; }
    IDatasourceDriverRegistry Registry { get; }
    DataSourceConfig EffectiveForSchema(DataSourceConfig ds);
}

/// <summary>
/// 数据库工作台左侧对象浏览：加载表/视图/存储过程、按关键字过滤重建树、懒加载列/索引/外键子节点。
/// 列/索引集合与 SQL 生成仍留在 ViewModel；本类只负责对象树数据与交互。
/// </summary>
internal sealed class WorkbenchObjectBrowser
{
    private readonly IWorkbenchObjectBrowserHost _host;
    private string _filter = string.Empty;
    private int _matched;
    private int _total;

    public WorkbenchObjectBrowser(IWorkbenchObjectBrowserHost host) => _host = host;

    public ObservableCollection<DbTableInfo> Tables { get; } = new();
    public ObservableCollection<DbObjectInfo> Views { get; } = new();
    public ObservableCollection<DbObjectInfo> Procedures { get; } = new();
    public ObservableCollection<DbTreeNode> ObjectTree { get; } = new();

    public string Filter
    {
        get => _filter;
        set
        {
            if (string.Equals(_filter, value, StringComparison.Ordinal)) return;
            _filter = value;
            RebuildObjectTree();
        }
    }

    public string FilterHint => string.IsNullOrWhiteSpace(_filter)
        ? string.Empty
        : $"匹配 {_matched} / {_total}";

    /// <summary>对象树重建/清空后通知宿主刷新提示。</summary>
    public event Action? Changed;

    public void Clear()
    {
        Tables.Clear();
        Views.Clear();
        Procedures.Clear();
        ObjectTree.Clear();
        _matched = 0;
        _total = 0;
        Changed?.Invoke();
    }

    /// <summary>加载当前库的表/视图/存储过程并重建对象树。</summary>
    public async Task LoadSchemaAsync()
    {
        var ds = _host.SelectedDatasource;
        var database = _host.SelectedDatabase;
        if (ds is null || string.IsNullOrWhiteSpace(database)) return;
        var explorer = _host.Registry.Get(ds.Type) as IDatabaseExplorer;
        if (explorer is null) return;

        _host.Busy = true;
        try
        {
            var eff = _host.EffectiveForSchema(ds);
            Tables.Clear();
            var warnings = new List<string>();

            try
            {
                foreach (var t in await explorer.ListTablesAsync(eff, database))
                    Tables.Add(t);
            }
            catch (Exception ex)
            {
                warnings.Add("表: " + ex.Message);
            }

            Views.Clear();
            Procedures.Clear();
            if (_host.Registry.Get(ds.Type) is ISchemaObjectExplorer objExplorer)
            {
                try
                {
                    foreach (var v in await objExplorer.ListViewsAsync(eff, database))
                        Views.Add(v);
                }
                catch (Exception ex)
                {
                    warnings.Add("视图: " + ex.Message);
                }
                try
                {
                    foreach (var p in await objExplorer.ListProceduresAsync(eff, database))
                        Procedures.Add(p);
                }
                catch (Exception ex)
                {
                    warnings.Add("存储过程: " + ex.Message);
                }
            }

            RebuildObjectTree();
            _host.StatusMessage = warnings.Count == 0
                ? $"表 {Tables.Count} · 视图 {Views.Count} · 存储过程 {Procedures.Count}"
                : $"部分对象读取失败（{string.Join("；", warnings)}）";
        }
        catch (Exception ex)
        {
            _host.StatusMessage = "读取对象失败: " + ex.Message;
        }
        finally { _host.Busy = false; }
    }

    /// <summary>对象节点被选中：同步“表/视图”到宿主 SelectedTable（供预览/生成 SQL）。</summary>
    public void OnTreeNodeSelected(DbTreeNode? node)
    {
        if (node?.Tag is DbTableInfo table)
            _host.SelectedTable = table;
    }

    /// <summary>展开对象节点时懒加载其列/索引/外键子节点。</summary>
    public async Task LoadNodeChildrenAsync(DbTreeNode node)
    {
        if (node.Children.Count > 0)
            return;
        if (node.Kind is not (DbTreeNodeKind.Table or DbTreeNodeKind.View))
            return;
        var ds = _host.SelectedDatasource;
        var database = _host.SelectedDatabase;
        if (node.Tag is not DbTableInfo table || ds is null || string.IsNullOrWhiteSpace(database))
            return;
        if (_host.Registry.Get(ds.Type) is not IDatabaseExplorer explorer)
            return;
        var eff = _host.EffectiveForSchema(ds);

        try
        {
            var cols = await explorer.ListColumnsAsync(eff, database, table.Name, table.Schema);
            var colFolder = new DbTreeNode { Name = $"列 ({cols.Count})", Kind = DbTreeNodeKind.Folder, HasChildren = cols.Count > 0 };
            foreach (var c in cols)
                colFolder.Children.Add(new DbTreeNode
                {
                    Name = c.Name,
                    Detail = "  " + c.Type +
                             (c.PrimaryKey ? "  PK" : string.Empty) +
                             (c.Nullable ? string.Empty : "  NOT NULL"),
                    Tooltip = string.IsNullOrWhiteSpace(c.Comment) ? null : c.Comment.Trim(),
                    Kind = DbTreeNodeKind.Column
                });
            node.Children.Add(colFolder);
        }
        catch (Exception ex)
        {
            _host.StatusMessage = "读取列失败: " + ex.Message;
        }

        if (node.Kind != DbTreeNodeKind.Table || _host.Registry.Get(ds.Type) is not ISchemaObjectExplorer se)
            return;

        try
        {
            var idx = await se.ListIndexesAsync(eff, database, table.Name, table.Schema);
            var idxFolder = new DbTreeNode { Name = $"索引 ({idx.Count})", Kind = DbTreeNodeKind.Folder, HasChildren = idx.Count > 0 };
            foreach (var i in idx)
                idxFolder.Children.Add(new DbTreeNode { Name = i.Display, Kind = DbTreeNodeKind.Index });
            node.Children.Add(idxFolder);
        }
        catch (Exception ex)
        {
            _host.StatusMessage = "读取索引失败: " + ex.Message;
        }

        try
        {
            var fks = await se.ListForeignKeysAsync(eff, database, table.Name, table.Schema);
            var fkFolder = new DbTreeNode { Name = $"外键 ({fks.Count})", Kind = DbTreeNodeKind.Folder, HasChildren = fks.Count > 0 };
            foreach (var f in fks)
                fkFolder.Children.Add(new DbTreeNode { Name = f.Display, Kind = DbTreeNodeKind.ForeignKey });
            node.Children.Add(fkFolder);
        }
        catch (Exception ex)
        {
            _host.StatusMessage = "读取外键失败: " + ex.Message;
        }
    }

    /// <summary>按过滤关键字重建对象树；保留展开状态，默认展开“表”。</summary>
    private void RebuildObjectTree()
    {
        var expanded = new Dictionary<string, bool>(StringComparer.Ordinal);
        CaptureExpansion(ObjectTree, expanded);

        ObjectTree.Clear();
        var filter = _filter?.Trim() ?? string.Empty;
        bool Match(string name) => filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase);

        IEnumerable<T> Rank<T>(IEnumerable<T> source, Func<T, string> name)
            => filter.Length == 0
                ? source
                : source.OrderBy(t => name(t).StartsWith(filter, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(name, StringComparer.OrdinalIgnoreCase);

        var tables = Rank(Tables.Where(t => Match(t.Display)), t => t.Display).ToList();
        var views = Rank(Views.Where(v => Match(v.Display)), v => v.Display).ToList();
        var procs = Rank(Procedures.Where(p => Match(p.Display)), p => p.Display).ToList();

        _matched = tables.Count + views.Count + procs.Count;
        _total = Tables.Count + Views.Count + Procedures.Count;

        var tableFolder = new DbTreeNode { Name = $"表 ({Tables.Count})", Key = "folder:tables", Kind = DbTreeNodeKind.Folder, HasChildren = true };
        foreach (var t in tables)
            tableFolder.Children.Add(new DbTreeNode
            {
                Name = t.Display,
                Tooltip = string.IsNullOrWhiteSpace(t.Comment) ? null : t.Comment.Trim(),
                Key = "obj:" + t.Display,
                Kind = DbTreeNodeKind.Table,
                HasChildren = true,
                Tag = t
            });

        var viewFolder = new DbTreeNode { Name = $"视图 ({Views.Count})", Key = "folder:views", Kind = DbTreeNodeKind.Folder, HasChildren = true };
        foreach (var v in views)
            viewFolder.Children.Add(new DbTreeNode
            {
                Name = v.Display,
                Key = "obj:" + v.Display,
                Kind = DbTreeNodeKind.View,
                HasChildren = true,
                Tag = new DbTableInfo { Schema = v.Schema, Name = v.Name }
            });

        var procFolder = new DbTreeNode { Name = $"存储过程 ({Procedures.Count})", Key = "folder:procs", Kind = DbTreeNodeKind.Folder, HasChildren = true };
        foreach (var p in procs)
            procFolder.Children.Add(new DbTreeNode
            {
                Name = p.Display,
                Key = "obj:" + p.Display,
                Kind = DbTreeNodeKind.Procedure,
                HasChildren = true,
                Tag = new DbObjectInfo { Schema = p.Schema, Name = p.Name }
            });

        if (tableFolder.Children.Count > 0 || filter.Length == 0) ObjectTree.Add(tableFolder);
        if (viewFolder.Children.Count > 0 || filter.Length == 0) ObjectTree.Add(viewFolder);
        if (procFolder.Children.Count > 0 || filter.Length == 0) ObjectTree.Add(procFolder);

        ApplyExpansion(ObjectTree, expanded);
        Changed?.Invoke();
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
}
