using System.Diagnostics;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Core.Services.Trigger;

/// <summary>
/// 触发事件动作流水线执行器：校验 → 解析目标（本机/远程服务器）→ 逐动作执行
/// → 失败按 ContinueOnError 决定中断/续跑 → 记录日志（TriggerLog）、审计（git/备份）、状态（TriggerStateStore）。
/// </summary>
public sealed class TriggerRunner
{
    private readonly ISshService _ssh;
    private readonly IAuditLogService? _audit;
    private readonly TriggerStateStore _state;
    private readonly SemaphoreSlim _auditLock = new(1, 1);
    private bool _auditReady;

    public TriggerRunner(ISshService? ssh = null, IAuditLogService? audit = null, TriggerStateStore? state = null)
    {
        _ssh = ssh ?? new SshService();
        _audit = audit;
        _state = state ?? TriggerStateStore.Default;
    }

    public async Task<TriggerRunResult> RunAsync(TriggerConfig trigger, TriggerRunCause cause,
        AppConfig config, CancellationToken ct = default)
    {
        var causeLabel = TriggerNotice.CauseLabel(cause);
        var result = new TriggerRunResult { StartedAt = DateTime.Now };
        var sw = Stopwatch.StartNew();

        TriggerLog.Write($"[{trigger.Name}] 开始执行（{causeLabel}，目标={(trigger.TargetType == TriggerTarget.Local ? "本机" : "远程")}，{trigger.Actions?.Length ?? 0} 步）");

        // 1) 校验
        var errors = trigger.Validate();
        if (errors.Count > 0)
        {
            result.Success = false;
            result.Message = "fail:" + errors[0];
            TriggerLog.Write($"[{trigger.Name}] 配置校验失败: {string.Join("；", errors)}");
            return Finish(result, trigger, cause, causeLabel, sw, _state);
        }

        // 2) 解析目标
        SshServerConfig? server = null;
        if (trigger.TargetType == TriggerTarget.Remote)
        {
            server = (config.Servers ?? Array.Empty<SshServerConfig>())
                .FirstOrDefault(s => s.Id == trigger.ServerId);
            if (server is null)
            {
                result.Success = false;
                result.Message = "fail:目标服务器不存在或已删除";
                TriggerLog.Write($"[{trigger.Name}] {result.Message}");
                return Finish(result, trigger, cause, causeLabel, sw, _state);
            }
            if (server.Disabled)
            {
                result.Success = false;
                result.Message = $"fail:目标服务器「{server.Name}」已禁用";
                TriggerLog.Write($"[{trigger.Name}] {result.Message}");
                return Finish(result, trigger, cause, causeLabel, sw, _state);
            }
        }

        // 3) 逐动作执行
        var actions = trigger.Actions ?? Array.Empty<TriggerAction>();
        for (var i = 0; i < actions.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var action = actions[i];
            var vars = BuildVars(trigger, cause, result.Steps, result.StartedAt);

            TriggerLog.Write($"[{trigger.Name}] 动作 {i + 1}/{actions.Length}: {action.Summarize()}");

            var step = action.Type switch
            {
                TriggerActionType.GitCommitPush => await GitAction.RunAsync(new TriggerActionContext
                {
                    Trigger = trigger, Action = action, Vars = vars, Server = server, Ssh = _ssh, Config = config
                }, ct),
                TriggerActionType.Webhook => await RunWebhookAsync(trigger, action, vars, ct),
                TriggerActionType.Backup => await BackupAction.RunAsync(new TriggerActionContext
                {
                    Trigger = trigger, Action = action, Vars = vars, Server = server, Ssh = _ssh, Config = config
                }, ct),
                TriggerActionType.DatabaseBackup => await DatabaseBackupAction.RunAsync(new TriggerActionContext
                {
                    Trigger = trigger, Action = action, Vars = vars, Server = server, Ssh = _ssh, Config = config
                }, ct),
                _ => new TriggerActionResult { Type = action.Type, Success = false, Error = "未知动作类型" }
            };

            result.Steps.Add(step);
            TriggerLog.Write($"[{trigger.Name}]   {(step.Success ? "成功" : "失败")}（{step.Duration.TotalSeconds:F1}s）" +
                             (step.Success ? Truncate(step.Output, 500) : Truncate(step.Error, 500)));

            // git / 备份 / 数据库备份属于写操作 → 进审计（尽力而为）
            if (action.Type is TriggerActionType.GitCommitPush or TriggerActionType.Backup or TriggerActionType.DatabaseBackup)
                await AuditAsync(server, trigger, action, step);

            if (!step.Success && !action.ContinueOnError)
            {
                TriggerLog.Write($"[{trigger.Name}] 动作失败且未勾选「失败后继续」，中断后续动作");
                break;
            }
        }

        result.Success = result.Steps.Count == actions.Length && result.Steps.All(s => s.Success);
        var firstError = result.Steps.FirstOrDefault(s => !s.Success)?.Error;
        result.Message = result.Success ? "ok" : "fail:" + (firstError ?? "执行失败");

        return Finish(result, trigger, cause, causeLabel, sw, _state);
    }

