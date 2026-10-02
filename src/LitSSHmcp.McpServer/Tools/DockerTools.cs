// 【同步约定 · 请勿删除】本文件中的工具若发生变动(新增/改名/删除、参数或描述变化), 必须同步更新:
//   ① docs/TOOLS.md —— 工具说明的唯一事实来源(接入说明、意图路由表、参数与返回结构);
//   ② App 端菜单"配置 → MCP工具说明"(McpToolsWindow, 内容由 docs/TOOLS.md 嵌入) + get_usage_guide 内置清单(由注解反射生成, 无需手改);
//   ③ 若新增了工具类, 记得在 Program.cs 注册 WithTools<T>()。
// 只同步其一, AI 客户端拿到的工具说明就会与实际能力不一致。详见 docs/TOOLS.md 顶部"同步约定"。
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

[McpServerToolType]
public class DockerTools
{
    private static readonly Regex ContainerName = new("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IGuardedCommandService _runner;

    public DockerTools(IGuardedCommandService runner)
    {
        _runner = runner;
    }

    [McpServerTool(Name = "docker_ps", UseStructuredContent = true, OutputSchemaType = typeof(DockerPsDto), ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("列出服务器上的Docker容器(名称/镜像/状态/端口)。all=true 含已停止容器; 看容器日志用docker_logs, 重启用docker_restart")]
    public async Task<DockerPsDto> DockerPs(
        [Description("服务器标识: ID/名称/主机名, 可用ssh_list_servers列出")] string serverId,
        [Description("true=包含已停止的容器(-a); 默认只看运行中")] bool all = false,
        CancellationToken cancellationToken = default)
    {
        var command = all
            ? "docker ps -a --format '{{json .}}'"
            : "docker ps --format '{{json .}}'";

        var outcome = await _runner.RunAsync(serverId, command, cancellationToken);
        if (!outcome.Success)
            return DockerPsDto.Fail(outcome.Status ?? "docker_error",
                outcome.Error ?? "docker ps 执行失败（确认服务器已安装 Docker 且当前账号有权限）。");

        var containers = new List<DockerContainerDto>();
        foreach (var line in outcome.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line, JsonOptions);
                if (raw == null) continue;
                containers.Add(new DockerContainerDto
                {
                    Id = Get(raw, "ID"),
                    Names = Get(raw, "Names"),
                    Image = Get(raw, "Image"),
                    Status = Get(raw, "Status"),
                    State = Get(raw, "State"),
                    Ports = Get(raw, "Ports")
                });
            }
            catch (JsonException)
            {
                // 非 JSON 行（如 Docker 的告警）忽略
            }
        }

        return new DockerPsDto
        {
            Success = true,
            ServerId = outcome.ServerId,
            ServerName = outcome.ServerName,
            Host = outcome.ServerHost,
            Count = containers.Count,
            Truncated = outcome.Truncated,
            Containers = containers
        };
    }

    [McpServerTool(Name = "docker_logs", UseStructuredContent = true, OutputSchemaType = typeof(RemoteCommandResultDto), ReadOnly = true, OpenWorld = true)]
    [Description("查看Docker容器日志。用tail限制行数, since按时间过滤(如 '10m'/'2024-01-01T00:00:00'); 容器操作异常时优先看它")]
    public async Task<RemoteCommandResultDto> DockerLogs(
        [Description("服务器标识: ID/名称/主机名")] string serverId,
        [Description("容器名或容器ID")] string container,
        [Description("返回最近的行数(默认200, 上限5000)")] int tail = 200,
        [Description("起始时间(可选), 如 10m / 2h / 2024-01-01T00:00:00")] string? since = null,
        CancellationToken cancellationToken = default)
    {
        if (!ContainerName.IsMatch(container))
            return new RemoteCommandResultDto { Success = false, Status = "invalid_container", Error = $"非法的容器名: {container}" };

        tail = Math.Clamp(tail, 1, 5000);
        var sinceArg = string.IsNullOrWhiteSpace(since)
            ? string.Empty
            : $" --since {Core.Services.Security.ShellQuote.Single(since)}";

        var command = $"docker logs --tail {tail}{sinceArg} -- {Core.Services.Security.ShellQuote.Single(container)}";
        return RemoteCommandResultDto.From(await _runner.RunAsync(serverId, command, cancellationToken));
    }

