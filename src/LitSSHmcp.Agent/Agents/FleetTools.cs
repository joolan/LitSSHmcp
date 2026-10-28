using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>
/// 批量只读查询：把一个只读目标工具并发跑在多台服务器/数据源/应用上，聚合成功/失败与每台摘要。
/// 仅允许内置只读目标工具，避免把写操作扇出到多台机器；目标工具仍受会话工具策略约束。
/// </summary>
public static class FleetTools
{
    public const string ToolName = "fleet_query";

    private const int DefaultParallelism = 4;
    private const int MaxParallelism = 8;
    private const int DefaultMaxTargets = 50;
    private const int MaxTargetsLimit = 100;
    private const int PerTargetMaxChars = 1200;
    private const int TotalMaxChars = 18000;

    /// <summary>允许批量调用的只读工具 → 目标参数名。</summary>
    private static readonly IReadOnlyDictionary<string, string> TargetParameters =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ssh_snapshot_get"] = "serverId",
            ["ssh_test_connection"] = "serverId",
            ["ssh_list_files"] = "serverId",
            ["log_query"] = "serverId",
            ["docker_query"] = "serverId",
            ["service_query"] = "serverId",
            ["java_diagnostics"] = "serverId",
            ["java_processes"] = "serverId",
            ["mysql_query"] = "datasourceId",
            ["postgres_query"] = "datasourceId",
            ["redis_read"] = "datasourceId",
            ["redis_diagnostics"] = "datasourceId",
            ["app_health_snapshot"] = "appId"
        };

    /// <summary>当前工具集中是否有可批量调用的只读目标工具（供系统提示决定是否展示该能力）。</summary>
    public static bool HasEligible(IEnumerable<IAgentTool> tools) =>
        tools.Any(t => t.ReadOnly && TargetParameters.ContainsKey(t.Name));

    public static IAgentTool? Create(IReadOnlyList<IAgentTool> tools, Func<string, bool>? isDenied = null)
    {
        var eligible = tools
            .Where(t => t.ReadOnly && TargetParameters.ContainsKey(t.Name))
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        if (eligible.Count == 0)
            return null;

        var function = new LocalFunction(
            ToolName,
            "批量只读查询: 把一个只读目标工具(ssh_snapshot_get/log_query/docker_query/service_query/java_diagnostics/mysql_query/redis_read/app_health_snapshot 等)并发跑在多台服务器/数据源/应用上, 聚合成功/失败与每台摘要; 写操作不支持; 结果超过预算会截断, 需要单台完整结果时再用原工具",
            """{"type":"object","properties":{"tool":{"type":"string","description":"只读目标工具名, 如 ssh_snapshot_get"},"targets":{"type":"string","description":"目标 ID, 逗号/空格分隔, 如 s1,s2,s3"},"argumentsJson":{"type":"string","description":"可选: 传给目标工具的 JSON 参数对象, 如 {\"section\":\"resource\",\"detail\":\"summary\"}"},"parallelism":{"type":"integer","description":"并发数, 默认4, 上限8"},"maxTargets":{"type":"integer","description":"最多目标数, 默认50, 上限100"}},"required":["tool","targets"]}""",
            (args, ct) => RunAsync(eligible, isDenied, args, ct));

        return new LocalAgentTool(function, readOnly: true);
    }

    private static async ValueTask<object?> RunAsync(
        IReadOnlyDictionary<string, IAgentTool> tools,
        Func<string, bool>? isDenied,
        IDictionary<string, object?> args,
        CancellationToken ct)
    {
        var toolName = (ArgumentReader.ReadString(args, "tool") ?? string.Empty).Trim();
        if (toolName.Length == 0)
            return "(缺少 tool)";
        if (!TargetParameters.TryGetValue(toolName, out var targetParam) || !tools.TryGetValue(toolName, out var tool))
            return $"不支持或不可用的工具: {toolName}。当前可用: {string.Join(", ", tools.Keys)}";
        if (isDenied?.Invoke(toolName) == true)
            return $"(工具 {toolName} 已被会话策略禁用，未执行批量查询)";

        var allTargets = ParseTargets(ArgumentReader.ReadString(args, "targets"));
        if (allTargets.Count == 0)
            return "(缺少 targets)";

        var maxTargets = Math.Clamp(ArgumentReader.ReadInt(args, "maxTargets") ?? DefaultMaxTargets, 1, MaxTargetsLimit);
        var truncated = allTargets.Count > maxTargets;
        var targets = allTargets.Take(maxTargets).ToList();

        var baseArgs = ParseArgs(ArgumentReader.ReadString(args, "argumentsJson"));
        if (baseArgs is null)
            return "(argumentsJson 不是合法 JSON 对象)";
        baseArgs.Remove(targetParam);

        var parallelism = Math.Clamp(
            ArgumentReader.ReadInt(args, "parallelism") ?? DefaultParallelism, 1, MaxParallelism);
        var results = new (string Target, bool Ok, string Text, long Ms)[targets.Count];
        using var gate = new SemaphoreSlim(parallelism, parallelism);
        var started = Stopwatch.StartNew();

        await Task.WhenAll(targets.Select(async (target, index) =>
        {
            await gate.WaitAsync(ct);
            var sw = Stopwatch.StartNew();
            try
            {
                var callArgs = new Dictionary<string, object?>(baseArgs) { [targetParam] = target };
                var result = await tool.InvokeAsync(callArgs, ct);
                results[index] = (target, true, Truncate(Normalize(result), PerTargetMaxChars), sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                results[index] = (target, false, Shorten(ex.Message, 300), sw.ElapsedMilliseconds);
            }
            finally
            {
                gate.Release();
            }
        }));

        var succeeded = results.Count(r => r.Ok);
        var failed = results.Length - succeeded;
        var sb = new StringBuilder();
        sb.AppendLine($"批量只读查询: {toolName} × {targets.Count} (并发 {parallelism}, 成功 {succeeded}, 失败 {failed}, 用时 {started.ElapsedMilliseconds} ms)");
        if (truncated)
            sb.AppendLine($"（目标数 {allTargets.Count} 超过上限 {maxTargets}，本次只执行前 {maxTargets} 个）");

        // 失败优先展示，避免预算耗尽时丢掉错误。
        var ordered = results.OrderBy(r => r.Ok).ToList();
        var budget = TotalMaxChars - sb.Length;
        for (var i = 0; i < ordered.Count; i++)
        {
            var r = ordered[i];
            var line = r.Ok
                ? $"[OK] {r.Target} ({r.Ms}ms): {r.Text}"
                : $"[ERR] {r.Target}: {r.Text}";
            if (line.Length + 1 > budget)
            {
                sb.AppendLine($"…（其余 {ordered.Count - i} 个目标结果已省略；请缩小 targets 或改用单机工具）");
                break;
            }
            sb.AppendLine(line);
            budget -= line.Length + 1;
        }
        sb.Append("提示: 这是聚合摘要；需要单台完整结果时再用原工具，不要逐台重复本调用。");
        return sb.ToString().Trim();
    }

    private static List<string> ParseTargets(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new List<string>()
            : value.Split(new[] { ',', '，', ';', ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();

    private static Dictionary<string, object?>? ParseArgs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Normalize(object? result)
    {
        var text = result switch
        {
            null => "(空结果)",
            string s => s,
            JsonElement je => je.GetRawText(),
            _ => JsonSerializer.Serialize(result)
        };
        return ToolResultFormatter.Normalize(text);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private static string Shorten(string? message, int max)
    {
        var text = (message ?? "unknown").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
