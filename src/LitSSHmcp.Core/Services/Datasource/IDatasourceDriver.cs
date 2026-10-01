using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Datasource;

public class DatasourceTestResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Version { get; set; }
    public double DurationMs { get; set; }
    public string AccessMode { get; set; } = "direct";
    public string? ViaTunnelServer { get; set; }
    public string? Error { get; set; }
}

public class DatasourceQueryResult
{
    public bool Success { get; set; }
    public string[] Columns { get; set; } = Array.Empty<string>();
    public List<Dictionary<string, object?>> Rows { get; set; } = new();
    public long RowCount { get; set; }
    public bool Truncated { get; set; }
    public double DurationMs { get; set; }
    public string? Error { get; set; }
}

public class DatasourceExecuteResult
{
    public bool Success { get; set; }
    public long RowsAffected { get; set; }
    public double DurationMs { get; set; }
    public string? Error { get; set; }
}

public class DatasourceDiagnosticsResult
{
    public bool Success { get; set; }
    public string? Summary { get; set; }
    public Dictionary<string, object?> Data { get; set; } = new();
    public double DurationMs { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// 数据源驱动抽象。新增 Redis/PostgreSQL 等只需实现该接口并在注册表登记。
/// </summary>
public interface IDatasourceDriver
{
    string Type { get; }
    Task<DatasourceTestResult> TestAsync(DataSourceConfig ds, CancellationToken ct = default);
    Task<DatasourceQueryResult> QueryAsync(DataSourceConfig ds, string sql, int maxRows, CancellationToken ct = default);
    Task<DatasourceExecuteResult> ExecuteAsync(DataSourceConfig ds, string sql, CancellationToken ct = default);
    Task<DatasourceQueryResult> ExplainAsync(DataSourceConfig ds, string sql, CancellationToken ct = default);
    Task<DatasourceDiagnosticsResult> DiagnoseAsync(DataSourceConfig ds, CancellationToken ct = default);
}

public interface IDatasourceDriverRegistry
{
    IDatasourceDriver? Get(string type);
    IDatasourceDriver GetRequired(string type);
}

public class DatasourceDriverRegistry : IDatasourceDriverRegistry
{
    private readonly Dictionary<string, IDatasourceDriver> _drivers;

    /// <summary>
    /// 按每个驱动自报的 <see cref="IDatasourceDriver.Type"/> 建立 "type -&gt; driver" 索引。
    /// 新增数据源类型只需实现 <see cref="IDatasourceDriver"/> 并注册到 DI，无需改动本类。
    /// </summary>
    public DatasourceDriverRegistry(IEnumerable<IDatasourceDriver> drivers)
    {
        _drivers = drivers.ToDictionary(d => d.Type, StringComparer.OrdinalIgnoreCase);
    }

    public IDatasourceDriver? Get(string type) =>
        _drivers.TryGetValue(type, out var driver) ? driver : null;

    public IDatasourceDriver GetRequired(string type) =>
        Get(type) ?? throw new NotSupportedException(
            $"数据源类型 '{type}' 暂不支持。当前支持: {string.Join(", ", _drivers.Keys)}");
}
