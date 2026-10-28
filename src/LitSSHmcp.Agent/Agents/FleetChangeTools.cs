using System.Text;
using System.Text.Json;
using LitSSHmcp.Core.Services.Security;
using Microsoft.Extensions.AI;
using ModelContextProtocol;

namespace LitSSHmcp.Agent;

/// <summary>
/// 批量变更编排：plan → dry_run（连通性预检）→ canary（1 台）→ apply（其余）→ rollback（已变更目标）。
/// 实际执行由 MCP `ssh_execute_fleet`（服务端一次审批 + 命令过滤 + 审计 + 全局写锁）；
/// 本工具只负责安全流程、持久化状态与逐台进度，不绕过审批。
/// </summary>
public static class FleetChangeTools
{
    public const string ToolName = "fleet_change";
    private const int MaxTargetsLimit = 100;

    public static IAgentTool? Create(IAgentTool? fleetExecTool, IAgentTool? testConnectionTool,
        IFleetPlanStore? planStore = null, string ownerKey = "local", long? sessionId = null,
        IAgentTool? listServersTool = null, Func<string, bool>? isDenied = null)
    {
        if (fleetExecTool is null)
            return null;

        var store = planStore ?? new MemoryFleetPlanStore();
        var owner = string.IsNullOrWhiteSpace(ownerKey) ? "local" : ownerKey;
        var function = new LocalFunction(
            ToolName,
            "批量变更编排（最重要：严格按 plan → dry_run → canary → apply → rollback 顺序）。plan 只生成计划；dry_run 只做连通性预检；canary 先在 1 台执行；apply 执行其余目标；rollback 只回滚已变更目标。canary/apply/rollback/retry_failed 必须 confirm=true，apply 前必须 canary 成功且提供 rollbackCommand；retry_failed 只重试失败目标。实际执行由 MCP ssh_execute_fleet 一次审批+命令过滤+审计完成。",
            """
            {"type":"object","properties":{
              "mode":{"type":"string","enum":["plan","dry_run","canary","apply","rollback","retry_failed","status"],"description":"操作阶段"},
              "planId":{"type":"string","description":"plan 返回的计划ID；dry_run/canary/apply/rollback/status 必填"},
              "serverIds":{"type":"string","description":"plan: 目标服务器ID，逗号/空格分隔"},
              "command":{"type":"string","description":"plan: 要执行的单条非脚本命令"},
              "rollbackCommand":{"type":"string","description":"plan: 回滚命令；apply 前必须提供"},
              "concurrency":{"type":"integer","description":"并发数 1-8，默认 4"},
              "maxTargets":{"type":"integer","description":"最多目标数 1-100，默认 20"},
              "timeoutSeconds":{"type":"integer","description":"单台命令超时秒数 1-3600，默认 60"},
              "riskNote":{"type":"string","description":"可选：助手风险评估摘要，展示在审批弹窗"},
              "confirm":{"type":"boolean","description":"canary/apply/rollback/retry_failed 必须传 true"}
            },"required":["mode"]}
            """,
            async (args, ct) =>
            {
                var mode = (ArgumentReader.ReadString(args, "mode") ?? string.Empty).Trim().ToLowerInvariant();
                if (mode == "plan")
                    return await PlanAsync(store, owner, sessionId, args, listServersTool, isDenied, ct);

                if (mode is "canary" or "apply" or "rollback" or "retry_failed" &&
                    isDenied?.Invoke("ssh_execute_fleet") == true)
                    return "错误: 底层 ssh_execute_fleet 已被当前会话策略禁用，无法执行批量变更；请调整工具策略或使用单目标审批";

                var planId = (ArgumentReader.ReadString(args, "planId") ?? string.Empty).Trim();
                if (planId.Length == 0)
                    return "错误: 该操作需要 planId（先 mode=plan）";
                var plan = await store.GetAsync(planId, owner, sessionId, ct);
                if (plan is null)
                    return $"错误: 计划 {planId} 不存在、已过期或不属于当前会话；请重新 plan";
                if (mode == "status")
                    return Status(plan);

                plan.Running = true;
                plan.Phase = mode;
                plan.LastError = null;
                await store.SaveAsync(plan, ct);
                try
                {
                    var text = mode switch
                    {
                        "dry_run" => await DryRunAsync(store, plan, testConnectionTool, isDenied, ct),
                        "canary" => await CanaryAsync(store, plan, fleetExecTool, args, ct),
                        "apply" => await ApplyAsync(store, plan, fleetExecTool, args, ct),
                        "rollback" => await RollbackAsync(store, plan, fleetExecTool, args, ct),
                        "retry_failed" => await RetryFailedAsync(store, plan, fleetExecTool, args, ct),
                        _ => "错误: mode 仅支持 plan/dry_run/canary/apply/rollback/retry_failed/status"
                    };
                    plan.Running = false;
                    plan.Phase = mode + "_done";
                    if (text.StartsWith("错误:", StringComparison.Ordinal))
                        plan.LastError = Shorten(text);
                    await store.SaveAsync(plan, CancellationToken.None);
                    return text;
                }
                catch (OperationCanceledException)
                {
                    plan.Running = false;
                    plan.Phase = "cancelled";
                    plan.LastError = "用户取消";
                    foreach (var item in plan.TargetResults.Where(t => t.Status == "running"))
                        item.Status = "cancelled";
                    await store.SaveAsync(plan, CancellationToken.None);
                    return $"已取消计划 {plan.Id}（{mode}）。已执行的目标不会自动回滚，请检查后按需 rollback。";
                }
                catch (Exception ex)
                {
                    plan.Running = false;
                    plan.Phase = "failed";
                    plan.LastError = Shorten(ex.Message);
                    foreach (var item in plan.TargetResults.Where(t => t.Status == "running"))
                    {
                        item.Status = "failed";
                        item.Error ??= plan.LastError;
                    }
                    await store.SaveAsync(plan, CancellationToken.None);
                    return $"错误: {mode} 执行失败: {Shorten(ex.Message)}";
                }
            });

        return new LocalAgentTool(function);
    }

