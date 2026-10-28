using LitSSHmcp.Agent;

namespace LitSSHmcp.App.ViewModels;

public partial class AgentViewModel
{
    private IFleetPlanStore? _fleetPlanStore;
    private IFleetPlanStore FleetPlanStore => _fleetPlanStore ??= new SqliteFleetPlanStore();
    private CancellationTokenSource? _fleetCts;

    public bool CanInvokeFleetChange => _runtime is not null;

    private async Task TryAdoptFleetPlansAsync(string owner, long sessionId)
    {
        try
        {
            await FleetPlanStore.AdoptSessionAsync("local", owner, sessionId);
        }
        catch
        {
            // 计划接管失败不影响会话创建
        }
    }

    public async Task<string> InvokeFleetChangeAsync(string mode, string? planId, string? serverIds,
        string? command, string? rollbackCommand, int concurrency, int maxTargets, int timeoutSeconds, bool confirm)
    {
        if (_runtime is null)
            return "运行时尚未就绪，请先打开一个会话。";
        if (IsBusy || _fleetCts is not null)
            return "当前对话或批量变更正在执行，请稍后再试。";

        var cts = new CancellationTokenSource();
        _fleetCts = cts;
        var args = new Dictionary<string, object?>
        {
            ["mode"] = mode,
            ["planId"] = planId,
            ["serverIds"] = serverIds,
            ["command"] = command,
            ["rollbackCommand"] = rollbackCommand,
            ["concurrency"] = concurrency,
            ["maxTargets"] = maxTargets,
            ["timeoutSeconds"] = timeoutSeconds,
            ["confirm"] = confirm,
        };
        try
        {
            var result = await _runtime.InvokeHostToolAsync(FleetChangeTools.ToolName, args, cts.Token);
            return result?.ToString() ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            return "批量变更已取消。";
        }
        catch (Exception ex)
        {
            return $"批量变更失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_fleetCts, cts)) _fleetCts = null;
            cts.Dispose();
        }
    }

    public void CancelFleetChange()
    {
        try { _fleetCts?.Cancel(); } catch { /* 已结束 */ }
    }

    public async Task<FleetPlanState?> GetFleetPlanAsync(string planId, CancellationToken ct = default)
    {
        if (_runtime is null || string.IsNullOrWhiteSpace(planId))
            return null;
        try
        {
            return await _runtime.FleetPlans.GetAsync(planId, _runtime.FleetPlanOwnerKey,
                _runtime.FleetPlanSessionId, ct);
        }
        catch
        {
            return null;
        }
    }

    private async Task TryDeleteFleetPlansAsync(long sessionId)
    {
        try { await FleetPlanStore.DeleteBySessionAsync(sessionId); }
        catch { /* 计划清理失败不影响会话删除 */ }
    }

    public async Task<bool> DeleteFleetPlanAsync(string planId, CancellationToken ct = default)
    {
        if (_runtime is null || string.IsNullOrWhiteSpace(planId)) return false;
        try
        {
            return await _runtime.FleetPlans.DeleteScopedAsync(planId, _runtime.FleetPlanOwnerKey,
                _runtime.FleetPlanSessionId, ct);
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<FleetPlanState>> ListFleetPlansAsync(CancellationToken ct = default)
    {
        if (_runtime is null) return Array.Empty<FleetPlanState>();
        try
        {
            return await _runtime.FleetPlans.ListAsync(_runtime.FleetPlanOwnerKey,
                _runtime.FleetPlanSessionId, ct);
        }
        catch
        {
            return Array.Empty<FleetPlanState>();
        }
    }

    public async Task<string> GetFleetTargetsAsync(string? query = null, CancellationToken ct = default)
    {
        if (_runtime is null)
            return "运行时尚未就绪。";
        var args = new Dictionary<string, object?> { ["query"] = query };
        try
        {
            var result = await _runtime.InvokeHostToolAsync(FleetTargetTools.ToolName, args, ct);
            return result?.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            return $"加载目标失败：{ex.Message}";
        }
    }
}