    // ---------- webhook（需要渲染 {{notice}} 变量） ----------

    private static async Task<TriggerActionResult> RunWebhookAsync(TriggerConfig trigger, TriggerAction action,
        IReadOnlyDictionary<string, string> vars, CancellationToken ct)
    {
        var r = await WebhookSender.SendAsync(new TriggerActionContext
        {
            Trigger = trigger, Action = action, Vars = vars, Server = null, Ssh = null
        }, ct);

        return new TriggerActionResult
        {
            Type = TriggerActionType.Webhook,
            Success = r.Success,
            Output = r.Success ? $"HTTP {r.StatusCode}" : string.Empty,
            Error = r.Error
        };
    }

    // ---------- 变量 ----------

    private static Dictionary<string, string> BuildVars(TriggerConfig trigger, TriggerRunCause cause,
        IReadOnlyList<TriggerActionResult> steps, DateTime at)
    {
        var allOk = steps.All(s => s.Success);
        var firstError = steps.FirstOrDefault(s => !s.Success)?.Error ?? string.Empty;
        var stepLines = steps.Select((s, i) =>
            $"{i + 1}. [{(s.Success ? "✓" : "✗")}] {TriggerNotice.ShortType(s.Type)}: {Truncate(s.Success ? s.Output : s.Error, 200)}");
        var stepsText = string.Join("\n", stepLines);

        // notice 用「截至当前」的结果（webhook 步骤执行前，反映已完成步骤）
        var notice = TriggerNotice.Build(trigger, cause, allOk, steps.ToList(), at);

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["trigger"] = trigger.Name,
            ["triggerId"] = trigger.Id,
            ["timestamp"] = at.ToString("yyyy-MM-dd HH:mm:ss"),
            ["date"] = at.ToString("yyyyMMdd"),
            ["cause"] = TriggerNotice.CauseLabel(cause),
            ["result"] = allOk ? "成功" : "失败",
            ["error"] = firstError,
            ["steps"] = stepsText,
            ["notice"] = notice
        };
    }

    // ---------- 收尾：状态落盘 ----------

    private static TriggerRunResult Finish(TriggerRunResult result, TriggerConfig trigger,
        TriggerRunCause cause, string causeLabel, Stopwatch sw, TriggerStateStore state)
    {
        result.Duration = sw.Elapsed;
        var summary = result.Steps.Count == 0
            ? result.Message
            : string.Join(" / ", result.Steps.Select(s => $"{TriggerNotice.ShortType(s.Type)}:{(s.Success ? "✓" : "✗")}"));

        try
        {
            state.Save(trigger.Id, result.StartedAt, result.Message, result.Success, summary, causeLabel);
        }
        catch
        {
            // 状态落盘失败不影响结果
        }

        TriggerLog.Write($"[{trigger.Name}] 结束: {(result.Success ? "成功" : result.Message)}（{result.Duration.TotalSeconds:F1}s）{summary}");
        return result;
    }

    // ---------- 审计 ----------

    private async Task AuditAsync(SshServerConfig? server, TriggerConfig trigger, TriggerAction action,
        TriggerActionResult step)
    {
        if (_audit is null)
            return;
        try
        {
            if (!_auditReady)
            {
                await _auditLock.WaitAsync();
                try
                {
                    if (!_auditReady)
                    {
                        await _audit.InitializeAsync();
                        _auditReady = true;
                    }
                }
                finally { _auditLock.Release(); }
            }

            await _audit.LogCommandAsync(new CommandAuditLog
            {
                ServerId = server?.Id ?? string.Empty,
                ServerName = server?.Name ?? "本机",
                Command = $"[触发事件 {trigger.Name}] {action.Summarize()}",
                Result = Truncate(step.Success ? step.Output : step.Error, 1000),
                Status = step.Success ? CommandStatus.Executed : CommandStatus.Failed,
                ExitCode = step.ExitCode,
                Category = AuditCategory.Exec,
                Tool = "trigger"
            });
        }
        catch
        {
            // 审计失败不影响触发事件执行
        }
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return text.Length <= max ? text : text[..max] + "…";
    }
}
