using LitSSHmcp.Agent;
using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.ViewModels;

public partial class AgentViewModel
{
    // ---- 跨进程对账（Web 进程也会写 agent.db）：可见且空闲时轻量比较会话列表/选中会话更新时间 ----
    private CancellationTokenSource? _autoRefreshCts;
    private string _lastSessionSnapshot = string.Empty;
    private readonly Dictionary<long, string> _loadedSessionUpdatedAt = new();
    private DateTime _lastSessionChangeUtc = DateTime.UtcNow;
    private bool _isViewVisible = true;
    private static readonly TimeSpan FastRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleRefreshInterval = TimeSpan.FromSeconds(20);

    /// <summary>启动跨进程对账循环：刚有变化时 5s 快轮询，长时间无变化降到 20s；页面不可见/任务进行中不轮询。</summary>
    private void StartAutoRefresh()
    {
        if (_autoRefreshCts is not null)
            return;
        _autoRefreshCts = new CancellationTokenSource();
        _ = AutoRefreshLoopAsync(_autoRefreshCts.Token);
    }

    /// <summary>页面可见性变化：不可见时暂停对账；重新可见立即补一次。</summary>
    public void SetViewVisible(bool visible)
    {
        if (_isViewVisible == visible)
            return;
        _isViewVisible = visible;
        if (visible)
            _ = AutoRefreshTickAsync();
    }

    private async Task AutoRefreshLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var idleFor = DateTime.UtcNow - _lastSessionChangeUtc;
                var delay = idleFor < TimeSpan.FromMinutes(2) ? FastRefreshInterval : IdleRefreshInterval;
                await Task.Delay(delay, ct);
                _uiInvoke(() => _ = AutoRefreshTickAsync());
            }
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭
        }
    }

    /// <summary>
    /// 轻量对账：会话列表快照变化 → 重载列表；当前会话空闲且其 UpdatedAt 变化 → 重载该会话消息。
    /// 本地有会话执行中/正在编辑时不打扰。跨进程只能看到已落库的整轮结果（无法实时流式）。
    /// </summary>
    private async Task AutoRefreshTickAsync()
    {
        if (_autoRefreshCts is null || !_isViewVisible || IsLoading || HasRunningSessions || _editingTurn is not null)
            return;
        try
        {
            var rows = await _store.ListSessionsAsync();
            if (_autoRefreshCts is null || !_isViewVisible || IsLoading || HasRunningSessions || _editingTurn is not null)
                return;
            var snapshot = SessionAutoRefresh.Snapshot(rows);
            if (!string.Equals(snapshot, _lastSessionSnapshot, StringComparison.Ordinal))
            {
                _lastSessionChangeUtc = DateTime.UtcNow;
                _lastSessionSnapshot = snapshot;
                await ReloadSessionsAsync();
            }

            if (_sessionId <= 0 || _editingTurn is not null)
                return;
            var latest = Sessions.FirstOrDefault(s => s.Id == _sessionId);
            if (latest is null)
                return;
            _loadedSessionUpdatedAt.TryGetValue(_sessionId, out var loaded);
            if (!SessionAutoRefresh.ShouldReloadSelected(loaded, latest.UpdatedAt))
                return;
            await LoadSessionAsync(_sessionId);
        }
        catch
        {
            // 对账失败忽略（下一轮重试）
        }
    }

    public async Task ReloadSessionsAsync()
    {
        var list = await _store.ListSessionsAsync();
        _lastSessionSnapshot = SessionAutoRefresh.Snapshot(list);
        // 后台会话轮结束也会触发本刷新：先保住当前选中，避免正在显示的会话被列表重建清掉选中
        var keepId = _selectedSession?.Id;
        if (SessionAutoRefresh.SameStructure(Sessions, list))
        {
            // 仅 UpdatedAt 等非结构字段变化：原地换行，不清空 ObservableCollection，避免侧栏“消失又出现”。
            for (var i = 0; i < list.Count; i++)
                if (!Equals(Sessions[i], list[i]))
                    Sessions[i] = list[i];
        }
        else
        {
            Sessions.Clear();
            foreach (var s in list)
                Sessions.Add(s);
            ApplySessionFilter();
        }
        if (keepId is { } id)
        {
            var again = Sessions.FirstOrDefault(s => s.Id == id);
            if (!ReferenceEquals(again, _selectedSession))
            {
                _suppressSelection = true;
                SelectedSession = again;
                _suppressSelection = false;
                SyncSidebarSelection();
            }
        }
    }
}
