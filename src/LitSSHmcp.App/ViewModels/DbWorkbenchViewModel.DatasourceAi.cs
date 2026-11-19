using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public sealed partial class DbWorkbenchViewModel
{
    internal IConfigService ConfigService => _configService;
    internal IAuditLogService AuditService => _audit;
    internal ISqlFilterService SqlFilterService => _sqlFilter;
    internal IDatasourceDriverRegistry DriverRegistry => _registry;

    /// <summary>把 AI 生成的 SQL 插入一个新查询标签，不自动执行。</summary>
    public void InsertAiSql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return;
        AddTab();
        SqlText = sql;
        StatusMessage = "AI 生成的 SQL 已插入新查询标签，请确认后手动执行。";
    }

    /// <summary>把 AI 已确认执行的查询结果展示到工作台结果区。</summary>
    public void ShowAiQueryResult(string sql, DatasourceQueryResult result)
    {
        SqlText = sql;
        _paged = false;
        _pageBaseSql = null;
        _pageIndex = 0;
        _totalRows = result.RowCount;
        var view = WorkbenchResultView.Build(result);
        ResultSetTabs.Clear();
        ResultSetTabs.Add(new ResultSetRef
        {
            Index = 0,
            Label = "AI 结果",
            View = view,
            Kind = ResultTabKind.Result
        });
        RebuildResultTabs();
        OnPropertyChanged(nameof(ShowPaging));
        OnPropertyChanged(nameof(PageLabel));
        StatusMessage = result.Success
            ? $"AI 查询完成：{result.RowCount:N0} 行 · {result.DurationMs:F0} ms"
            : "AI 查询失败：" + result.Error;
    }
}
