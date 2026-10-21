using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Services;

/// <summary>
/// 授权矩阵强制过滤器（评估报告 D1 / §3.2 / §10.B.4）：在 MCP 工具调用过滤器层按
/// "用户 → 目标白名单 + 访问级别（read/write/exec）"强制校验——<b>UI 隐藏不算数</b>，
/// 目标级校验先于建 SSH 连接完成（在写互斥/工具体之前拒绝）。
/// <list type="bullet">
/// <item>仅当宿主 spawn 注入 <c>LITSSH_AUTHZ_MODE=enforce</c>（Web 宿主）才生效——
/// 桌面会话不注入 = 仅归因不限权（D1）；身份取自 <c>LITSSH_AUTHZ_USER</c>/归因 Id，绝不来自工具参数（§10.A.5）；</item>
/// <item>目标与所需访问级别来自 <see cref="ToolSecurityCatalog"/>（单一事实源），不再按工具名猜目标；</item>
/// <item>无目标的工具（本会话/元数据类）不受矩阵约束；全局只读工具暂放行，后续按用户裁剪返回内容；</item>
/// <item>拒绝时写 Gate/Blocked 审计（Decision=authorization_denied，带 SessionId+UserId 入哈希链）；</item>
/// <item>矩阵缺省 fail-closed：未命中用户规则 / 目标不在白名单 / 级别不足 / 工具未登记一律拒绝。</item>
/// </list>
/// </summary>
public static class McpAuthorizationFilter
{
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CreateFilter()
    {
        McpRequestFilter<CallToolRequestParams, CallToolResult> filter = next => async (context, ct) =>
        {
            if (!EnforcementEnabled)
                return await next(context, ct).ConfigureAwait(false);

            var tool = context.Params?.Name;
            if (string.IsNullOrEmpty(tool))
                return await next(context, ct).ConfigureAwait(false);

            var services = context.Services;
            var args = context.Params?.Arguments;

            // 配置读取失败时 ConfigService 返回空配置（不抛异常）→ 空矩阵 = 全拒（fail-closed）
            var config = new AppConfig();
            var configService = services?.GetService(typeof(IConfigService)) as IConfigService;
            if (configService is not null)
            {
                try { config = await configService.LoadConfigAsync().ConfigureAwait(false); }
                catch { config = new AppConfig(); }
            }

            var descriptor = ToolSecurityCatalog.Find(tool);
            if (descriptor is null)
            {
                const string unclassified = "(unclassified)";
                await RecordDeniedAsync(services, config, tool, unclassified, "工具未登记安全描述符（默认拒绝）")
                    .ConfigureAwait(false);
                return Denied(tool, unclassified, "工具未登记安全描述符（默认拒绝）");
            }

            var resolution = ToolTargetResolver.Resolve(config, descriptor, args);
            if (resolution.Error is not null)
            {
                var display = resolution.DisplayKey ?? "(none)";
                await RecordDeniedAsync(services, config, tool, display, resolution.Error).ConfigureAwait(false);
                return Denied(tool, display, resolution.Error);
            }

            // 无目标：元数据工具直接放行；可选目标未提供时暂不做目标级校验（P0-B 再按用户裁剪）；
            // 全局只读工具当前放行，后续按用户过滤返回内容。
            if (resolution.TargetKeys.Count == 0)
            {
                if (descriptor.TargetKind is ToolTargetKind.None or ToolTargetKind.Global
                    or ToolTargetKind.OptionalServer or ToolTargetKind.OptionalDatasource)
                    return await next(context, ct).ConfigureAwait(false);

                const string noTarget = "(none)";
                const string noTargetReason = "目标为空（默认拒绝）";
                await RecordDeniedAsync(services, config, tool, noTarget, noTargetReason).ConfigureAwait(false);
                return Denied(tool, noTarget, noTargetReason);
            }

            var sqlFilter = services?.GetService(typeof(ISqlFilterService)) as ISqlFilterService;
            var requiredAccess = ResolveRequiredAccess(descriptor, tool, args, sqlFilter);

            var policy = config.Security?.Authorization ?? new AuthorizationConfig();
            var (allowed, reason) = EvaluateAccess(
                policy,
                Environment.GetEnvironmentVariable(HostEnv.AuthzUser),
                AuditContext.CurrentUserId,
                resolution.TargetKeys,
                requiredAccess);

            if (allowed)
                return await next(context, ct).ConfigureAwait(false);

            var auditTarget = resolution.DisplayKey ?? resolution.TargetKeys[0];
            await RecordDeniedAsync(services, config, tool, auditTarget, reason).ConfigureAwait(false);
            return Denied(tool, auditTarget, reason);
        };

        return filter;
    }