    private static async Task<string> PlanAsync(IFleetPlanStore store, string ownerKey, long? sessionId,
        IDictionary<string, object?> args, IAgentTool? listServersTool, Func<string, bool>? isDenied,
        CancellationToken ct)
    {
        var targets = ParseTargets(ArgumentReader.ReadString(args, "serverIds"));
        if (targets.Count == 0)
            return "错误: serverIds 不能为空";
        var maxTargets = Math.Clamp(ArgumentReader.ReadInt(args, "maxTargets") ?? 20, 1, MaxTargetsLimit);
        if (targets.Count > maxTargets)
            return $"错误: 目标数 {targets.Count} 超过 maxTargets={maxTargets}";

        var command = (ArgumentReader.ReadString(args, "command") ?? string.Empty).Trim();
        if (command.Length == 0)
            return "错误: command 不能为空";
        if (ScriptExecForm.TryParse(command, out _, out _))
            return "错误: 批量变更不支持脚本文件（bash x.sh / python3 x.py / ./x.sh）；请用单目标审批执行脚本";
        var rollback = (ArgumentReader.ReadString(args, "rollbackCommand") ?? string.Empty).Trim();
        if (rollback.Length > 0 && ScriptExecForm.TryParse(rollback, out _, out _))
            return "错误: 批量回滚不支持脚本文件；请用单目标审批执行回滚脚本";
        var concurrency = Math.Clamp(ArgumentReader.ReadInt(args, "concurrency") ?? 4, 1, 8);
        var timeoutSeconds = Math.Clamp(ArgumentReader.ReadInt(args, "timeoutSeconds") ?? 60, 1, 3600);

        var preflightTool = listServersTool is not null && isDenied?.Invoke(listServersTool.Name) != true
            ? listServersTool
            : null;
        var preflight = await PreflightTargetsAsync(targets, preflightTool, ct);
        if (preflight.Unknown.Count > 0)
            return $"错误: 目标不存在或无权访问: {string.Join(", ", preflight.Unknown)}；请从目标清单重新选择";
        if (preflight.NoExec.Count > 0)
            return $"错误: 目标缺少 exec 权限: {string.Join(", ", preflight.NoExec)}；请调整授权矩阵或改用只读/单目标工具";

        var id = Guid.NewGuid().ToString("N")[..8];
        var plan = new FleetPlanState
        {
            Id = id,
            OwnerKey = ownerKey,
            SessionId = sessionId,
            Targets = targets,
            Command = command,
            RollbackCommand = rollback,
            Concurrency = concurrency,
            TimeoutSeconds = timeoutSeconds,
            RiskNote = (ArgumentReader.ReadString(args, "riskNote") ?? string.Empty).Trim(),
            Phase = "planned"
        };
        foreach (var target in targets)
        {
            preflight.Details.TryGetValue(target, out var detail);
            plan.TargetResults.Add(new FleetTargetState
            {
                ServerId = target,
                ServerName = detail?.Name,
                Host = detail?.Host,
                Phase = "plan",
                Status = "pending"
            });
        }
        await store.SaveAsync(plan, ct);

        var sb = new StringBuilder();
        sb.AppendLine($"## 批量变更计划 {id}");
        sb.AppendLine($"- 目标: {targets.Count} 台");
        foreach (var target in targets.Take(30))
        {
            preflight.Details.TryGetValue(target, out var detail);
            sb.AppendLine($"  - {target}{(string.IsNullOrWhiteSpace(detail?.Name) ? "" : $" ({detail?.Name})")}" +
                          $"{(string.IsNullOrWhiteSpace(detail?.Host) ? "" : $" {detail?.Host}")}");
        }
        if (targets.Count > 30) sb.AppendLine($"  - …其余 {targets.Count - 30} 台");
        sb.AppendLine($"- 命令: `{Truncate(command, 500)}`");
        sb.AppendLine($"- 回滚: {(rollback.Length == 0 ? "**未提供**（apply 前必须补充）" : $"`{Truncate(rollback, 500)}`")}");
        sb.AppendLine($"- 并发: {concurrency}；单台超时: {timeoutSeconds}s");
        if (plan.RiskNote.Length > 0) sb.AppendLine($"- 风险备注: {Truncate(plan.RiskNote, 500)}");
        sb.AppendLine(preflight.Checked
            ? "- 预检: 目标均存在于当前可见服务器清单"
            : "- 预检: 未校验目标清单（ssh_list_servers 不可用），执行前仍会校验授权/连通性");
        sb.AppendLine();
        sb.AppendLine($"下一步: `fleet_change(mode=\"dry_run\", planId=\"{id}\")`");
        return sb.ToString().Trim();
    }

