using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Core.Services.Security;

/// <summary>
/// 当前调用者的数据可见范围（评估报告 P0-2）：Web 强制模式下普通用户只能看到自己的历史/会话，
/// 管理员必须显式 <c>allUsers</c> 才可查看全局；桌面非强制模式保持原有全量行为。
/// 身份与角色只从宿主注入的进程环境变量/审计上下文读取，<b>绝不接受工具参数或前端传入</b>。
/// </summary>
public sealed record CallerScope(bool Enforced, string? UserId, bool IsAdmin)
{
    public static CallerScope Current => new(
        string.Equals(Environment.GetEnvironmentVariable(HostEnv.AuthzMode), HostEnv.Enforce,
            StringComparison.OrdinalIgnoreCase),
        AuditContext.CurrentUserId,
        string.Equals(Environment.GetEnvironmentVariable(HostEnv.AuthzRole), HostEnv.AdminRole,
            StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 命令/SQL 历史的用户过滤：非强制模式返回 <c>UserIds = null</c>（全量）；
    /// 强制模式下普通用户固定为本人，管理员仅在 <paramref name="allUsers"/> 时放开全量。
    /// 无法确定身份时 fail-closed，返回错误而不过滤成“全量”。
    /// </summary>
    public (IReadOnlyCollection<string>? UserIds, string? Error) AuditUserFilter(bool allUsers)
    {
        if (!Enforced)
            return (null, null);
        if (allUsers)
            return IsAdmin ? (null, null) : (null, "仅管理员可查看全部用户历史");
        if (string.IsNullOrWhiteSpace(UserId))
            return (null, "无法确定当前用户，已拒绝跨用户历史查询");
        return (new[] { UserId }, null);
    }

    /// <summary>
    /// 会话列表的归属过滤：返回 <c>null</c> 表示全局（仅非强制模式或管理员显式 <paramref name="allUsers"/>）；
    /// 强制模式普通用户固定为本人，无法确定身份时 fail-closed。
    /// </summary>
    public (string? OwnerUserId, string? Error) SessionOwnerFilter(bool allUsers)
    {
        if (!Enforced)
            return (null, null);
        if (allUsers)
            return IsAdmin ? (null, null) : (null, "仅管理员可查看全部会话");
        if (string.IsNullOrWhiteSpace(UserId))
            return (null, "无法确定当前用户，已拒绝跨用户会话查询");
        return (UserId, null);
    }
}
