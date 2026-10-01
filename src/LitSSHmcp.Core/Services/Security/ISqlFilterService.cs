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

    public SqlFilterResult CheckReadOnly(string sql)
    {
        var config = _options.SqlFilter;

        if (SqlFilterConfig.IsMultiStatement(sql))
            return SqlFilterResult.Blocked;

        if (!config.IsReadOnlyStatement(sql))
            return SqlFilterResult.Blocked;

        if (config.IsBlocked(sql))
            return SqlFilterResult.Blocked;

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