    private static async Task<string> DryRunAsync(IFleetPlanStore store, FleetPlanState plan,
        IAgentTool? testTool, Func<string, bool>? isDenied, CancellationToken ct)
    {
        if (testTool is null || isDenied?.Invoke(testTool.Name) == true)
        {
            plan.DryRunOk = false;
            plan.LastError = "ssh_test_connection 不可用或已被会话策略禁用";
            return "错误: 当前缺少可用的 ssh_test_connection，无法 dry_run；仍可 plan，但不允许直接 apply";
        }
        var sb = new StringBuilder();
        sb.AppendLine($"## 批量变更预检 {plan.Id}");
        var okCount = 0;
        foreach (var target in plan.Targets)
        {
            ct.ThrowIfCancellationRequested();
            plan.UpsertTarget(new FleetTargetState
            {
                ServerId = target,
                Phase = "dry_run",
                Status = "running"
            });
            await store.SaveAsync(plan, ct);
            var ok = false;
            string text;
            try
            {
                var result = await testTool.InvokeAsync(new Dictionary<string, object?> { ["serverId"] = target }, ct);
                var json = ToJson(result);
                ok = ReadBool(json, "success");
                text = ok ? "连通正常" : Truncate(ReadString(json, "error") ?? ReadString(json, "status") ?? "失败", 200);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                text = "异常: " + Shorten(ex.Message);
            }
            if (ok) okCount++;
            plan.UpsertTarget(new FleetTargetState
            {
                ServerId = target,
                Phase = "dry_run",
                Status = ok ? "ok" : "failed",
                Error = ok ? null : text,
                Output = text
            });
            await store.SaveAsync(plan, ct);
            sb.AppendLine($"- {(ok ? "OK" : "ERR")} {target}: {text}");
        }
        plan.DryRunOk = okCount == plan.Targets.Count;
        if (!plan.DryRunOk)
            plan.LastError = $"dry_run 失败 {plan.Targets.Count - okCount} 台";
        sb.AppendLine();
        sb.AppendLine(plan.DryRunOk
            ? $"预检通过。下一步: `fleet_change(mode=\"canary\", planId=\"{plan.Id}\", confirm=true)`"
            : $"预检失败 {plan.Targets.Count - okCount} 台，禁止继续；请先修复连通性。");
        return sb.ToString().Trim();
    }

