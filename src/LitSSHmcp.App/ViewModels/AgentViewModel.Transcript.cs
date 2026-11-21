using System.Windows.Input;

namespace LitSSHmcp.App.ViewModels;

public partial class AgentViewModel
{
    private int _visibleFrom;
    private const int TranscriptPageSize = 10;
    private long? _oldestLoadedUserId;
    private bool _hasOlderMessages;
    private bool _loadingOlderHistory;

    /// <summary>是否还有更早的记录可加载（已加载但折叠的，或数据库中还有更早轮次）。</summary>
    public bool HasMoreHistory => _visibleFrom > 0 || _hasOlderMessages;

    public ICommand LoadOlderHistoryCommand { get; }

    /// <summary>即将在顶部插入更早内容（视图据此记录滚动锚点，插入后回补偏移）。</summary>
    public event Action? OlderHistoryApplying;

    /// <summary>展示更多更早的轮次（每页 <see cref="TranscriptPageSize"/> 轮）；内存不够时向数据库翻页。</summary>
    public async Task LoadOlderHistoryAsync()
    {
        if (_visibleFrom >= TranscriptPageSize)
        {
            OlderHistoryApplying?.Invoke();
            _visibleFrom = Math.Max(0, _visibleFrom - TranscriptPageSize);
            RefreshTranscriptView();
            return;
        }

        if (!_hasOlderMessages || _sessionId <= 0 || _loadingOlderHistory)
            return;

        _loadingOlderHistory = true;
        var sessionId = _sessionId;
        try
        {
            var page = await _store.GetMessagePageAsync(sessionId, TranscriptPageSize, _oldestLoadedUserId);
            if (_sessionId != sessionId)
                return;
            if (page.Rows.Count == 0)
            {
                _hasOlderMessages = false;
                RaiseHistoryState();
                return;
            }

            var (olderTurns, _) = ParseTranscriptRows(page.Rows);
            if (olderTurns.Count == 0)
            {
                _hasOlderMessages = page.HasMore;
                RaiseHistoryState();
                return;
            }

            var combined = new List<AgentTurn>(olderTurns.Count + Turns.Count);
            combined.AddRange(olderTurns);
            combined.AddRange(Turns);
            OlderHistoryApplying?.Invoke();
            Turns.ReplaceRange(combined);
            CurrentContext.LiveTurns = Turns.ToList();

            _oldestLoadedUserId = page.OldestUserId ?? _oldestLoadedUserId;
            CurrentContext.OldestLoadedUserId = _oldestLoadedUserId;
            _hasOlderMessages = page.HasMore;
            RenumberTurns();
            _visibleFrom = Math.Max(0, olderTurns.Count - TranscriptPageSize);
            RefreshTranscriptView();
        }
        catch (Exception ex)
        {
            StatusMessage = "加载更早记录失败: " + ex.Message;
        }
        finally
        {
            _loadingOlderHistory = false;
        }
    }

    private void RefreshTranscriptView()
    {
        TurnsView?.Refresh();
        RaiseHistoryState();
    }

    private void RenumberTurns()
    {
        for (var i = 0; i < Turns.Count; i++)
            Turns[i].UiOrdinal = i;
    }

    private void RaiseHistoryState()
    {
        OnPropertyChanged(nameof(HasMoreHistory));
        (LoadOlderHistoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>重置转录窗口：重算各轮序号，只显示最后 <see cref="TranscriptPageSize"/> 轮。</summary>
    private void ResetTranscriptWindow()
    {
        RenumberTurns();
        _visibleFrom = Math.Max(0, Turns.Count - TranscriptPageSize);
        RefreshTranscriptView();
    }
}
