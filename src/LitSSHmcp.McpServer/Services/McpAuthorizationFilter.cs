using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Services;

/// <summary>
/// 授权矩阵强制过滤器（评估报告 D1 / §3.2 / §10.B.4）：在 MCP 工具调用过滤器层按
/// "用户 → 目标白名单 + 操作级别（read/exec）"强制校验——<b>UI 隐藏不算数</b>，
/// 目标级校验先于建 SSH 连接完成（在写互斥/工具体之前拒绝）。
/// <list type="bullet">
/// <item>仅当宿主 spawn 注入 <c>LITSSH_AUTHZ_MODE=enforce</c>（Web 宿主）才生效——
/// 桌面会话不注入 = 仅归因不限权（D1）；身份取自 <c>LITSSH_AUTHZ_USER</c>/归因 Id，绝不来自工具参数（§10.A.5）；</item>
/// <item>写/读分类复用 <see cref="McpWriteGateFilter"/> 的判定（目标级写语义的单一事实源，M2-1）；</item>
/// <item>无目标的工具（本会话/元数据类）不受矩阵约束；无法归一化的目标由工具自身拒绝；</item>
/// <item>拒绝时写 Gate/Blocked 审计（Decision=authorization_denied，带 SessionId+UserId 入哈希链）；</item>
/// <item>矩阵缺省 fail-closed：未命中用户规则 / 目标不在白名单 / 只读级别遇写调用一律拒绝。</item>
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

            var targetKey = McpWriteGateFilter.ResolveTargetKey(config, tool!, args);
            if (targetKey is null)
                return await next(context, ct).ConfigureAwait(false); // 无目标/无法归一 → 工具自身校验

            var sqlFilter = services?.GetService(typeof(ISqlFilterService)) as ISqlFilterService;
            var isWrite = McpWriteGateFilter.RequiresWriteGate(tool!) &&
                          !McpWriteGateFilter.IsReadCall(tool!, args, sqlFilter);

            var policy = config.Security?.Authorization ?? new AuthorizationConfig();
            var (allowed, reason) = Evaluate(
                policy,
                Environment.GetEnvironmentVariable(HostEnv.AuthzUser),
                AuditContext.CurrentUserId,
                targetKey,
                isWrite);

            if (allowed)
                return await next(context, ct).ConfigureAwait(false);

            await RecordDeniedAsync(services, config, tool!, targetKey, reason).ConfigureAwait(false);
            return Denied(tool!, targetKey, reason);
        };

        return filter;
    }

    /// <summary>宿主是否注入了强制模式（Web 宿主启动时设置 <c>LITSSH_AUTHZ_MODE=enforce</c>；桌面不设置）。</summary>
    public static bool EnforcementEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(HostEnv.AuthzMode), HostEnv.Enforce, StringComparison.Ordinal);

    /// <summary>矩阵判定（纯逻辑，供单测直调）。返回是否放行；拒绝时带原因。</summary>
    public static (bool Allowed, string? Reason) Evaluate(
        AuthorizationConfig policy, string? username, string? userId, string targetKey, bool isWrite)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Enabled)
            return (true, null); // 管理员显式关闭（config.json 仅桌面可写，D2）

        var rules = (policy.Users ?? Array.Empty<AuthorizationUserRule>())
            .Where(r => r is not null && MatchesUser(r, username, userId))
            .ToList();
        if (rules.Count == 0)
            return (false, $"用户 {DisplayName(username, userId)} 不在授权矩阵中（默认拒绝）");

        var matched = rules.Where(r => MatchesTarget(r.Targets, targetKey)).ToList();
        if (matched.Count == 0)
            return (false, $"目标 {targetKey} 不在该用户的白名单中");

        var level = matched.Max(r => LevelRank(r.Level));
        if (isWrite && level < ExecRank)
            return (false, $"用户对目标 {targetKey} 仅为只读授权，写操作被拒绝");

        return (true, null);
    }

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

    /// <summary>操作级别：exec=2，其余（read/未知/空）=1 —— 未知值按只读处理，宁严勿松。</summary>
    public static int LevelRank(string? level) =>
        string.Equals(level?.Trim(), "exec", StringComparison.OrdinalIgnoreCase) ? ExecRank : ReadRank;

    public const int ReadRank = 1;
    public const int ExecRank = 2;

    private static string DisplayName(string? username, string? userId) =>
        !string.IsNullOrWhiteSpace(username) ? username! : (userId is null or "" ? "(未知)" : userId);

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
                       "授权由管理员在桌面 config.json 的 security.authorization 中配置（用户白名单 + read/exec 级别）。"
            }
        }
    };
}