    private static async Task<string> CanaryAsync(IFleetPlanStore store, FleetPlanState plan, IAgentTool execTool,
        IDictionary<string, object?> args, CancellationToken ct)
    {
        if (!ReadBool(args, "confirm"))
            return "错误: canary 必须 confirm=true";
        if (!plan.DryRunOk)
            return "错误: 必须先完成 dry_run 且全部连通正常，才能 canary";
        if (plan.CanaryOk)
            return $"错误: 计划 {plan.Id} 已完成 canary（{plan.CanaryTarget}）；请用 mode=apply";
        var target = plan.Targets[0];
        var (success, text, items) = await ExecuteAsync(store, plan, execTool, new[] { target }, plan.Command,
            "canary", plan.Concurrency, plan.RiskNote, ct);
        if (!success)
        {
            plan.LastError = "canary 失败";
            return $"## canary 失败 {plan.Id}\n{text}\n\n禁止 apply；请修复后重做 plan。";
        }
        plan.CanaryOk = true;
        plan.CanaryTarget = target;
        var changed = items.Where(i => i.Ok).Select(i => i.Id).ToList();
        if (changed.Count == 0) changed.Add(target);
        foreach (var id in changed) plan.AppliedTargets.Add(id);
        await store.SaveAsync(plan, ct);
        return $"## canary 成功 {plan.Id}\n- 目标: {target}\n\n{text}\n\n下一步: `fleet_change(mode=\"apply\", planId=\"{plan.Id}\", confirm=true)`";
    }

    private static async Task<string> ApplyAsync(IFleetPlanStore store, FleetPlanState plan, IAgentTool execTool,
        IDictionary<string, object?> args, CancellationToken ct)
    {
        if (!ReadBool(args, "confirm"))
            return "错误: apply 必须 confirm=true";
        if (!plan.CanaryOk)
            return "错误: 必须先 canary 成功，才能 apply";
        if (string.IsNullOrWhiteSpace(plan.RollbackCommand))
            return "错误: apply 前必须提供 rollbackCommand（plan 时填写，或重做 plan）";
        var remaining = plan.Targets.Where(t => !plan.AppliedTargets.Contains(t)).ToList();
        if (remaining.Count == 0)
            return $"计划 {plan.Id} 已无剩余目标（canary/apply 已覆盖全部）";
        var (success, text, items) = await ExecuteAsync(store, plan, execTool, remaining, plan.Command,
            "apply", plan.Concurrency, plan.RiskNote, ct);
        var changed = items.Where(i => i.Ok).Select(i => i.Id).ToList();
        if (changed.Count == 0 && success) changed.AddRange(remaining);
        foreach (var id in changed) plan.AppliedTargets.Add(id);
        if (!success) plan.LastError = "apply 部分失败";
        await store.SaveAsync(plan, ct);
        return $"## apply {(success ? "完成" : "部分失败")} {plan.Id}\n- 已变更目标累计: {plan.AppliedTargets.Count}/{plan.Targets.Count}\n\n{text}\n\n" +
               (success ? $"如需回滚: `fleet_change(mode=\"rollback\", planId=\"{plan.Id}\", confirm=true)`" : "存在失败，请检查逐台结果；回滚用 mode=rollback。");
    }