    /// <summary>宿主是否注入了强制模式（Web 宿主启动时设置 <c>LITSSH_AUTHZ_MODE=enforce</c>；桌面不设置）。</summary>
    public static bool EnforcementEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(HostEnv.AuthzMode), HostEnv.Enforce, StringComparison.Ordinal);

    /// <summary>
    /// 解析调用所需的最低访问级别：SQL/Redis 执行按实际语句动态判定，其余按目录静态声明。
    /// shell 命令不再用“首词只读”降级授权，统一要求 exec。
    /// </summary>
    public static ToolAccessLevel ResolveRequiredAccess(
        ToolSecurityDescriptor descriptor, string tool, IDictionary<string, JsonElement>? args, ISqlFilterService? sqlFilter)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (descriptor.IsConditionalWrite)
            return McpWriteGateFilter.IsReadCall(tool, args, sqlFilter) ? ToolAccessLevel.Read : ToolAccessLevel.Write;

        return descriptor.RequiredAccess;
    }

    /// <summary>
    /// 多目标授权：每个目标都必须命中用户规则，且该目标上的最高级别不低于 required。
    /// 同一用户可配置多行 <see cref="AuthorizationUserRule"/> 来表达不同目标的不同级别。
    /// </summary>
    public static (bool Allowed, string? Reason) EvaluateAccess(
        AuthorizationConfig policy,
        string? username,
        string? userId,
        IReadOnlyCollection<string> targetKeys,
        ToolAccessLevel required)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Enabled)
            return (true, null); // 管理员显式关闭（config.json 仅桌面可写，D2）

        var rules = (policy.Users ?? Array.Empty<AuthorizationUserRule>())
            .Where(r => r is not null && MatchesUser(r, username, userId))
            .ToList();
        if (rules.Count == 0)
            return (false, $"用户 {DisplayName(username, userId)} 不在授权矩阵中（默认拒绝）");

        foreach (var targetKey in targetKeys)
        {
            var matched = rules.Where(r => MatchesTarget(r.Targets, targetKey)).ToList();
            if (matched.Count == 0)
                return (false, $"目标 {targetKey} 不在该用户的白名单中");

            var level = matched.Max(r => LevelRank(r.Level));
            if (level < (int)required)
                return (false, $"用户对目标 {targetKey} 的权限为 {LevelLabel(level)}，低于要求的 {AccessLabel(required)}");
        }

        return (true, null);
    }

    /// <summary>矩阵判定（兼容旧单目标/布尔写调用；isWrite=true 按 write 级别校验）。</summary>
    public static (bool Allowed, string? Reason) Evaluate(
        AuthorizationConfig policy, string? username, string? userId, string targetKey, bool isWrite) =>
        EvaluateAccess(
            policy,
            username,
            userId,
            new[] { targetKey },
            isWrite ? ToolAccessLevel.Write : ToolAccessLevel.Read);

    /// <summary>规则用户匹配：精确匹配用户名（忽略大小写）或用户 Id；<c>*</c> 为兜底规则。</summary>
    public static bool MatchesUser(AuthorizationUserRule rule, string? username, string? userId)
    {
        var key = rule.User?.Trim();
        if (string.IsNullOrEmpty(key))
            return false;
        if (key == "*")
            return true;
        if (!string.IsNullOrEmpty(username) && string.Equals(key, username, StringComparison.OrdinalIgnoreCase))
            return true;
        return !string.IsNullOrEmpty(userId) && string.Equals(key, userId, StringComparison.Ordinal);
    }

    /// <summary>
    /// 目标白名单匹配：精确 <c>ssh:id</c>/<c>ds:id</c>，或末位 <c>*</c> 前缀通配
    /// （<c>*</c>=全部目标、<c>ssh:*</c>=全部服务器、<c>ds:*</c>=全部数据源）。
    /// </summary>
    public static bool MatchesTarget(IEnumerable<string>? patterns, string targetKey)
    {
        foreach (var raw in patterns ?? Array.Empty<string>())
        {
            var pattern = raw?.Trim();
            if (string.IsNullOrEmpty(pattern))
                continue;
            if (pattern == targetKey)
                return true;
            if (pattern[^1] == '*' && targetKey.StartsWith(pattern[..^1], StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>操作级别：exec=3、write=2，其余（read/未知/空）=1 —— 未知值按只读处理，宁严勿松。</summary>
    public static int LevelRank(string? level) =>
        level?.Trim().ToLowerInvariant() switch
        {
            "exec" => ExecRank,
            "write" => WriteRank,
            _ => ReadRank
        };

    public const int ReadRank = 1;
    public const int WriteRank = 2;
    public const int ExecRank = 3;

    private static string DisplayName(string? username, string? userId) =>
        !string.IsNullOrWhiteSpace(username) ? username! : (userId is null or "" ? "(未知)" : userId);

    private static string AccessLabel(ToolAccessLevel access) => access switch
    {
        ToolAccessLevel.Exec => "exec（执行）",
        ToolAccessLevel.Write => "write（写）",
        _ => "read（只读）"
    };

    private static string LevelLabel(int level) => level switch
    {
        >= ExecRank => "exec",
        >= WriteRank => "write",
        _ => "read"
    };

    /// <summary>拒绝写入审计：Gate/Blocked + Decision=authorization_denied（SessionId/Tool/UserId 自动带上链）。</summary>
    private static async Task RecordDeniedAsync(
        IServiceProvider? services, AppConfig config, string tool, string targetKey, string? reason)
    {
        try
        {
            var audit = services?.GetService(typeof(IAuditLogService)) as IAuditLogService;
            if (audit is null)
                return;

            var separator = targetKey.IndexOf(':');
            var kind = separator > 0 ? targetKey[..separator] : string.Empty;
            var id = separator > 0 ? targetKey[(separator + 1)..] : targetKey;
            string name;
            if (kind == "ssh")
            {
                var (server, _, _) = ToolSupport.ResolveServer(config, id);
                name = server?.Name ?? targetKey;
            }
            else if (kind == "ds")
            {
                var (datasource, _, _) = ToolSupport.ResolveDatasource(config, id);
                name = datasource?.Name ?? targetKey;
            }
            else
            {
                name = targetKey;
            }

            await audit.LogCommandAsync(new CommandAuditLog
            {
                ServerId = id,
                ServerName = name,
                Command = tool,
                Result = reason,
                Status = CommandStatus.Blocked,
                Category = AuditCategory.Gate,
                Decision = "authorization_denied",
                Tool = tool
            }).ConfigureAwait(false);
        }
        catch
        {
            // 审计失败不改变拒绝结论（与会话过滤器同一容错原则）
        }
    }

    private static CallToolResult Denied(string tool, string targetKey, string? reason) => new()
    {
        IsError = true,
        Content = new List<ContentBlock>
        {
            new TextContentBlock
            {
                Text = $"authorization_denied: {reason}。工具 {tool} 对目标 {targetKey} 的调用被授权矩阵拒绝；" +
                       "授权由管理员在桌面 config.json 的 security.authorization 中配置（用户白名单 + read/write/exec 级别）。"
            }
        }
    };
}
