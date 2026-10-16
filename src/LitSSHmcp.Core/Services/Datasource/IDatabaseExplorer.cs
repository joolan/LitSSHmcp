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

    /// <summary>表注释（可能为空）。</summary>
    public string? Comment { get; set; }

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

    /// <summary>列注释（可能为空）。</summary>
    public string? Comment { get; set; }
}

/// <summary>外键信息。</summary>
public class DbForeignKeyInfo
{
    public string Name { get; set; } = string.Empty;
    public string[] Columns { get; set; } = Array.Empty<string>();
    public string RefSchema { get; set; } = string.Empty;
    public string RefTable { get; set; } = string.Empty;
    public string[] RefColumns { get; set; } = Array.Empty<string>();

    public string Display =>
        string.Join(", ", Columns) + " → " +
        (string.IsNullOrEmpty(RefSchema) ? RefTable : $"{RefSchema}.{RefTable}") +
        (RefColumns.Length > 0 ? " (" + string.Join(", ", RefColumns) + ")" : string.Empty);
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
    Task<IReadOnlyList<DbForeignKeyInfo>> ListForeignKeysAsync(DataSourceConfig ds, string database, string table, string? schema = null, CancellationToken ct = default);
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
    /// <summary>从 cursor 起 SCAN 一批键（单次往返）；<see cref="RedisScanPage.Cursor"/> 为 "0" 表示已遍历完成。</summary>
    Task<RedisScanPage> ScanKeysPageAsync(DataSourceConfig ds, string match, string cursor, int count, CancellationToken ct = default);

    /// <summary>按 MATCH 模式 SCAN 键名，最多返回 limit 个（分批接口的便捷封装，仅适合小键空间/联调）。</summary>
    Task<IReadOnlyList<string>> ScanKeysAsync(DataSourceConfig ds, string match, int limit, CancellationToken ct = default);

    /// <summary>批量读取键的类型 / 生存时间 / 大小（逐键 TYPE + TTL + MEMORY USAGE，一次会话完成）。</summary>
    Task<IReadOnlyList<RedisKeyMeta>> GetKeysMetaAsync(DataSourceConfig ds, IReadOnlyList<string> keys, CancellationToken ct = default);

    /// <summary>读取单个键的类型、生存时间与值（按类型自动选择，值读取**有界**：超量截断）。</summary>
    Task<RedisKeyValue> GetKeyValueAsync(DataSourceConfig ds, string key, int maxRows = 200, CancellationToken ct = default);
}

/// <summary>一次 SCAN 的返回页（键 + 续扫游标）。</summary>
public sealed class RedisScanPage
{
    public IReadOnlyList<string> Keys { get; init; } = Array.Empty<string>();

    /// <summary>下一批游标；"0" 表示键空间已遍历完成。</summary>
    public string Cursor { get; init; } = "0";
}

/// <summary>单个键的元信息（类型 / 生存时间 / 内存占用）。</summary>
public sealed class RedisKeyMeta
{
    public string Key { get; init; } = string.Empty;

    /// <summary>键类型：string / hash / list / set / zset / stream / none。</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>生存时间（秒）：-1=永不过期，-2=键不存在。</summary>
    public long TtlSeconds { get; init; } = -2;

    /// <summary>内存占用字节；服务端不支持 MEMORY USAGE 或读取失败时为 null。</summary>
    public long? SizeBytes { get; init; }
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

    /// <summary>该键的元素总数（string 为字节数；HLEN/SCARD/ZCARD/LLEN/XLEN）；未知时为 null。</summary>
    public long? TotalCount { get; set; }

    /// <summary>值表（列 + 行）；键不存在或类型不支持时为 null。</summary>
    public DatasourceQueryResult? Data { get; set; }
}