    private static async Task<string> RetryFailedAsync(IFleetPlanStore store, FleetPlanState plan,
        IAgentTool execTool, IDictionary<string, object?> args, CancellationToken ct)
    {
        if (!ReadBool(args, "confirm"))
            return "错误: retry_failed 必须 confirm=true";
        if (!plan.DryRunOk)
            return "错误: 必须先完成 dry_run，才能重试失败目标";
        if (plan.AppliedTargets.Count > 0 && string.IsNullOrWhiteSpace(plan.RollbackCommand))
            return "错误: 已有目标变更但计划缺少 rollbackCommand；请先补充回滚再重试";

        var failed = plan.TargetResults
            .Where(t => string.Equals(t.Status, "failed", StringComparison.Ordinal))
            .Select(t => t.ServerId)
            .Where(plan.Targets.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (failed.Count == 0)
            return $"计划 {plan.Id} 没有失败目标需要重试";

        var (success, text, items) = await ExecuteAsync(store, plan, execTool, failed, plan.Command,
            "retry_failed", plan.Concurrency, plan.RiskNote, ct);
        var changed = items.Where(i => i.Ok).Select(i => i.Id).ToList();
        if (changed.Count == 0 && success) changed.AddRange(failed);
        foreach (var id in changed) plan.AppliedTargets.Add(id);
        if (success && failed.Count == 1 && string.Equals(failed[0], plan.Targets[0], StringComparison.Ordinal))
        {
            plan.CanaryOk = true;
            plan.CanaryTarget = failed[0];
        }
        if (!success) plan.LastError = "retry_failed 部分失败";
        await store.SaveAsync(plan, ct);
        return $"## retry_failed {(success ? "完成" : "部分失败")} {plan.Id}\n" +
               $"- 重试目标: {string.Join(", ", failed)}\n\n{text}";
    }

    private static async Task<string> RollbackAsync(IFleetPlanStore store, FleetPlanState plan, IAgentTool execTool,
        IDictionary<string, object?> args, CancellationToken ct)
    {
        if (!ReadBool(args, "confirm"))
            return "错误: rollback 必须 confirm=true";
        if (string.IsNullOrWhiteSpace(plan.RollbackCommand))
            return "错误: 计划未提供 rollbackCommand，无法回滚";
        var requested = ParseTargets(ArgumentReader.ReadString(args, "serverIds"));
        var targets = requested.Count == 0
            ? plan.AppliedTargets.ToList()
            : plan.AppliedTargets.Where(requested.Contains).ToList();
        if (targets.Count == 0)
            return requested.Count == 0
                ? $"计划 {plan.Id} 没有已变更目标，无需回滚"
                : $"错误: 指定的目标中没有已变更目标，无法回滚";
        var (success, text, items) = await ExecuteAsync(store, plan, execTool, targets, plan.RollbackCommand,
            "rollback", plan.Concurrency, "批量回滚: " + plan.RiskNote, ct);
        var rolledBack = items.Where(i => i.Ok).Select(i => i.Id).ToList();
        if (rolledBack.Count == 0 && success) rolledBack.AddRange(targets);
        foreach (var id in rolledBack) plan.AppliedTargets.Remove(id);
        plan.RolledBack = success && plan.AppliedTargets.Count == 0;
        if (!success) plan.LastError = "rollback 部分失败";
        await store.SaveAsync(plan, ct);
        return $"## rollback {(success ? "完成" : "部分失败")} {plan.Id}\n- 已回滚: {rolledBack.Count}/{targets.Count}\n\n{text}";
    }

    private static string Status(FleetPlanState plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## 计划 {plan.Id}");
        sb.AppendLine($"- 阶段: {plan.Phase}{(plan.Running ? "（运行中）" : "")}");
        sb.AppendLine($"- 目标: {plan.Targets.Count} 台；dry_run={plan.DryRunOk}；canary={plan.CanaryOk}；已变更={plan.AppliedTargets.Count}；已回滚={plan.RolledBack}");
        sb.AppendLine($"- 命令: `{Truncate(plan.Command, 300)}`");
        sb.AppendLine($"- 回滚: {(plan.RollbackCommand.Length == 0 ? "未提供" : $"`{Truncate(plan.RollbackCommand, 300)}`")}");
        if (plan.LastError is not null) sb.AppendLine($"- 最近错误: {plan.LastError}");
        if (plan.TargetResults.Count > 0)
        {
            sb.AppendLine("- 逐台状态:");
            foreach (var item in plan.TargetResults.Take(100))
            {
                var name = string.IsNullOrWhiteSpace(item.ServerName) ? item.ServerId : item.ServerName;
                sb.AppendLine($"  - {(item.Status ?? "pending").ToUpperInvariant()} {name} [{item.Phase}]" +
                              (string.IsNullOrWhiteSpace(item.Error) ? "" : $": {Truncate(item.Error, 160)}"));
            }
        }
        return sb.ToString().Trim();
    }

    private static async Task<(bool Success, string Text, List<FleetExecItem> Items)> ExecuteAsync(
        IFleetPlanStore store, FleetPlanState plan, IAgentTool execTool, IReadOnlyList<string> targets,
        string command, string phase, int concurrency, string riskNote, CancellationToken ct)
    {
        var args = new Dictionary<string, object?>
        {
            ["serverIds"] = string.Join(",", targets),
            ["command"] = command,
            ["concurrency"] = concurrency,
            ["timeoutSeconds"] = plan.TimeoutSeconds,
            ["maxTargets"] = targets.Count,
            ["riskNote"] = riskNote
        };
        var progress = new InlineProgress<ProgressNotificationValue>(value =>
            OnProgress(store, plan, phase, value));
        var result = await execTool.InvokeAsync(args, progress, ct);
        var json = ToJson(result);
        var items = new List<FleetExecItem>();
        if (!TryParse(json, out var root))
            return (false, Shorten(json), items);

        var success = ReadBoolElement(root, "success");
        if (TryGetProperty(root, "items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsElement.EnumerateArray())
            {
                var id = ReadStringElement(item, "serverId");
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                var ok = ReadBoolElement(item, "success");
                var detail = new FleetExecItem(
                    id!,
                    ReadStringElement(item, "serverName"),
                    ReadStringElement(item, "host"),
                    ok,
                    ReadStringElement(item, "error"),
                    ReadStringElement(item, "output"));
                items.Add(detail);
                plan.UpsertTarget(new FleetTargetState
                {
                    ServerId = detail.Id,
                    ServerName = detail.Name,
                    Host = detail.Host,
                    Phase = phase,
                    Status = ok ? "ok" : "failed",
                    Error = detail.Error,
                    Output = detail.Output
                });
            }
        }
        await store.SaveAsync(plan, ct);
        var text = ReadStringElement(root, "summary") ?? Shorten(json);
        return (success, text, items);
    }

    private static void OnProgress(IFleetPlanStore store, FleetPlanState plan, string phase,
        ProgressNotificationValue value)
    {
        if (string.IsNullOrWhiteSpace(value.Message))
            return;
        try
        {
            using var doc = JsonDocument.Parse(value.Message);
            var root = doc.RootElement;
            var id = ReadStringElement(root, "serverId");
            if (string.IsNullOrWhiteSpace(id))
                return;
            var ok = ReadBoolElement(root, "success");
            plan.UpsertTarget(new FleetTargetState
            {
                ServerId = id!,
                ServerName = ReadStringElement(root, "serverName"),
                Host = ReadStringElement(root, "host"),
                Phase = phase,
                Status = ok ? "ok" : "failed",
                Error = ReadStringElement(root, "error"),
                Output = ReadStringElement(root, "output")
            });
            // 进度通知必须尽快落盘，UI 才能轮询到逐台状态。
            store.SaveAsync(plan, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // 进度失败不影响批量执行主流程
        }
    }

    private static async Task<FleetPreflightResult> PreflightTargetsAsync(
        IReadOnlyList<string> targets, IAgentTool? listTool, CancellationToken ct)
    {
        var details = targets.ToDictionary(t => t, _ => new FleetTargetDetail(null, null, null),
            StringComparer.Ordinal);
        if (listTool is null)
            return new FleetPreflightResult(false, new List<string>(), new List<string>(), details);
        try
        {
            var result = await listTool.InvokeAsync(new Dictionary<string, object?>(), ct);
            if (!TryParse(ToJson(result), out var root))
                return new FleetPreflightResult(false, new List<string>(), new List<string>(), details);
            if (!TryGetProperty(root, "servers", out var servers) || servers.ValueKind != JsonValueKind.Array)
                return new FleetPreflightResult(false, new List<string>(), new List<string>(), details);
            var lookup = new Dictionary<string, FleetTargetDetail>(StringComparer.OrdinalIgnoreCase);
            foreach (var server in servers.EnumerateArray())
            {
                var id = ReadStringElement(server, "id");
                var name = ReadStringElement(server, "name");
                var host = ReadStringElement(server, "host");
                var canExec = ReadNullableBoolElement(server, "canExec");
                var detail = new FleetTargetDetail(name, host, canExec);
                if (!string.IsNullOrWhiteSpace(id))
                    lookup[id!] = detail;
                if (!string.IsNullOrWhiteSpace(name))
                    lookup[name!] = detail;
                if (!string.IsNullOrWhiteSpace(host))
                    lookup[host!] = detail;
            }
            var unknown = new List<string>();
            var noExec = new List<string>();
            foreach (var target in targets)
            {
                if (!lookup.TryGetValue(target, out var detail))
                {
                    unknown.Add(target);
                    continue;
                }
                details[target] = detail;
                if (detail.CanExec == false)
                    noExec.Add(target);
            }
            return new FleetPreflightResult(true, unknown, noExec, details);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new FleetPreflightResult(false, new List<string>(), new List<string>(), details);
        }
    }

    private sealed record FleetPreflightResult(bool Checked, List<string> Unknown, List<string> NoExec,
        Dictionary<string, FleetTargetDetail> Details);

    private sealed record FleetTargetDetail(string? Name, string? Host, bool? CanExec);

    private static string? ReadStringElement(JsonElement element, string key) =>
        TryGetProperty(element, key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record FleetExecItem(string Id, string? Name, string? Host, bool Ok, string? Error, string? Output);

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private static List<string> ParseTargets(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new List<string>()
            : value.Split(new[] { ',', '，', ';', ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .Take(MaxTargetsLimit)
                .ToList();

    private static string ToJson(object? value)
    {
        if (value is null) return "{}";
        if (value is JsonElement element) return Unwrap(element).GetRawText();
        if (value is string text) return text;
        return JsonSerializer.Serialize(value);
    }

    private static JsonElement Unwrap(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            TryGetProperty(element, "structuredContent", out var structured))
            return Unwrap(structured);
        if (element.ValueKind == JsonValueKind.Object &&
            TryGetProperty(element, "result", out var result) && result.ValueKind != JsonValueKind.Null)
            return Unwrap(result);
        return element;
    }

    private static bool ReadBool(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return false;
        return value switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var parsed) && parsed,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.String } je => bool.TryParse(je.GetString(), out var parsed) && parsed,
            _ => false
        };
    }

    private static bool ReadBool(string json, string key) =>
        TryParse(json, out var root) && ReadBoolElement(root, key);

    private static bool ReadBoolElement(JsonElement element, string key) =>
        TryGetProperty(element, key, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool? ReadNullableBoolElement(JsonElement element, string key) =>
        TryGetProperty(element, key, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            }
            : null;

    private static string? ReadString(string json, string key) =>
        TryParse(json, out var root) && TryGetProperty(root, key, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            value = property.Value;
            return true;
        }
        return false;
    }

    private static bool TryParse(string json, out JsonElement root)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
            return true;
        }
        catch
        {
            root = default;
            return false;
        }
    }

    private static string Shorten(string? text)
    {
        var value = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 500 ? value : value[..500] + "…";
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
