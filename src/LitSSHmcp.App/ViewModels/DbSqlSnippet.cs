using System.Text.Json.Serialization;

namespace LitSSHmcp.App.ViewModels;

/// <summary>数据库 SQL 片段（属于某个「数据源 · 库」），用于在工作台快速复用常用语句。</summary>
public sealed class DbSqlSnippet
{
    /// <summary>可选名称；为空时用 SQL 首行预览作为显示名。</summary>
    public string Name { get; set; } = string.Empty;

    public string Sql { get; set; } = string.Empty;

    [JsonIgnore]
    public string Display => string.IsNullOrWhiteSpace(Name) ? Preview(Sql) : Name;

    public DbSqlSnippet() { }

    public DbSqlSnippet(string sql, string? name = null)
    {
        Sql = sql;
        Name = name ?? string.Empty;
    }

    private static string Preview(string sql)
    {
        var line = (sql ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length == 0 ? "(空)" : line.Length > 48 ? line[..48] + "…" : line;
    }
}
