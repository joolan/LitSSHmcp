using System.Text.Json;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.McpServer.Services;

/// <summary>工具的目标形态：决定授权过滤器如何解析和校验目标。</summary>
public enum ToolTargetKind
{
    /// <summary>无目标工具（使用指南、自检等）。</summary>
    None,

    /// <summary>必须有单个 SSH 服务器目标。</summary>
    Server,

    /// <summary>必须有单个数据源目标。</summary>
    Datasource,

    /// <summary>可选 SSH 服务器目标；未提供时暂不做目标级校验（后续按用户裁剪）。</summary>
    OptionalServer,

    /// <summary>可选数据源目标；未提供时暂不做目标级校验（后续按用户裁剪）。</summary>
    OptionalDatasource,

    /// <summary>多服务器目标（如拓扑发现）；要求调用者拥有全部涉及目标的权限。</summary>
    MultiServer,

    /// <summary>应用目标：授权校验扩展到该应用关联的服务器/数据源。</summary>
    Application,

    /// <summary>全局只读工具；当前放行，后续按用户裁剪返回内容。</summary>
    Global
}

/// <summary>用户对目标的最低访问级别。级别是有序的：read &lt; write &lt; exec。</summary>
public enum ToolAccessLevel
{
    Read = 1,
    Write = 2,
    Exec = 3
}

/// <summary>
/// 工具安全元数据的单一事实源：目标参数、最低访问级别、是否需要写锁、是否按参数动态判定读写。
/// 新增工具必须在此登记；<c>ToolCatalogConsistencyTests</c> 会用反射校验注册工具与目录一致。
/// <param name="SelfScopedGlobal">目标形态为 Global 时，工具自身按调用者裁剪返回内容（如服务器/数据源/会话清单），
/// 强制模式下允许普通成员调用；为 false 的无目标全局工具仅管理员可用。</param>
/// </summary>
public sealed record ToolSecurityDescriptor(
    string Name,
    ToolTargetKind TargetKind,
    string[] TargetArguments,
    ToolAccessLevel RequiredAccess,
    bool RequiresWriteLock,
    bool IsConditionalWrite,
    bool SelfScopedGlobal = false);

/// <summary>已注册 MCP 工具的安全目录。</summary>
public static class ToolSecurityCatalog
{
    private static readonly IReadOnlyDictionary<string, ToolSecurityDescriptor> Descriptors = Build();

    public static IReadOnlyCollection<string> Names => Descriptors.Keys.ToArray();

    public static ToolSecurityDescriptor? Find(string? name) =>
        !string.IsNullOrEmpty(name) && Descriptors.TryGetValue(name, out var descriptor) ? descriptor : null;

    public static bool TryGet(string? name, out ToolSecurityDescriptor descriptor)
    {
        if (!string.IsNullOrEmpty(name) && Descriptors.TryGetValue(name, out var found))
        {
            descriptor = found;
            return true;
        }

        descriptor = null!;
        return false;
    }

