using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Security;

public enum SqlFilterResult
{
    Allowed,
    Sensitive,
    Blocked
}

public interface ISqlFilterService
{
    SqlFilterResult CheckReadOnly(string sql);
    SqlFilterResult CheckWrite(string sql);
}

public class SqlFilterService : ISqlFilterService
{
    private readonly ISecurityOptionsProvider _options;

    public SqlFilterService(ISecurityOptionsProvider options)
    {
        _options = options;
    }

    /// <summary>
    /// 只读入口(mysql_query / *_explain)的过滤。
    /// 关键点: 首关键字为只读(含 with / explain)并不等于整条语句只读 ——
    /// 数据修改 CTE(WITH ... DELETE/UPDATE)与 EXPLAIN ANALYZE &lt;DML&gt; 都会真的写库,
    /// 因此这里必须再跑一遍敏感规则, 把它们从"只读通道"里踢出去。
    /// </summary>
    public SqlFilterResult CheckReadOnly(string sql)
    {
        var config = _options.SqlFilter;

        if (SqlFilterConfig.IsMultiStatement(sql))
            return SqlFilterResult.Blocked;

        if (!config.IsReadOnlyStatement(sql))
            return SqlFilterResult.Blocked;

        if (config.IsBlocked(sql))
            return SqlFilterResult.Blocked;

        if (config.IsSensitive(sql))
            return SqlFilterResult.Sensitive;

        return SqlFilterResult.Allowed;
    }

    public SqlFilterResult CheckWrite(string sql)
    {
        var config = _options.SqlFilter;

        if (SqlFilterConfig.IsMultiStatement(sql))
            return SqlFilterResult.Blocked;

        var keyword = SqlFilterConfig.GetFirstKeyword(sql);

        if (keyword is "select" or "show" or "explain" or "desc" or "describe")
            return SqlFilterResult.Blocked;

        if (config.IsBlocked(sql))
            return SqlFilterResult.Blocked;

        if (config.IsSensitive(sql))
            return SqlFilterResult.Sensitive;

        return SqlFilterResult.Allowed;
    }
}