    [McpServerTool(Name = "docker_inspect", UseStructuredContent = true, OutputSchemaType = typeof(RemoteCommandResultDto), ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("查看Docker容器详情(inspect, JSON): 环境变量/挂载/网络/重启次数/健康检查。排查容器配置问题用")]
    public async Task<RemoteCommandResultDto> DockerInspect(
        [Description("服务器标识: ID/名称/主机名")] string serverId,
        [Description("容器名或容器ID")] string container,
        CancellationToken cancellationToken = default)
    {
        if (!ContainerName.IsMatch(container))
            return new RemoteCommandResultDto { Success = false, Status = "invalid_container", Error = $"非法的容器名: {container}" };

        var command = $"docker inspect -- {Core.Services.Security.ShellQuote.Single(container)}";
        return RemoteCommandResultDto.From(await _runner.RunAsync(serverId, command, cancellationToken));
    }

    [McpServerTool(Name = "docker_stats", UseStructuredContent = true, OutputSchemaType = typeof(RemoteCommandResultDto), ReadOnly = true, OpenWorld = true)]
    [Description("查看Docker容器资源占用(CPU/内存/网络/磁盘IO的一次性快照)。容器占满CPU/内存/OOM时用")]
    public async Task<RemoteCommandResultDto> DockerStats(
        [Description("服务器标识: ID/名称/主机名")] string serverId,
        CancellationToken cancellationToken = default)
    {
        const string command = "docker stats --no-stream --format '{{.Name}} | CPU {{.CPUPerc}} | MEM {{.MemUsage}} ({{.MemPerc}}) | NET {{.NetIO}} | BLOCK {{.BlockIO}} | PIDs {{.PIDs}}'";
        return RemoteCommandResultDto.From(await _runner.RunAsync(serverId, command, cancellationToken));
    }

    [McpServerTool(Name = "docker_images", UseStructuredContent = true, OutputSchemaType = typeof(RemoteCommandResultDto), ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("列出服务器上的Docker镜像(仓库/标签/大小/创建时间)。排查镜像版本/磁盘占用用")]
    public async Task<RemoteCommandResultDto> DockerImages(
        [Description("服务器标识: ID/名称/主机名")] string serverId,
        CancellationToken cancellationToken = default)
    {
        const string command = "docker images --format '{{.Repository}}:{{.Tag}} | {{.ID}} | {{.Size}} | {{.CreatedSince}}'";
        return RemoteCommandResultDto.From(await _runner.RunAsync(serverId, command, cancellationToken));
    }

    [McpServerTool(Name = "docker_restart", UseStructuredContent = true, OutputSchemaType = typeof(RemoteCommandResultDto), Destructive = true, OpenWorld = true)]
    [Description("重启Docker容器(会短暂中断服务, 需人工确认)。确认容器名用docker_ps, 重启后看docker_logs")]
    public async Task<RemoteCommandResultDto> DockerRestart(
        [Description("服务器标识: ID/名称/主机名")] string serverId,
        [Description("容器名或容器ID")] string container,
        CancellationToken cancellationToken = default)
    {
        if (!ContainerName.IsMatch(container))
            return new RemoteCommandResultDto { Success = false, Status = "invalid_container", Error = $"非法的容器名: {container}" };

        var command = $"docker restart -- {Core.Services.Security.ShellQuote.Single(container)}";
        return RemoteCommandResultDto.From(await _runner.RunAsync(serverId, command, cancellationToken));
    }

    [McpServerTool(Name = "docker_exec", UseStructuredContent = true, OutputSchemaType = typeof(RemoteCommandResultDto), Destructive = true, OpenWorld = true)]
    [Description("在运行中的Docker容器内执行命令(需人工确认)。用于容器内排查: 查进程/看配置/测端口。只读排查优先用docker_logs/docker_inspect")]
    public async Task<RemoteCommandResultDto> DockerExec(
        [Description("服务器标识: ID/名称/主机名")] string serverId,
        [Description("容器名或容器ID")] string container,
        [Description("要在容器内执行的命令, 如 'ps -ef' / 'cat /app/config.yml'")] string command,
        CancellationToken cancellationToken = default)
    {
        if (!ContainerName.IsMatch(container))
            return new RemoteCommandResultDto { Success = false, Status = "invalid_container", Error = $"非法的容器名: {container}" };
        if (string.IsNullOrWhiteSpace(command))
            return new RemoteCommandResultDto { Success = false, Status = "invalid_argument", Error = "容器内命令不能为空。" };

        var full = $"docker exec {Core.Services.Security.ShellQuote.Single(container)} {command}";
        return RemoteCommandResultDto.From(await _runner.RunAsync(serverId, full, cancellationToken));
    }

    private static string? Get(Dictionary<string, JsonElement> raw, string key) =>
        raw.TryGetValue(key, out var value) ? value.ToString() : null;
}