    private static IReadOnlyDictionary<string, ToolSecurityDescriptor> Build()
    {
        var list = new List<ToolSecurityDescriptor>();

        void Add(
            string name,
            ToolTargetKind kind,
            ToolAccessLevel access,
            bool writeLock = false,
            bool conditionalWrite = false,
            bool selfScopedGlobal = false,
            params string[] targetArguments)
        {
            list.Add(new ToolSecurityDescriptor(
                name, kind, targetArguments, access, writeLock, conditionalWrite, selfScopedGlobal));
        }

        void LocalRead(string name) => Add(name, ToolTargetKind.None, ToolAccessLevel.Read);
        void GlobalRead(string name, bool selfScoped = false) =>
            Add(name, ToolTargetKind.Global, ToolAccessLevel.Read, selfScopedGlobal: selfScoped);
        void ServerRead(string name) => Add(name, ToolTargetKind.Server, ToolAccessLevel.Read, targetArguments: "serverId");
        void DatasourceRead(string name) => Add(name, ToolTargetKind.Datasource, ToolAccessLevel.Read, targetArguments: "datasourceId");
        void ServerWrite(string name) => Add(name, ToolTargetKind.Server, ToolAccessLevel.Write, writeLock: true, targetArguments: "serverId");
        void ServerExec(string name, bool writeLock = true) => Add(name, ToolTargetKind.Server, ToolAccessLevel.Exec, writeLock: writeLock, targetArguments: "serverId");
        void DatasourceConditional(string name) => Add(name, ToolTargetKind.Datasource, ToolAccessLevel.Read, writeLock: true, conditionalWrite: true, targetArguments: "datasourceId");

        // 指南/自检：无目标。
        LocalRead("mcp_usage_guide");
        LocalRead("mcp_self_check");
        LocalRead("mcp_approval_round");

        // 服务器清单/状态。
        GlobalRead("ssh_list_servers", selfScoped: true);
        ServerRead("ssh_test_connection");
        ServerRead("ssh_snapshot_get");
        ServerExec("ssh_snapshot_refresh");
        ServerRead("ssh_list_files");
        ServerRead("ssh_download");

        // 命令执行：任意 shell 执行都要求 exec，不再用“首词只读”降低授权。
        ServerExec("ssh_execute_command");
        Add("ssh_execute_fleet", ToolTargetKind.MultiServer, ToolAccessLevel.Exec, writeLock: true,
            targetArguments: "serverIds");
        ServerExec("ssh_execute_sudo");
        ServerRead("ssh_get_sudo_status");

        // 文件上传。
        ServerWrite("ssh_upload");

        // 日志 / Java / Docker / systemd。
        ServerRead("log_query");
        ServerRead("java_processes");
        ServerRead("java_diagnostics");
        ServerRead("docker_query");
        ServerWrite("docker_restart");
        ServerExec("docker_exec");
        ServerRead("service_query");
        ServerWrite("service_restart");

        // 数据源。
        GlobalRead("datasource_list", selfScoped: true);
        DatasourceRead("datasource_test_connection");
        DatasourceRead("mysql_query");
        DatasourceConditional("mysql_execute");
        DatasourceRead("postgres_query");
        DatasourceConditional("postgres_execute");
        DatasourceRead("redis_read");
        DatasourceConditional("redis_execute");
        DatasourceRead("redis_diagnostics");

        // 拓扑查询：当前为全局只读，后续按用户裁剪图数据。
        GlobalRead("topology_get_overview");
        GlobalRead("topology_get_dependencies");
        GlobalRead("topology_query");
        Add("topology_timeline", ToolTargetKind.Server, ToolAccessLevel.Read, targetArguments: "serverId");
        Add("topology_diff", ToolTargetKind.Server, ToolAccessLevel.Read, targetArguments: "serverId");
        Add("topology_discover", ToolTargetKind.MultiServer, ToolAccessLevel.Exec, targetArguments: "serverIds");

        // 应用聚合体检：扩展到应用关联的服务器/数据源。
        Add("app_health_snapshot", ToolTargetKind.Application, ToolAccessLevel.Read, targetArguments: "appId");

        // 会话列表：全局清单，由工具按用户裁剪。

        var duplicate = list.GroupBy(d => d.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"工具安全目录存在重复项: {duplicate.Key}");

        return list.ToDictionary(d => d.Name, StringComparer.Ordinal);
    }
}

/// <summary>目标解析结果：规范目标键（ssh:&lt;id&gt; / ds:&lt;id&gt;）、错误和用于审计的展示键。</summary>
public sealed record ToolTargetResolution(
    IReadOnlyList<string> TargetKeys,
    string? Error,
    string? DisplayKey);

/// <summary>按工具安全元数据解析并归一化目标引用。</summary>
public static class ToolTargetResolver
{
    public static ToolTargetResolution Resolve(
        AppConfig config,
        ToolSecurityDescriptor descriptor,
        IDictionary<string, JsonElement>? args)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(descriptor);

