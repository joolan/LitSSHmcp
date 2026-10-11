using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Datasource;

/// <summary>库中的一张表（含可选的估算行数）。</summary>
public class DbTableInfo
{
    /// <summary>PostgreSQL schema；MySQL 为空。</summary>
    public string Schema { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>估算行数（information_schema.TABLE_ROWS，可能不精确；null = 未知）。</summary>
    public long? Rows { get; set; }

    /// <summary>展示名（PG: schema.name；MySQL: name）。</summary>
    public string Display => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
}

public class DbColumnInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool Nullable { get; set; }
    public bool PrimaryKey { get; set; }
    public string? Default { get; set; }
}

/// <summary>库中的一个对象（视图 / 存储过程）。</summary>
public class DbObjectInfo
{
    public string Schema { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Display => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
}

/// <summary>一张表/视图的索引。</summary>
public class DbIndexInfo
{
    public string Name { get; set; } = string.Empty;
    public string[] Columns { get; set; } = Array.Empty<string>();
    public bool Unique { get; set; }
    public bool Primary { get; set; }

    public string Display =>
        (Primary ? "PK " : Unique ? "UQ " : string.Empty) + Name +
        (Columns.Length > 0 ? " (" + string.Join(", ", Columns) + ")" : string.Empty);
}

/// <summary>
/// 可选能力：更丰富的对象浏览（视图 / 存储过程 / 索引）。由 MySQL / PostgreSQL 驱动实现，
/// 不影响 <see cref="IDatabaseExplorer"/> 的既有实现。
/// </summary>
public interface ISchemaObjectExplorer
{
    Task<IReadOnlyList<DbObjectInfo>> ListViewsAsync(DataSourceConfig ds, string database, CancellationToken ct = default);
    Task<IReadOnlyList<DbObjectInfo>> ListProceduresAsync(DataSourceConfig ds, string database, CancellationToken ct = default);
    Task<IReadOnlyList<DbIndexInfo>> ListIndexesAsync(DataSourceConfig ds, string database, string table, string? schema = null, CancellationToken ct = default);
}

/// <summary>
/// 可选能力：为"数据库工作台"提供对象浏览（库/表/列）。MySQL / PostgreSQL 驱动实现；
/// 不影响 <see cref="IDatasourceDriver"/>，Redis 等其它类型自然不具备。
/// </summary>
public interface IDatabaseExplorer
{
    Task<IReadOnlyList<string>> ListDatabasesAsync(DataSourceConfig ds, CancellationToken ct = default);
    Task<IReadOnlyList<DbTableInfo>> ListTablesAsync(DataSourceConfig ds, string database, CancellationToken ct = default);
    Task<IReadOnlyList<DbColumnInfo>> ListColumnsAsync(DataSourceConfig ds, string database, string table, string? schema = null, CancellationToken ct = default);
}

/// <summary>
/// 可选能力：Redis 键浏览（供"数据库工作台"的 Redis 模式：SCAN 列键 + 按类型读取值）。
/// 由 <c>RedisDriver</c> 实现，不影响 <see cref="IDatasourceDriver"/>。
/// </summary>
public interface IRedisKeyExplorer
{
    /// <summary>按 MATCH 模式 SCAN 键名，最多返回 limit 个。</summary>
    Task<IReadOnlyList<string>> ScanKeysAsync(DataSourceConfig ds, string match, int limit, CancellationToken ct = default);

    /// <summary>读取单个键的类型、生存时间与值（按类型自动选择 GET/HGETALL/LRANGE/SMEMBERS/ZRANGE/XRANGE）。</summary>
    Task<RedisKeyValue> GetKeyValueAsync(DataSourceConfig ds, string key, int maxRows = 200, CancellationToken ct = default);
}

/// <summary>单个 Redis 键的读取结果（类型 / 生存时间 / 值表）。</summary>
public sealed class RedisKeyValue
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>键类型：string / hash / list / set / zset / stream / none / 其它。</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>生存时间（秒）：-1=永不过期，-2=键不存在，>=0=剩余秒数。</summary>
    public long TtlSeconds { get; set; } = -2;

    /// <summary>值表（列 + 行）；键不存在或类型不支持时为 null。</summary>
    public DatasourceQueryResult? Data { get; set; }
}
