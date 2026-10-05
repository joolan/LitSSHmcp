using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.App.ViewModels;

/// <summary>AI 运维助手：按"轮"组织的多会话对话（内嵌 MCP 客户端驱动）。</summary>
public class AgentViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IConfigService _configService;
    private readonly IContextStore _store;
    private readonly string _bundledSkillsDir;
    private readonly Action<Action> _uiInvoke;

    private AgentConfig _agentConfig = new();
    private AgentRuntime? _runtime;
    private CancellationTokenSource? _cts;
    private bool _suppressSelection;
    private AgentTurn? _editingTurn;
    private readonly StringBuilder _pendingDelta = new();
    private DateTime _lastFlushUtc = DateTime.MinValue;
    private const int StreamFlushMs = 150;

    public AgentViewModel(IConfigService configService, IContextStore store, string bundledSkillsDir, Action<Action>? uiInvoke = null)
    {
        _configService = configService;
        _store = store;
        _bundledSkillsDir = bundledSkillsDir;
        _uiInvoke = uiInvoke ?? (action => action());

        SendCommand = new RelayCommand(_ => _ = SendAsync(), _ => !IsBusy && !string.IsNullOrWhiteSpace(Input));
        StopCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
        SendOrStopCommand = new RelayCommand(_ => { if (IsBusy) _cts?.Cancel(); else _ = SendAsync(); },
            _ => IsBusy || !string.IsNullOrWhiteSpace(Input));
        NewSessionCommand = new RelayCommand(_ => _ = NewSessionAsync(), _ => !IsBusy);
        DeleteSessionCommand = new RelayCommand(_ => _ = DeleteSessionAsync(), _ => !IsBusy && SelectedSession != null);
        ClearCommand = new RelayCommand(_ => { Turns.Clear(); _editingTurn = null; RefreshContextInfo(); });
        ContinueCommand = new RelayCommand(_ => _ = SendTextAsync("请继续"), _ => !IsBusy);
        RegenerateCommand = new RelayCommand(p => _ = RegenerateAsync(p as AgentTurn), p => !IsBusy && p is AgentTurn);
        RetryStepCommand = new RelayCommand(p => _ = RetryStepAsync(p as AgentStep), p => !IsBusy && p is AgentStep);
        TestConnectionCommand = new RelayCommand(_ => _ = TestConnectionAsync(), _ => !IsBusy && SelectedProvider != null);
        CancelEditCommand = new RelayCommand(_ => CancelEdit(), _ => IsEditing);

        Plan.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(PlanHeader));
            OnPropertyChanged(nameof(PlanVisibility));
        };

        Turns.CollectionChanged += (_, _) => OnPropertyChanged(nameof(EmptyStateVisibility));
    }

    public ObservableCollection<AgentProviderConfig> Providers { get; } = new();

    /// <summary>模型下拉列表 = 可用模型 + 末尾固定的“模型设置”入口。</summary>
    public ObservableCollection<AgentProviderConfig> ComboProviders { get; } = new();

    private readonly AgentProviderConfig _modelSettingsEntry = new() { Id = "\u0001model-settings", Name = "模型设置" };

    /// <summary>请求打开设置窗口（参数为初始 Tab：null=助手设置 / "model"=大模型设置）。</summary>
    public event Action<string?>? SettingsRequested;

    private void RebuildComboProviders()
    {
        ComboProviders.Clear();
        foreach (var p in Providers)
            ComboProviders.Add(p);
        ComboProviders.Add(_modelSettingsEntry);
    }
    public ObservableCollection<AgentSessionRow> Sessions { get; } = new();
    public ObservableCollection<AgentSessionRow> FilteredSessions { get; } = new();
    public ObservableCollection<AgentTurn> Turns { get; } = new();

    /// <summary>挑选可用模型：优先指定 id 且模型名非空 → 任一模型名非空 → 指定 id → 第一个。</summary>
    private AgentProviderConfig? PickProvider(string? preferredId) =>
        Providers.FirstOrDefault(p => p.Id == preferredId && !string.IsNullOrWhiteSpace(p.Model))
        ?? Providers.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Model))
        ?? Providers.FirstOrDefault(p => p.Id == preferredId)
        ?? Providers.FirstOrDefault();

    /// <summary>当前任务计划（update_plan 工具驱动的进度清单）。</summary>
    public ObservableCollection<PlanItemVM> Plan { get; } = new();

    public string PlanHeader => $"任务计划 ({Plan.Count(p => p.Done)}/{Plan.Count})";
    public Visibility PlanVisibility => Plan.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>无对话轮次时显示空态引导。</summary>
    public Visibility EmptyStateVisibility => Turns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public ICommand SendCommand { get; }
    public ICommand StopCommand { get; }

    /// <summary>合并的"发送/停止"按钮：空闲时发送、运行中停止。</summary>
    public ICommand SendOrStopCommand { get; }

    public string SendButtonText => IsBusy ? "停止" : "发送";

    /// <summary>工作区目录（AI 文档工具 ops_doc_* 的根目录）。</summary>
    public string WorkspaceDir => WorkspaceTools.ResolveDir(_agentConfig.WorkspaceDir);
    public ICommand NewSessionCommand { get; }
    public ICommand DeleteSessionCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ContinueCommand { get; }
    public ICommand RegenerateCommand { get; }
    public ICommand RetryStepCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand CancelEditCommand { get; }

    private long _sessionId;
    private bool _sessionTitled;

    private AgentProviderConfig? _selectedProvider;
    public AgentProviderConfig? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            // 下拉末尾的“模型设置”入口：打开设置窗口的大模型设置 Tab，并还原选择
            if (ReferenceEquals(value, _modelSettingsEntry))
            {
                SettingsRequested?.Invoke("model");
                OnPropertyChanged(nameof(SelectedProvider));
                return;
            }

            var changed = !ReferenceEquals(_selectedProvider, value);
            if (!Set(ref _selectedProvider, value)) return;
            (TestConnectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
            if (changed && !_suppressSelection && _runtime is not null)
                _ = SwitchProviderAsync();
            if (changed && !_suppressSelection && value is not null)
                _ = PersistProviderSelectionAsync(value.Id);
        }
    }

    private AgentSessionRow? _selectedSession;
    public AgentSessionRow? SelectedSession
    {
        get => _selectedSession;
        set
        {
            // 任务进行中禁止切换会话（同一时间只允许一个任务），并提示用户
            if (IsBusy && !_suppressSelection && !ReferenceEquals(_selectedSession, value))
            {
                StatusMessage = "当前会话任务进行中：同一时间只能有一个任务；请等它完成或点「停止」后再切换会话。";
                OnPropertyChanged(nameof(SelectedSession));   // 还原列表选中项
                return;
            }

            var changed = !ReferenceEquals(_selectedSession, value);
            if (!Set(ref _selectedSession, value)) return;
            (DeleteSessionCommand as RelayCommand)?.RaiseCanExecuteChanged();
            if (changed && !_suppressSelection && value is not null)
                _ = LoadSessionAsync(value.Id);
        }
    }

    private string _sessionFilter = string.Empty;
    public string SessionFilter
    {
        get => _sessionFilter;
        set { if (Set(ref _sessionFilter, value)) ApplySessionFilter(); }
    }

    private string _input = string.Empty;
    public string Input
    {
        get => _input;
        set { if (Set(ref _input, value)) { (SendCommand as RelayCommand)?.RaiseCanExecuteChanged(); (SendOrStopCommand as RelayCommand)?.RaiseCanExecuteChanged(); } }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!Set(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(SendButtonText));
            foreach (var cmd in new[] { SendCommand, StopCommand, SendOrStopCommand, NewSessionCommand, DeleteSessionCommand, ContinueCommand, RegenerateCommand, RetryStepCommand, TestConnectionCommand })
                (cmd as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private string _statusMessage = "加载中…";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    private string _contextInfo = string.Empty;
    public string ContextInfo { get => _contextInfo; set => Set(ref _contextInfo, value); }

    /// <summary>当前模型连接状态：idle / connecting / ready / error（用于模型选择框的状态标识）。</summary>
    private string _modelStatus = "idle";
    public string ModelStatus { get => _modelStatus; set => Set(ref _modelStatus, value); }

    /// <summary>上下文占用比例 0..1（用于环形进度，图标展示）。</summary>
    private double _contextPercent;
    public double ContextPercent { get => _contextPercent; set => Set(ref _contextPercent, value); }

    public bool IsEditing => _editingTurn != null;

    public async Task InitializeAsync()
    {
        try
        {
            await _store.InitializeAsync();
            var config = await _configService.LoadConfigAsync();
            _agentConfig = config.Agent ?? new AgentConfig();
            try
            {
                await _store.PruneAsync(_agentConfig.RetentionMaxSessions, _agentConfig.RetentionMaxMessages, _agentConfig.RetentionDays, CancellationToken.None);
            }
            catch
            {
                // 启动裁剪失败忽略
            }

            _suppressSelection = true;
            Providers.Clear();
            foreach (var p in _agentConfig.Providers.Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Model) && !string.IsNullOrWhiteSpace(p.Endpoint)))
                Providers.Add(p);
            RebuildComboProviders();
            SelectedProvider = PickProvider(_agentConfig.ActiveProviderId);

            await ReloadSessionsAsync();
            var target = Sessions.FirstOrDefault();
            if (target is null)
            {
                _sessionId = await _store.CreateSessionAsync("运维会话");
                await ReloadSessionsAsync();
                target = Sessions.FirstOrDefault();
            }
            SelectedSession = target;
            _suppressSelection = false;

            if (target is not null)
                await LoadSessionAsync(target.Id);

            if (Providers.Count == 0)
                StatusMessage = "未配置大模型：请点「AI 助手设置」添加并启用一个 OpenAI 兼容模型。";
        }
        catch (Exception ex)
        {
            StatusMessage = "初始化失败: " + ex.Message;
        }
    }

    private async Task ReloadSessionsAsync()
    {
        var list = await _store.ListSessionsAsync();
        Sessions.Clear();
        foreach (var s in list)
            Sessions.Add(s);
        ApplySessionFilter();
    }

    private void ApplySessionFilter()
    {
        FilteredSessions.Clear();
        var keyword = SessionFilter?.Trim() ?? string.Empty;
        foreach (var s in Sessions.Where(s => keyword.Length == 0 || s.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            FilteredSessions.Add(s);
    }

    private async Task LoadSessionAsync(long id)
    {
        _sessionId = id;
        _editingTurn = null;
        Turns.Clear();
        Plan.Clear();

        // 切换会话：优先用该会话最后使用的模型；已删除/停用则回退全局默认 → 第一个启用模型
        var session = Sessions.FirstOrDefault(s => s.Id == id);
        _suppressSelection = true;
        SelectedProvider = PickProvider(session?.ProviderId ?? _agentConfig.ActiveProviderId);
        _suppressSelection = false;
        if (SelectedProvider is not null && !string.Equals(session?.ProviderId, SelectedProvider.Id, StringComparison.Ordinal))
            _ = PersistProviderSelectionAsync(SelectedProvider.Id);

        var rows = await _store.GetMessagesAsync(id);
        _sessionTitled = rows.Any(r => r.Role == "user");

        AgentTurn? current = null;
        var history = new List<ChatMessage>();
        foreach (var row in rows)
        {
            if (row.Role == "user")
            {
                current = new AgentTurn(row.Content, ParseUtc(row.Timestamp));
                Turns.Add(current);
                history.Add(new ChatMessage(ChatRole.User, row.Content));
            }
            else if (row.Role == "assistant")
            {
                current ??= AddPlaceholderTurn(row.Timestamp);
                current.AssistantText = row.Content;
                current.IsRunning = false;
                current.SetTimes(LocalHms(row.Timestamp), string.Empty);
                history.Add(new ChatMessage(ChatRole.Assistant, row.Content));
            }
        }

        await ResetRuntimeAsync(history);
    }

    private AgentTurn AddPlaceholderTurn(string timestamp)
    {
        var turn = new AgentTurn(string.Empty, ParseUtc(timestamp));
        Turns.Add(turn);
        return turn;
    }

    public async Task NewSessionAsync()
    {
        _sessionId = await _store.CreateSessionAsync("运维会话");
        _sessionTitled = false;
        Turns.Clear();
        Plan.Clear();
        _editingTurn = null;
        await ReloadSessionsAsync();
        _suppressSelection = true;
        SelectedSession = Sessions.FirstOrDefault(s => s.Id == _sessionId);
        _suppressSelection = false;
        if (SelectedProvider is not null)
            _ = PersistProviderSelectionAsync(SelectedProvider.Id);
        await ResetRuntimeAsync(null);
    }

    private Task DeleteSessionAsync() => DeleteSessionAsync(SelectedSession);

    /// <summary>删除指定会话；若删除的是当前会话才切换到其他会话，否则保持当前显示。</summary>
    public async Task DeleteSessionAsync(AgentSessionRow? session)
    {
        if (session is null) return;
        var wasCurrent = session.Id == _sessionId;
        await _store.DeleteSessionAsync(session.Id);
        await ReloadSessionsAsync();

        var next = Sessions.FirstOrDefault();
        if (next is null)
        {
            _sessionId = await _store.CreateSessionAsync("运维会话");
            await ReloadSessionsAsync();
            next = Sessions.FirstOrDefault();
            wasCurrent = true;
        }

        _suppressSelection = true;
        SelectedSession = wasCurrent ? next : Sessions.FirstOrDefault(s => s.Id == _sessionId);
        _suppressSelection = false;
        if (wasCurrent && next is not null)
            await LoadSessionAsync(next.Id);
    }

    public Task RenameSessionAsync(string newTitle) => RenameSessionAsync(SelectedSession, newTitle);

    /// <summary>重命名指定会话（用于右键菜单，不影响当前选中）。</summary>
    public async Task RenameSessionAsync(AgentSessionRow? session, string newTitle)
    {
        if (session is null || string.IsNullOrWhiteSpace(newTitle))
            return;
        await _store.RenameSessionAsync(session.Id, newTitle.Trim());
        await ReloadSessionsAsync();
        _suppressSelection = true;
        SelectedSession = Sessions.FirstOrDefault(s => s.Id == _sessionId);
        _suppressSelection = false;
    }

    /// <summary>当前显示的会话 Id。</summary>
    public long CurrentSessionId => _sessionId;

    /// <summary>读取某会话的持久化消息（用于对非当前会话导出）。</summary>
    public Task<List<AgentMessageRow>> GetMessagesAsync(long sessionId) => _store.GetMessagesAsync(sessionId);

    /// <summary>清空指定会话的消息（保留会话）；若为当前会话则同步清空显示。</summary>
    public async Task ClearSessionMessagesAsync(AgentSessionRow? session)
    {
        if (session is null)
            return;
        await _store.ReplaceMessagesAsync(session.Id, Array.Empty<(string Role, string Content)>());
        if (session.Id == _sessionId)
        {
            Turns.Clear();
            _editingTurn = null;
            Plan.Clear();
            OnPropertyChanged(nameof(IsEditing));
            RefreshContextInfo();
        }
    }

    private async Task EnsureRuntimeAsync(IEnumerable<ChatMessage>? history)
    {
        if (SelectedProvider is null)
            return;

        if (_runtime is not null)
        {
            await _runtime.DisposeAsync();
            _runtime = null;
        }

        try
        {
            ModelStatus = "connecting";
            StatusMessage = "正在连接 MCP 服务器…";
            _runtime = await AgentRuntime.StartAsync(_agentConfig, SelectedProvider, _bundledSkillsDir, history, UpdatePlan);
            StatusMessage = $"已就绪 · {SelectedProvider.Name} / {SelectedProvider.Model} · {_runtime.ToolCount} 个工具"
                            + (_agentConfig.ReadOnly ? " · 只读模式" : "");
            ModelStatus = "ready";
            RefreshContextInfo();
        }
        catch (Exception ex)
        {
            ModelStatus = "error";
            StatusMessage = "连接失败: " + ChatErrorClassifier.Describe(ex);
        }
    }

    /// <summary>切换会话/模型：复用已连接的 MCP 宿主，仅重建对话会话（不重连 MCP）。</summary>
    private async Task ResetRuntimeAsync(IEnumerable<ChatMessage>? history)
    {
        if (SelectedProvider is null)
            return;

        if (_runtime is null)
        {
            await EnsureRuntimeAsync(history);
            return;
        }

        try
        {
            _runtime.ResetSession(SelectedProvider, history);
            StatusMessage = $"已就绪 · {SelectedProvider.Name} / {SelectedProvider.Model} · {_runtime.ToolCount} 个工具"
                            + (_agentConfig.ReadOnly ? " · 只读模式" : "");
            ModelStatus = "ready";
            RefreshContextInfo();
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            ModelStatus = "error";
            StatusMessage = "切换失败: " + ChatErrorClassifier.Describe(ex);
        }
    }

    public async Task ReloadProvidersAsync()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            _agentConfig = config.Agent ?? new AgentConfig();
            _suppressSelection = true;
            Providers.Clear();
            foreach (var p in _agentConfig.Providers.Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Model) && !string.IsNullOrWhiteSpace(p.Endpoint)))
                Providers.Add(p);
            RebuildComboProviders();
            var session = Sessions.FirstOrDefault(s => s.Id == _sessionId);
            SelectedProvider = PickProvider(session?.ProviderId ?? _agentConfig.ActiveProviderId);
            _suppressSelection = false;

            if (SelectedProvider is null)
            {
                StatusMessage = "未配置大模型：请在「AI 助手设置」中添加并启用。";
                return;
            }
            await EnsureRuntimeAsync(await LoadHistoryFromStoreAsync());
        }
        catch (Exception ex)
        {
            StatusMessage = "重载失败: " + ex.Message;
        }
    }

    private async Task SwitchProviderAsync()
    {
        if (IsBusy || SelectedProvider is null) return;
        var history = await LoadHistoryFromStoreAsync();
        StatusMessage = $"切换到 {SelectedProvider.Name} …";
        await ResetRuntimeAsync(history);
    }

    private async Task<List<ChatMessage>> LoadHistoryFromStoreAsync()
    {
        var history = new List<ChatMessage>();
        foreach (var row in await _store.GetMessagesAsync(_sessionId))
        {
            if (row.Role == "user") history.Add(new ChatMessage(ChatRole.User, row.Content));
            else if (row.Role == "assistant") history.Add(new ChatMessage(ChatRole.Assistant, row.Content));
        }
        return history;
    }

    /// <summary>记录当前会话最后使用的模型（并更新全局默认），用于切换会话时恢复。</summary>
    private async Task PersistProviderSelectionAsync(string providerId)
    {
        try
        {
            if (_sessionId > 0)
                await _store.SetSessionProviderAsync(_sessionId, providerId);

            if (!string.Equals(_agentConfig.ActiveProviderId, providerId, StringComparison.Ordinal))
            {
                _agentConfig.ActiveProviderId = providerId;
                var cfg = await _configService.LoadConfigAsync();
                (cfg.Agent ??= new AgentConfig()).ActiveProviderId = providerId;
                await _configService.SaveConfigAsync(cfg);
            }
        }
        catch
        {
            // 记录失败不影响主流程
        }
    }

    public Task SendAsync() => SendTextAsync(null);

    /// <summary>发送一轮对话；<paramref name="fixedText"/> 非空时使用该文本（用于"继续"）。</summary>
    private async Task SendTextAsync(string? fixedText)
    {
        if (_runtime is null)
            await EnsureRuntimeAsync(await LoadHistoryFromStoreAsync());
        if (_runtime is null)
            return;

        // 编辑模式下：截断该轮之后的所有轮次与会话上下文
        if (_editingTurn is not null)
        {
            var idx = Turns.IndexOf(_editingTurn);
            if (idx >= 0)
            {
                for (var i = Turns.Count - 1; i > idx; i--)
                    Turns.RemoveAt(i);
                RebuildSessionHistoryUpTo(idx);
                _editingTurn.ResetForRerun(fixedText ?? _editingTurn.UserText);
                var editTurn = _editingTurn;
                _editingTurn = null;
                OnPropertyChanged(nameof(IsEditing));
                await RunTurnAsync(editTurn);
                return;
            }
            _editingTurn = null;
            OnPropertyChanged(nameof(IsEditing));
        }

        var text = (fixedText ?? Input)?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        if (fixedText is null)
        {
            Input = string.Empty;
            Plan.Clear();   // 新指令 = 新任务：清掉上一轮残留的计划(模型如需会重新 update_plan)
        }

        var turn = new AgentTurn(text, DateTime.UtcNow);
        Turns.Add(turn);
        await RunTurnAsync(turn);
    }

    private async Task RunTurnAsync(AgentTurn turn)
    {
        if (_runtime is null)
            return;

        // 首次用户消息自动命名
        if (_sessionTitled is false)
        {
            var title = turn.UserText.Length <= 20 ? turn.UserText : turn.UserText[..20];
            await _store.RenameSessionAsync(_sessionId, title);
            _sessionTitled = true;
        }

        IsBusy = true;
        _cts = new CancellationTokenSource();
        var assistantText = new StringBuilder();
        _pendingDelta.Clear();
        _lastFlushUtc = DateTime.MinValue;
        var progress = new SyncProgress(e => OnAgentEvent(e, turn, assistantText), _uiInvoke);

        try
        {
            await _runtime.Session.SendAsync(turn.UserText, progress, _cts.Token);
            StatusMessage = "完成";
        }
        catch (OperationCanceledException)
        {
            turn.Note = "已停止。";
        }
        catch (Exception ex)
        {
            var reason = ChatErrorClassifier.Describe(ex);
            turn.Note = reason;
            StatusMessage = reason;
        }
        finally
        {
            FlushDelta(turn);
            turn.MarkDone(DateTime.UtcNow);
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            await PersistTranscriptAsync();
            try
            {
                await _store.PruneAsync(_agentConfig.RetentionMaxSessions, _agentConfig.RetentionMaxMessages, _agentConfig.RetentionDays, CancellationToken.None);
            }
            catch
            {
                // 裁剪失败忽略
            }
            await _store.TouchSessionAsync(_sessionId);
            try
            {
                if (_runtime is not null)
                    await _runtime.IndexTurnAsync(turn.UserText, assistantText.ToString());
            }
            catch
            {
                // 记忆写入失败忽略
            }
            await ReloadSessionsAsync();
            _suppressSelection = true;
            SelectedSession = Sessions.FirstOrDefault(s => s.Id == _sessionId);
            _suppressSelection = false;
            RefreshContextInfo();
        }
    }

    /// <summary>重新生成最后一条回答。</summary>
    private async Task RegenerateAsync(AgentTurn? turn)
    {
        if (turn is null || IsBusy || _runtime is null)
            return;
        var idx = Turns.IndexOf(turn);
        if (idx < 0 || idx != Turns.Count - 1)
        {
            StatusMessage = "只能重新生成最后一轮。";
            return;
        }

        RebuildSessionHistoryUpTo(idx);
        turn.ResetForRerun(turn.UserText);
        await RunTurnAsync(turn);
    }

    /// <summary>进入编辑：把该轮用户指令载入输入框，发送时将替换该指令及其后的对话。</summary>
    public void BeginEdit(AgentTurn? turn)
    {
        if (turn is null || IsBusy)
            return;
        _editingTurn = turn;
        Input = turn.UserText;
        OnPropertyChanged(nameof(IsEditing));
        (CancelEditCommand as RelayCommand)?.RaiseCanExecuteChanged();
        StatusMessage = "编辑中：按「发送」将替换该指令及其后的对话；按「取消编辑」放弃。";
    }

    public void CancelEdit()
    {
        if (_editingTurn is null) return;
        _editingTurn = null;
        Input = string.Empty;
        OnPropertyChanged(nameof(IsEditing));
        (CancelEditCommand as RelayCommand)?.RaiseCanExecuteChanged();
        StatusMessage = "已取消编辑";
    }

    private async Task RetryStepAsync(AgentStep? step)
    {
        if (step is null || IsBusy || _runtime is null)
            return;

        IDictionary<string, object?>? args = null;
        if (!string.IsNullOrWhiteSpace(step.ArgsJson))
        {
            try { args = JsonSerializer.Deserialize<Dictionary<string, object?>>(step.ArgsJson); }
            catch { args = null; }
        }

        step.Restart();
        StatusMessage = $"重试工具 {step.Tool} …";
        var (success, result, durationMs) = await _runtime.Session.InvokeToolAsync(step.Tool, args, CancellationToken.None);
        step.Complete(success, result, durationMs);
        StatusMessage = success ? $"工具 {step.Tool} 重试成功" : $"工具 {step.Tool} 重试仍失败";
    }

    private async Task TestConnectionAsync()
    {
        if (SelectedProvider is null)
            return;

        IsBusy = true;
        try
        {
            var client = ChatClientFactory.Create(SelectedProvider);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await client.GetResponseAsync(
                new[] { new ChatMessage(ChatRole.User, "ping") },
                new ChatOptions { MaxOutputTokens = 8, Temperature = 0 },
                CancellationToken.None);
            stopwatch.Stop();
            StatusMessage = $"连接正常 · {SelectedProvider.Name}（{stopwatch.ElapsedMilliseconds} ms）";
        }
        catch (Exception ex)
        {
            StatusMessage = "连接失败: " + ChatErrorClassifier.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnAgentEvent(AgentEvent e, AgentTurn turn, StringBuilder assistantText)
    {
        switch (e.Kind)
        {
            case AgentEventKind.AssistantText:
                var chunk = e.Text ?? string.Empty;
                if (chunk.Length == 0) break;
                assistantText.Append(chunk);
                _pendingDelta.Append(chunk);
                if ((DateTime.UtcNow - _lastFlushUtc).TotalMilliseconds >= StreamFlushMs)
                    FlushDelta(turn);
                break;

            case AgentEventKind.ToolCall:
                FlushDelta(turn);
                turn.Steps.Add(new AgentStep(e.ToolName ?? "工具", e.ArgumentsJson ?? string.Empty, e.Destructive));
                break;

            case AgentEventKind.ToolResult:
                FlushDelta(turn);
                var body = Truncate(e.Text, 20000);
                var denied = LooksApprovalDenied(e.Text);
                var pending = turn.Steps.LastOrDefault(s => s.IsRunning && s.Tool == (e.ToolName ?? "工具"));
                pending?.Complete(e.ToolSuccess && !denied, denied ? "（审批未通过）\n" + body : body, e.DurationMs);
                break;

            case AgentEventKind.Error:
                FlushDelta(turn);
                turn.Note = e.Text ?? "错误";
                break;

            case AgentEventKind.Done:
                FlushDelta(turn);
                break;
        }
    }

    private void FlushDelta(AgentTurn turn)
    {
        if (_pendingDelta.Length == 0)
            return;
        turn.AssistantText += _pendingDelta.ToString();
        _pendingDelta.Clear();
        _lastFlushUtc = DateTime.UtcNow;
    }

    private void UpdatePlan(IReadOnlyList<PlanItem> items) => _uiInvoke(() =>
    {
        Plan.Clear();
        foreach (var item in items)
            Plan.Add(new PlanItemVM(item));
    });

    private static bool LooksApprovalDenied(string? text) =>
        !string.IsNullOrEmpty(text) &&
        (text.Contains("approval_timeout", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("approval_unavailable", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("\"status\":\"rejected\"", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("\"status\": \"rejected\"", StringComparison.OrdinalIgnoreCase));

    private void RebuildSessionHistoryUpTo(int turnIndex)
    {
        if (_runtime is null) return;
        var history = new List<ChatMessage>();
        for (var i = 0; i < turnIndex && i < Turns.Count; i++)
        {
            var turn = Turns[i];
            if (!string.IsNullOrEmpty(turn.UserText))
                history.Add(new ChatMessage(ChatRole.User, turn.UserText));
            if (!string.IsNullOrEmpty(turn.AssistantText))
                history.Add(new ChatMessage(ChatRole.Assistant, turn.AssistantText));
        }
        _runtime.Session.ReplaceHistory(history);
    }

    private async Task PersistTranscriptAsync()
    {
        var messages = new List<(string Role, string Content)>();
        foreach (var turn in Turns)
        {
            if (!string.IsNullOrEmpty(turn.UserText))
                messages.Add(("user", turn.UserText));
            if (!string.IsNullOrEmpty(turn.AssistantText))
                messages.Add(("assistant", turn.AssistantText));
        }
        await _store.ReplaceMessagesAsync(_sessionId, messages);
    }

    private void RefreshContextInfo()
    {
        var tokens = _runtime?.Session.EstimatedTokens ?? 0;
        var tools = _runtime?.ToolCount ?? 0;
        var limit = _agentConfig.ContextTokenLimit;
        ContextPercent = limit > 0 ? Math.Min(1.0, (double)tokens / limit) : 0;
        var pct = limit > 0 ? $" ({ContextPercent * 100:F0}%)" : string.Empty;
        ContextInfo = $"轮次 {Turns.Count} · 上下文 ~{tokens} tokens{pct} · 上限 {_agentConfig.ContextLimit} 条 / {limit} tokens{( _agentConfig.AutoSummarize ? " · 自动摘要" : "")} · {tools} 工具";
    }

    private static DateTime ParseUtc(string timestamp) =>
        DateTime.TryParse(timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt.ToUniversalTime() : DateTime.UtcNow;

    private static string LocalHms(string timestamp) => ParseUtc(timestamp).ToLocalTime().ToString("HH:mm:ss");

    private static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) ? string.Empty : (text.Length <= max ? text : text[..max] + " …(截断)");

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_runtime is not null)
            await _runtime.DisposeAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed class SyncProgress : IProgress<AgentEvent>
    {
        private readonly Action<AgentEvent> _sink;
        private readonly Action<Action> _uiInvoke;
        public SyncProgress(Action<AgentEvent> sink, Action<Action> uiInvoke) { _sink = sink; _uiInvoke = uiInvoke; }
        public void Report(AgentEvent value) => _uiInvoke(() => _sink(value));
    }
}

/// <summary>一轮对话：用户指令 + 该轮的工具调用过程 + 助手回答（含时间与耗时）。</summary>
public class AgentTurn : INotifyPropertyChanged
{
    private DateTime _startedLocal;

    public AgentTurn(string userText, DateTime timeUtc)
    {
        _userText = userText;
        _startedLocal = timeUtc.ToLocalTime();
        _userTime = _startedLocal.ToString("HH:mm:ss");
        Steps.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ToolHeader));
            OnPropertyChanged(nameof(ToolsVisibility));
        };
    }

    private string _userText;
    public string UserText { get => _userText; set => SetField(ref _userText, value); }

    private string _userTime;
    public string UserTime { get => _userTime; private set => SetField(ref _userTime, value); }

    public ObservableCollection<AgentStep> Steps { get; } = new();
    public bool HasSteps => Steps.Count > 0;
    public string ToolHeader => $"工具过程 ({Steps.Count})";
    public Visibility ToolsVisibility => HasSteps ? Visibility.Visible : Visibility.Collapsed;

    private bool _showTools = true;
    public bool ShowTools { get => _showTools; set => SetField(ref _showTools, value); }

    private string _assistantText = string.Empty;
    public string AssistantText
    {
        get => _assistantText;
        set { if (SetField(ref _assistantText, value)) OnPropertyChanged(nameof(AssistantVisibility)); }
    }

    private bool _isRunning = true;
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (!SetField(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(Footer));
            OnPropertyChanged(nameof(RunningVisibility));
            OnPropertyChanged(nameof(AssistantVisibility));
        }
    }
    public Visibility RunningVisibility => _isRunning ? Visibility.Visible : Visibility.Collapsed;

    private string _note = string.Empty;
    public string Note
    {
        get => _note;
        set { if (SetField(ref _note, value)) { OnPropertyChanged(nameof(NoteVisibility)); OnPropertyChanged(nameof(AssistantVisibility)); } }
    }
    public Visibility NoteVisibility => string.IsNullOrEmpty(_note) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility AssistantVisibility =>
        (_isRunning || _assistantText.Length > 0 || !string.IsNullOrEmpty(_note)) ? Visibility.Visible : Visibility.Collapsed;

    private string _endTime = string.Empty;
    private string _duration = string.Empty;
    public string Footer => _isRunning
        ? "处理中…"
        : (_endTime.Length == 0 ? string.Empty : _endTime + (_duration.Length > 0 ? $" · 用时 {_duration}" : string.Empty));

    public void MarkDone(DateTime endUtc)
    {
        var end = endUtc.ToLocalTime();
        _endTime = end.ToString("HH:mm:ss");
        var secs = (end - _startedLocal).TotalSeconds;
        _duration = secs >= 60 ? $"{secs / 60:F1} 分" : $"{secs:F1} 秒";
        IsRunning = false;
        ShowTools = false;
        OnPropertyChanged(nameof(Footer));
    }

    public void SetTimes(string endTime, string duration)
    {
        _endTime = endTime;
        _duration = duration;
        OnPropertyChanged(nameof(Footer));
    }

    public void ResetForRerun(string newText)
    {
        UserText = newText;
        _startedLocal = DateTime.Now;
        UserTime = _startedLocal.ToString("HH:mm:ss");
        AssistantText = string.Empty;
        Note = string.Empty;
        Steps.Clear();
        _endTime = string.Empty;
        _duration = string.Empty;
        ShowTools = true;
        IsRunning = true;
        OnPropertyChanged(nameof(Footer));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>一次工具调用（可展开看完整入参/结果，带耗时与重试）。</summary>
public sealed class AgentStep : INotifyPropertyChanged
{
    public AgentStep(string tool, string argsJson, bool destructive = false)
    {
        Tool = tool;
        ArgsJson = argsJson;
        Destructive = destructive;
    }

    public string Tool { get; }
    public string ArgsJson { get; }
    public bool Destructive { get; }

    private bool _isRunning = true;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            Raise();
            Raise(nameof(Tail));   // Tail 依赖运行状态：完成后立即刷新（否则停留在"调用中…"）
        }
    }

    private bool _success = true;
    public bool Success { get => _success; private set { _success = value; Raise(); } }

    private string _result = string.Empty;
    public string Result { get => _result; private set { _result = value; Raise(); Raise(nameof(Detail)); } }

    private long _durationMs;
    public long DurationMs { get => _durationMs; private set { _durationMs = value; Raise(); Raise(nameof(Tail)); } }

    public string Label => $"调用 {Tool}";
    public string Tail => IsRunning ? (Destructive ? "调用中…（可能等待人工审批）" : "调用中…") : $"{DurationMs} ms";
    public string Detail => (string.IsNullOrWhiteSpace(ArgsJson) ? string.Empty : "入参: " + ArgsJson + "\n\n") + (Result.Length > 0 ? "结果:\n" + Result : string.Empty);
    public Visibility RetryVisibility => !IsRunning && !Success ? Visibility.Visible : Visibility.Collapsed;

    public void Complete(bool success, string result, long durationMs)
    {
        Success = success;
        Result = result;
        DurationMs = durationMs;
        IsRunning = false;
        Raise(nameof(RetryVisibility));
    }

    public void Restart()
    {
        IsRunning = true;
        Result = string.Empty;
        DurationMs = 0;
        Raise(nameof(RetryVisibility));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>计划面板条目（显示用）。</summary>
public sealed class PlanItemVM
{
    public PlanItemVM(PlanItem item)
    {
        Done = item.Done;
        Text = item.Text;
    }

    public bool Done { get; }
    public string Text { get; }
    public string Glyph => Done ? "☑" : "☐";
}