        return descriptor.TargetKind switch
        {
            ToolTargetKind.None or ToolTargetKind.Global => new(Array.Empty<string>(), null, null),
            ToolTargetKind.Server => ResolveServer(config, GetStringArg(args, "serverId")),
            ToolTargetKind.Datasource => ResolveDatasource(config, GetStringArg(args, "datasourceId"), required: true),
            ToolTargetKind.OptionalServer => ResolveOptionalServer(config, GetStringArg(args, "serverId")),
            ToolTargetKind.OptionalDatasource => ResolveDatasource(config, GetStringArg(args, "datasourceId"), required: false),
            ToolTargetKind.MultiServer => ResolveServers(config, GetStringArg(args, "serverIds")),
            ToolTargetKind.Application => ResolveApplication(config, GetStringArg(args, "appId")),
            _ => new(Array.Empty<string>(), "unsupported_target_kind", null)
        };
    }

    private static ToolTargetResolution ResolveServer(AppConfig config, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return new(Array.Empty<string>(), "serverId 参数不能为空", null);

        var (server, status, error) = ToolSupport.ResolveServer(config, reference);
        if (server is null)
            return new(Array.Empty<string>(), error ?? status ?? "server_not_found", $"ssh:{reference}");

        var key = $"ssh:{server.Id}";
        return new(new[] { key }, null, key);
    }

    private static ToolTargetResolution ResolveOptionalServer(AppConfig config, string? reference) =>
        string.IsNullOrWhiteSpace(reference)
            ? new(Array.Empty<string>(), null, null)
            : ResolveServer(config, reference);

    private static ToolTargetResolution ResolveDatasource(AppConfig config, string? reference, bool required)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return required
                ? new(Array.Empty<string>(), "datasourceId 参数不能为空", null)
                : new(Array.Empty<string>(), null, null);

        var (datasource, status, error) = ToolSupport.ResolveDatasource(config, reference);
        if (datasource is null)
            return new(Array.Empty<string>(), error ?? status ?? "datasource_not_found", $"ds:{reference}");

        var key = $"ds:{datasource.Id}";
        return new(new[] { key }, null, key);
    }

    private static ToolTargetResolution ResolveServers(AppConfig config, string? reference)
    {
        var references = string.IsNullOrWhiteSpace(reference)
            ? config.Servers.Where(s => !s.Disabled).Select(s => s.Id).ToArray()
            : SplitReferences(reference);

        if (references.Length == 0)
            return new(Array.Empty<string>(), "没有可校验的服务器目标", "ssh:*");

        var keys = new List<string>();
        foreach (var item in references)
        {
            var (server, status, error) = ToolSupport.ResolveServer(config, item);
            if (server is null)
                return new(Array.Empty<string>(), error ?? status ?? "server_not_found", $"ssh:{item}");

            var key = $"ssh:{server.Id}";
            if (!keys.Contains(key, StringComparer.Ordinal))
                keys.Add(key);
        }

        return new(keys, null, keys.Count == 1 ? keys[0] : "ssh:multiple");
    }

    private static ToolTargetResolution ResolveApplication(AppConfig config, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return new(Array.Empty<string>(), "appId 参数不能为空", null);

        var (application, status, error) = ToolSupport.ResolveApplication(config, reference);
        if (application is null)
            return new(Array.Empty<string>(), error ?? status ?? "application_not_found", $"app:{reference}");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var applicationNode = AssetNode.App(application.Id);

        foreach (var relation in config.Relations ?? Array.Empty<RelationConfig>())
        {
            if (!string.Equals(relation.From, applicationNode, StringComparison.Ordinal))
                continue;

            AddAssetKey(config, relation.To, keys);
        }

        if (keys.Count == 0 && !string.IsNullOrWhiteSpace(application.Host))
        {
            var (server, _, _) = ToolSupport.ResolveServer(config, application.Host);
            if (server is not null)
                keys.Add($"ssh:{server.Id}");
        }

        if (keys.Count == 0)
            return new(Array.Empty<string>(), "应用没有可校验的服务器/数据源目标", $"app:{application.Id}");

        return new(keys.ToArray(), null, $"app:{application.Id}");
    }

    private static void AddAssetKey(AppConfig config, string asset, ISet<string> keys)
    {
        if (asset.StartsWith(AssetNode.SshPrefix, StringComparison.Ordinal))
        {
            var (server, _, _) = ToolSupport.ResolveServer(config, asset[AssetNode.SshPrefix.Length..]);
            if (server is not null)
                keys.Add($"ssh:{server.Id}");
            return;
        }

        if (asset.StartsWith(AssetNode.DsPrefix, StringComparison.Ordinal))
        {
            var (datasource, _, _) = ToolSupport.ResolveDatasource(config, asset[AssetNode.DsPrefix.Length..]);
            if (datasource is not null)
                keys.Add($"ds:{datasource.Id}");
        }
    }

    private static string[] SplitReferences(string value) =>
        value.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static string? GetStringArg(IDictionary<string, JsonElement>? args, string name)
    {
        if (args is null || !args.TryGetValue(name, out var element))
            return null;

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }
}
