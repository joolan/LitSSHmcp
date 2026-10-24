using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.Agent;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.LocalTerminal;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.App.ViewModels;

/// <summary>AI 运维助手：按"轮"组织的多会话对话（内嵌 MCP 客户端驱动）。</summary>
public class AgentViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IConfigService _configService;
    private readonly IContextStore _store;
    private readonly DesktopLoginService? _desktopLogin;
    private readonly string _bundledSkillsDir;
    private readonly Action<Action> _uiInvoke;
    /// <summary>非阻塞的 UI 调度（BeginInvoke）：审批弹窗用它投递，工具线程只 await 结果，不再同步卡住 Dispatcher。</summary>
    private readonly Action<Action> _uiBeginInvoke;

    private AgentConfig _agentConfig = new();
    /// <summary>本机终端审批的「本轮次/本次会话」豁免登记表（随本会话窗口生命周期，重启即失效）。</summary>
    private readonly LocalApprovalExemptionRegistry _localExemptions = new();
    private bool _suppressSelection;
    private AgentTurn? _editingTurn;
    private const int StreamFlushMs = 150;

    // ---- runtime 池（评估报告 §4.1 路线 1）：每会话一个运行上下文（状态机 + 独立 runtime + 权威镜像） ----

    /// <summary>池元素只在 UI 线程增删（AgentViewModel 单线程亲和），普通字典即可。</summary>
    private readonly Dictionary<long, SessionRunContext> _contexts = new();

    /// <summary>当前显示会话的运行上下文（“未保存的新任务”= 会话 0 草稿上下文）。</summary>
    private SessionRunContext CurrentContext => GetContext(_sessionId);

    private SessionRunContext GetContext(long sessionId)
    {
        if (!_contexts.TryGetValue(sessionId, out var ctx))
        {
            ctx = new SessionRunContext(sessionId);
            ctx.StateChanged += OnContextStateChanged;
            _contexts[sessionId] = ctx;
        }
        return ctx;
    }

    /// <summary>当前会话的运行时（原单实例字段改派生属性：读写自动落到当前会话上下文，读点零改动）。</summary>
    private AgentRuntime? _runtime
    {
        get => CurrentContext.Runtime;
        set => CurrentContext.Runtime = value;
    }

    /// <summary>配置代际：设置/连接器重载后 +1；旧代际的池内运行时下次激活时重建。</summary>
    private int _poolVersion;

    /// <summary>测试连接进行中（会话闸门的另一半：与运行状态无关的 UI 占用）。</summary>
    private bool _connectionTesting;

    public AgentViewModel(IConfigService configService, IContextStore store, string bundledSkillsDir, Action<Action>? uiInvoke = null, Action<Action>? uiBeginInvoke = null, DesktopLoginService? desktopLogin = null)
    {
        _configService = configService;
        _store = store;
        _desktopLogin = desktopLogin;
        _bundledSkillsDir = bundledSkillsDir;
        _uiInvoke = uiInvoke ?? (action => action());
        _uiBeginInvoke = uiBeginInvoke ?? _uiInvoke;
        _sidebar = new AgentSessionSidebar(
            () => FilteredSessions,
            () => !string.IsNullOrWhiteSpace(_sessionFilter),
            StateTextForSession,
            ApplySidebarSelection);
        _attachmentManager = new AgentAttachmentManager(message => StatusMessage = message);
        _attachmentManager.Changed += () => OnPropertyChanged(nameof(HasAttachments));
        _skills = new AgentSkillState(name => OnPropertyChanged(name));
        SendCommand = new RelayCommand(_ => _ = SendAsync(), _ => !IsBusy && !string.IsNullOrWhiteSpace(Input));
        StopCommand = new RelayCommand(_ => CurrentContext.Cts?.Cancel(), _ => IsBusy);
        SendOrStopCommand = new RelayCommand(_ => { if (IsBusy) CurrentContext.Cts?.Cancel(); else _ = SendAsync(); },
            _ => IsBusy || !string.IsNullOrWhiteSpace(Input));
        NewSessionCommand = new RelayCommand(_ => _ = NewSessionAsync(), _ => !IsBusy);
        DeleteSessionCommand = new RelayCommand(_ => _ = DeleteSessionAsync(), _ => !IsBusy && SelectedSession != null);
        ClearCommand = new RelayCommand(_ => { Turns.Clear(); CurrentContext.LiveTurns = Turns.ToList(); _editingTurn = null; RefreshContextInfo(); });
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

        Turns.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset || Turns.Count == 0)
                _visibleFrom = 0;
            OnPropertyChanged(nameof(EmptyStateVisibility));
            OnPropertyChanged(nameof(HasMoreHistory));
            (LoadOlderHistoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        };

        // 对话列表分页视图：只渲染最近 N 轮，避免超长会话一次性渲染全部（模型上下文仍是全量）。
        TurnsView = CollectionViewSource.GetDefaultView(Turns);
        TurnsView.Filter = o => o is AgentTurn turn && turn.UiOrdinal >= _visibleFrom;
        LoadOlderHistoryCommand = new RelayCommand(_ => LoadOlderHistory(), _ => HasMoreHistory);

        // 模型下拉视图：按厂家（GroupName）分组，选中项仍是具体的某个模型
        ComboView = CollectionViewSource.GetDefaultView(ComboProviders);
        ComboView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AgentProviderConfig.GroupName)));
    }

    public ObservableCollection<AgentProviderConfig> Providers { get; } = new();

    /// <summary>
    /// 按会话解析共享本机终端（由宿主 App 注入，须在 UI 线程创建/显示）。键=会话 Id（新建未落库为 "new"）。
    /// 宿主负责进程存活校验与失效重建，保证同一会话始终操作同一个终端。
    /// </summary>
    public Func<string, LocalTerminalSessionViewModel?>? LocalTerminalResolver { get; set; }

    public IAiTerminalBroker? LocalTerminalBroker { get; set; }

    /// <summary>当前会话键（已落库=会话 Id；新建未落库="new"），用于按会话隔离本机终端。</summary>
    public string CurrentSessionKey => _sessionId > 0 ? _sessionId.ToString() : "new";

    /// <summary>模型下拉列表 = 可用模型 + 末尾固定的“模型设置”入口。</summary>
    public ObservableCollection<AgentProviderConfig> ComboProviders { get; } = new();

    /// <summary>模型下拉的分组视图（按厂家 GroupName 分组）。</summary>
    public ICollectionView ComboView { get; }

    private readonly AgentProviderConfig _modelSettingsEntry = new()
    {
        Id = "\u0001model-settings",
        Name = "模型设置",
        GroupName = "设置"
    };

    /// <summary>请求打开设置窗口（参数为初始 Tab：null=助手设置 / "model"=大模型设置）。</summary>
    public event Action<string?>? SettingsRequested;

    /// <summary>智能体下拉列表 = 配置的智能体 + 末尾固定的“智能体设置”入口。</summary>
    public ObservableCollection<ChatAgentConfig> ComboAgents { get; } = new();

    /// <summary>会话窗口「连接器」列表：配置的全部 MCP 服务器（内置置顶），含连接状态。</summary>
    public ObservableCollection<McpConnectorItem> Connectors { get; } = new();

    private readonly ChatAgentConfig _agentSettingsEntry = new()
    {
        Id = "agent-settings-entry",
        Name = "智能体设置…",
        Description = "打开设置窗口的「智能体」标签页"
    };

    private ChatAgentConfig? _selectedAgent;
    public ChatAgentConfig? SelectedAgent
    {
        get => _selectedAgent;
        set
        {
            // 下拉末尾的“智能体设置”入口：打开设置窗口的智能体 Tab，并还原选择
            if (ReferenceEquals(value, _agentSettingsEntry))
            {
                SettingsRequested?.Invoke("agents");
                OnPropertyChanged(nameof(SelectedAgent));
                return;
            }

            // 任务进行中禁止切换智能体（提示词热切换须在空闲时），并还原选中项
            if (IsBusy && !_suppressSelection && !ReferenceEquals(_selectedAgent, value))
            {
                StatusMessage = "任务进行中：请等它完成或点「停止」后再切换智能体。";
                OnPropertyChanged(nameof(SelectedAgent));
                return;
            }

            var changed = !ReferenceEquals(_selectedAgent, value);
            if (!Set(ref _selectedAgent, value)) return;
            if (changed && !_suppressSelection)
            {
                _runtime?.SetActiveAgent(value?.ToPromptContext());
                _ = PersistAgentSelectionAsync(value?.Id ?? string.Empty);
                StatusMessage = value is null ? "已切到基础提示（无智能体）" : $"智能体：{value.Name}";
            }
        }
    }

    private void RebuildComboAgents()
    {
        ComboAgents.Clear();
        foreach (var a in _agentConfig.Agents ?? Array.Empty<ChatAgentConfig>())
            ComboAgents.Add(a);
        ComboAgents.Add(_agentSettingsEntry);
    }

    /// <summary>按 Id 挑选智能体：指定 Id 无效则回退全局默认 → 第一个（空列表=无智能体，用基础提示）。</summary>
    private ChatAgentConfig? PickAgent(string? preferredId)
    {
        var list = _agentConfig.Agents ?? Array.Empty<ChatAgentConfig>();
        if (list.Length == 0)
            return null;
        if (!string.IsNullOrWhiteSpace(preferredId))
        {
            var hit = list.FirstOrDefault(a => string.Equals(a.Id, preferredId, StringComparison.Ordinal));
            if (hit is not null)
                return hit;
        }
        return list.FirstOrDefault(a => string.Equals(a.Id, _agentConfig.ActiveAgentId, StringComparison.Ordinal))
            ?? list.FirstOrDefault();
    }

    /// <summary>记录当前会话使用的智能体（并更新全局默认），用于切换会话时恢复。</summary>
    private async Task PersistAgentSelectionAsync(string agentId)
    {
        try
        {
            if (_sessionId > 0)
                await _store.SetSessionAgentIdAsync(_sessionId, agentId);

            if (!string.Equals(_agentConfig.ActiveAgentId, agentId, StringComparison.Ordinal))
            {
                _agentConfig.ActiveAgentId = agentId;
                var cfg = await _configService.LoadConfigAsync();
                (cfg.Agent ??= new AgentConfig()).ActiveAgentId = agentId;
                await _configService.SaveConfigAsync(cfg);
            }
        }
        catch
        {
            // 记录失败不影响主流程
        }
    }

    /// <summary>会话被删除/归档时触发（参数为会话 Id），供宿主清理该会话的本机终端等资源。</summary>
    public event Action<long>? SessionRemoved;

    private void RebuildComboProviders()
    {
        ComboProviders.Clear();
        foreach (var p in Providers)
            ComboProviders.Add(p);
        ComboProviders.Add(_modelSettingsEntry);
    }
    public ObservableCollection<AgentSessionRow> Sessions { get; } = new();
    public ObservableCollection<AgentSessionRow> FilteredSessions { get; } = new();

    private readonly AgentSessionSidebar _sidebar;
    private readonly AgentAttachmentManager _attachmentManager;

    /// <summary>已定义的分组名（持久化；空分组也会在侧栏显示）。</summary>
    public ObservableCollection<string> Groups => _sidebar.Groups;

    /// <summary>侧栏扁平项：分组标题 + 会话（供带分组的列表渲染）。</summary>
    public ObservableCollection<SidebarItemVM> SidebarItems => _sidebar.SidebarItems;

    /// <summary>折叠/展开某分组。</summary>
    public void ToggleGroup(string key) => _sidebar.Toggle(key, _selectedSession);

    private SidebarItemVM? _selectedSidebarItem;
    /// <summary>侧栏选中项；选中会话项时联动 <see cref="SelectedSession"/>，标题项不可选。</summary>
    public SidebarItemVM? SelectedSidebarItem
    {
        get => _selectedSidebarItem;
        set
        {
            if (value is null)
            {
                Set(ref _selectedSidebarItem, null);
                return;
            }
            if (value.Session is { } row)
            {
                Set(ref _selectedSidebarItem, value);
                SelectedSession = row;
            }
            else
            {
                // 分组标题不可选：通知一次让 ListBox 回退到原选项
                OnPropertyChanged(nameof(SelectedSidebarItem));
            }
        }
    }

    /// <summary>根据当前会话把侧栏选中项同步到（重建后的）对应项。</summary>
    private void ApplySidebarSelection(SidebarItemVM? match)
    {
        if (ReferenceEquals(match, _selectedSidebarItem))
            return;
        _selectedSidebarItem = match;
        OnPropertyChanged(nameof(SelectedSidebarItem));
    }

    /// <summary>重建侧栏扁平项：未分组（有内容时）置顶 + 各命名分组（空也显示）下挂会话。</summary>
    private void RebuildSidebar() => _sidebar.Rebuild(_selectedSession);

    private void SyncSidebarSelection() => _sidebar.SyncSelection(_selectedSession);

    /// <summary>加载分组定义。</summary>
    public async Task ReloadGroupsAsync()
    {
        try
        {
            var list = await _store.ListGroupsAsync();
            Groups.Clear();
            foreach (var g in list)
                Groups.Add(g);
        }
        catch
        {
            // 读取失败忽略
        }
    }

    /// <summary>新增分组。</summary>
    public async Task AddGroupAsync(string name)
    {
        var g = name?.Trim();
        if (string.IsNullOrEmpty(g))
            return;
        await _store.AddGroupAsync(g);
        await ReloadGroupsAsync();
        RebuildSidebar();
    }

    /// <summary>重命名分组。</summary>
    public async Task RenameGroupAsync(string oldName, string newName)
    {
        var to = newName?.Trim();
        if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(to))
            return;
        await _store.RenameGroupAsync(oldName, to);
        await ReloadGroupsAsync();
        await ReloadSessionsAsync();
    }

    /// <summary>删除分组（其下会话归入未分组）。</summary>
    public async Task DeleteGroupAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        await _store.DeleteGroupAsync(name);
        await ReloadGroupsAsync();
        await ReloadSessionsAsync();
    }

    /// <summary>把某会话移动到指定分组（空=未分组，排到该分组最前）。</summary>
    public Task MoveSessionToGroupAsync(AgentSessionRow? session, string group) =>
        MoveSessionAsync(session, group ?? string.Empty, after: null);

    /// <summary>
    /// 拖拽落点移动：把 <paramref name="dragged"/> 移到 <paramref name="group"/> 分组，
    /// <paramref name="after"/> 为 null 表示置于该分组最前，否则紧跟在该会话之后。
    /// </summary>
    public async Task MoveSessionAsync(AgentSessionRow? dragged, string group, AgentSessionRow? after)
    {
        if (dragged is null)
            return;
        group ??= string.Empty;

        var order = AgentSessionOrder.ComputeMoveOrder(Sessions, dragged, group, after);
        await _store.MoveSessionAsync(dragged.Id, group, order);
        await ReloadSessionsAsync();
    }

    /// <summary>拖拽排序：把分组 <paramref name="dragged"/> 移到 <paramref name="target"/> 之前。</summary>
    public async Task ReorderGroupAsync(string dragged, string target)
    {
        if (AgentSessionOrder.ReorderGroups(Groups, dragged, target) is not { } list)
            return;
        await _store.ReorderGroupsAsync(list);
        await ReloadGroupsAsync();
        RebuildSidebar();
    }
    public ObservableCollection<AgentTurn> Turns { get; } = new();

    /// <summary>对话列表的分页视图（只渲染最近 N 轮，支持"加载更早"）；模型上下文仍是全量。</summary>
    public ICollectionView TurnsView { get; }

    private int _visibleFrom;
    private const int TranscriptPageSize = 20;

    /// <summary>是否还有更早的记录可加载。</summary>
    public bool HasMoreHistory => _visibleFrom > 0;

    public ICommand LoadOlderHistoryCommand { get; }

    /// <summary>展示更多更早的轮次（每页 <see cref="TranscriptPageSize"/>）。</summary>
    public void LoadOlderHistory()
    {
        if (_visibleFrom <= 0)
            return;
        _visibleFrom = Math.Max(0, _visibleFrom - TranscriptPageSize);
        TurnsView?.Refresh();
        OnPropertyChanged(nameof(HasMoreHistory));
        (LoadOlderHistoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>重置转录窗口：重算各轮序号，只显示最后 <see cref="TranscriptPageSize"/> 轮。</summary>
    private void ResetTranscriptWindow()
    {
        for (var i = 0; i < Turns.Count; i++)
            Turns[i].UiOrdinal = i;
        _visibleFrom = Math.Max(0, Turns.Count - TranscriptPageSize);
        TurnsView?.Refresh();
        OnPropertyChanged(nameof(HasMoreHistory));
        (LoadOlderHistoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    // ---- 技能目录与激活（选择器 / 索引模式） ----

    private readonly AgentSkillState _skills;

    /// <summary>可用技能目录（由技能根目录扫描，按当前可用域标注）。</summary>
    public ObservableCollection<SkillInfo> AvailableSkills => _skills.AvailableSkills;

    /// <summary>当前会话激活的技能（空 = 索引模式：只向模型注入技能索引）。</summary>
    public ObservableCollection<SkillInfo> ActiveSkills => _skills.ActiveSkills;

    public bool HasActiveSkills => _skills.HasActiveSkills;

    public bool HasAvailableSkills => _skills.HasAvailableSkills;

    /// <summary>请求打开技能选择窗口（由 AgentWindow 打开）。</summary>
    public event Action? SkillsPickerRequested;

    /// <summary>技能根目录（用户配置优先，否则内置）；无则 null。</summary>
    public string? SkillsRoot => SkillRegistry.ResolveDir(_agentConfig.SkillsDir, _bundledSkillsDir);

    // ---- 用量 / 成本（当前会话） ----

    /// <summary>生成当前会话的用量与估算成本报告（供「用量 / 成本」窗口展示）。</summary>
    public string BuildUsageReport()
    {
        var session = _runtime?.Session;
        var provider = SelectedProvider;
        if (session is null || provider is null)
            return "尚未连接模型（无用量数据）。请先发送一条消息。";

        var input = session.SessionInputTokens;
        var output = session.SessionOutputTokens;
        var cached = session.SessionCachedInputTokens;
        var requests = session.SessionRequestCount;
        var cost = input / 1_000_000.0 * provider.InputPricePerMTok + output / 1_000_000.0 * provider.OutputPricePerMTok;

        var sb = new StringBuilder();
        sb.AppendLine($"模型：{provider.DisplayName}");
        if (session.HasRealUsage)
        {
            sb.AppendLine($"请求次数：{requests}");
            sb.AppendLine($"输入 token：{input:N0}" + (cached > 0
                ? $"（其中缓存命中 {cached:N0}，{cached * 100 / Math.Max(1, input)}%）"
                : string.Empty));
            sb.AppendLine($"输出 token：{output:N0}");
            sb.AppendLine($"合计 token：{input + output:N0}");
            if (provider.InputPricePerMTok > 0 || provider.OutputPricePerMTok > 0)
                sb.AppendLine($"估算成本：¥{cost:F4}（入 {provider.InputPricePerMTok}/M · 出 {provider.OutputPricePerMTok}/M）");
            else
                sb.AppendLine("估算成本：未填价格 —— 在「AI 助手设置 → 大模型设置」为模型填入 入/出 价格（每百万 token）后显示。");
        }
        else
        {
            sb.AppendLine("该模型未返回用量（token 为本地估算，成本无法计算）。");
        }

        sb.AppendLine();
        sb.AppendLine("说明：统计为**当前会话**运行期累计；切换会话或重启助手会重置。缓存命中越多越省。");
        return sb.ToString();
    }

    // ---- 计划模式 ----

    private bool _planMode;
    public bool IsPlanMode
    {
        get => _planMode;
        private set
        {
            if (!Set(ref _planMode, value)) return;
            OnPropertyChanged(nameof(PlanModeText));
            OnPropertyChanged(nameof(PlanModeIndex));
        }
    }

    /// <summary>模式下拉的选中项：0=执行模式，1=计划模式。</summary>
    public int PlanModeIndex
    {
        get => _planMode ? 1 : 0;
        set
        {
            var planMode = value == 1;
            if (planMode == _planMode)
                return;
            SetPlanMode(planMode);
            OnPropertyChanged(nameof(PlanModeIndex));   // 若被"任务进行中"拒绝则回退显示
        }
    }

    public string PlanModeText => _planMode ? "计划模式" : "执行模式";

    // ---- 工具审批策略（会话级，Agent 层） ----

    private Dictionary<string, AgentToolPolicy> _toolPolicy = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>可配置策略的 MCP 工具目录。</summary>
    public IReadOnlyList<ToolPolicyItem> ToolCatalog => _runtime?.ToolCatalog ?? (IReadOnlyList<ToolPolicyItem>)Array.Empty<ToolPolicyItem>();

    /// <summary>当前工具审批策略（tool → policy）。</summary>
    public IReadOnlyDictionary<string, AgentToolPolicy> ToolPolicy => _toolPolicy;

    /// <summary>应用工具审批策略：更新、持久化到会话、并热更新运行时。</summary>
    public void ApplyToolPolicy(IReadOnlyDictionary<string, AgentToolPolicy> policy)
    {
        _toolPolicy = new Dictionary<string, AgentToolPolicy>(policy, StringComparer.OrdinalIgnoreCase);
        _ = PersistToolPolicyAsync();
        _runtime?.SetToolPolicy(_toolPolicy);
        var denied = _toolPolicy.Count(kv => kv.Value == AgentToolPolicy.Deny);
        StatusMessage = denied == 0 ? "工具审批策略：全部默认" : $"工具审批策略：已拒绝 {denied} 个工具（调用前拦截）";
    }

    private async Task PersistToolPolicyAsync()
    {
        if (_sessionId <= 0)
            return;
        try
        {
            var map = _toolPolicy
                .Where(kv => kv.Value != AgentToolPolicy.Default)
                .ToDictionary(kv => kv.Key, kv => AgentToolPolicyParser.ToName(kv.Value));
            var json = map.Count == 0 ? string.Empty : JsonSerializer.Serialize(map);
            await _store.SetSessionToolPolicyAsync(_sessionId, json);
        }
        catch { /* 保存失败不影响主流程 */ }
    }

    private async Task RestoreToolPolicyAsync(long sessionId)
    {
        _toolPolicy = new Dictionary<string, AgentToolPolicy>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = await _store.GetSessionToolPolicyAsync(sessionId);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (map is not null)
                    foreach (var (tool, value) in map)
                        _toolPolicy[tool] = AgentToolPolicyParser.Parse(value);
            }
        }
        catch
        {
            _toolPolicy = new Dictionary<string, AgentToolPolicy>(StringComparer.OrdinalIgnoreCase);
        }
    }

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

    /// <summary>
    /// 本会话工作目录（需求 D）：加载会话/新建会话时填充，首条消息分配后锁定，运行期代码层不再变更。
    /// 空 = 未分配（旧会话），回退共享工作区（现状行为，向后兼容）。
    /// </summary>
    private string? _sessionWorkspaceDir;

    /// <summary>
    /// 本会话连接器子集（D8 启用层，评估报告 §6）：null = 继承全局默认启用集合；
    /// 加载会话时从 agent_sessions.EnabledMcp 读回，首条消息（工作目录分配）后锁定，装配期烧入 ctx.Runtime。
    /// </summary>
    private IReadOnlyCollection<string>? _sessionMcpSubset;

    /// <summary>当前生效的工作区根：会话目录（已分配）或共享工作区（未分配）。</summary>
    public string CurrentWorkspaceRoot =>
        !string.IsNullOrWhiteSpace(_sessionWorkspaceDir)
            ? _sessionWorkspaceDir!
            : WorkspaceTools.ResolveDir(_agentConfig.WorkspaceDir);

    /// <summary>工作区目录（打开文件夹按钮：优先本会话工作目录，未分配则共享工作区）。</summary>
    public string WorkspaceDir => CurrentWorkspaceRoot;

    /// <summary>运行时装配的工作区根与当前会话工作区根是否一致（不一致须重建运行时，根在装配期烧定）。</summary>
    private bool RuntimeRootMismatched =>
        _runtime is not null &&
        !string.Equals(_runtime.WorkspaceRoot, CurrentWorkspaceRoot, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 会话归因同步到本地工具审计（评估报告 §10.A.1 / D1）：本地审计记录以 agent-会话Id 标识来源会话；
    /// 新建任务未发送（_sessionId=0）时为 null。
    /// </summary>
    private void SyncLocalAuditSession()
        => LocalAudit.AmbientSessionId = _sessionId > 0 ? $"agent-{_sessionId}" : null;
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

    // ---- 跨进程对账（Web 进程也会写 agent.db）：5s 轻量比较会话列表/选中会话更新时间 ----
    private CancellationTokenSource? _autoRefreshCts;
    private string _lastSessionSnapshot = string.Empty;
    private readonly Dictionary<long, string> _loadedSessionUpdatedAt = new();

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
            // 多会话并行（评估报告 §4.1）：任务进行中允许自由切换——每会话独立 runtime 与状态机，
            // 后台会话继续执行，当前会话闸门只约束正在显示的这个会话。
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
        set
        {
            if (Set(ref _input, value))
            {
                (SendCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (SendOrStopCommand as RelayCommand)?.RaiseCanExecuteChanged();
                UpdateSlashPalette();
            }
        }
    }

    // ---- 斜杠命令（/临时聊天 等） ----

    /// <summary>请求打开「临时聊天」面板（参数为可选初始文本），由 AgentWindow 打开独立窗口。</summary>
    public event Action<string?>? TemporaryChatRequested;

    public IReadOnlyList<SlashCommand> SlashCommandList { get; } = SlashCommands.All;

    /// <summary>内置命令 + 用户提示词模板（运行时合并）。</summary>
    private IReadOnlyList<SlashCommand> _allCommands = SlashCommands.BuiltIn;

    /// <summary>重新加载用户提示词模板并合并到斜杠命令（在助手设置保存后调用）。</summary>
    public void ReloadPromptTemplates()
    {
        var list = new List<SlashCommand>(SlashCommands.BuiltIn);
        try
        {
            foreach (var t in AgentPromptStore.Load())
                list.Add(new SlashCommand(
                    "/" + t.Name,
                    string.IsNullOrWhiteSpace(t.Description) ? "提示词模板" : t.Description,
                    SlashCommandKind.Template,
                    t.Prompt));
        }
        catch
        {
            // 模板读取失败则仅用内置命令
        }
        _allCommands = list;
        if (IsSlashPaletteOpen)
            UpdateSlashPalette();
    }

    public ObservableCollection<SlashSuggestion> SlashSuggestions { get; } = new();

    private bool _isSlashPaletteOpen;
    public bool IsSlashPaletteOpen { get => _isSlashPaletteOpen; private set => Set(ref _isSlashPaletteOpen, value); }

    private SlashSuggestion? _selectedSlashSuggestion;
    public SlashSuggestion? SelectedSlashSuggestion { get => _selectedSlashSuggestion; set => Set(ref _selectedSlashSuggestion, value); }

    private void UpdateSlashPalette()
    {
        var token = SlashCommands.CurrentToken(Input);
        if (token is null)
        {
            CloseSlashPalette();
            return;
        }

        var filter = token.TrimStart('/').Trim();
        var suggestions = new List<SlashSuggestion>();

        foreach (var command in SlashCommands.Match(_allCommands, token))
            suggestions.Add(new SlashSuggestion { Name = command.Name, Description = command.Description, Command = command });

        // codex 风格：技能直接内联在命令列表中（分组标题 + 缩进项，选中即切换激活）。
        var skills = AvailableSkills
            .Where(s => filter.Length == 0
                        || s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || s.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || s.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (skills.Count > 0)
        {
            suggestions.Add(new SlashSuggestion { IsHeader = true, Name = "技能", Description = "选中即切换激活（✓=已激活；详情用 /技能）" });
            var active = new HashSet<string>(_skills.ActiveIds, StringComparer.OrdinalIgnoreCase);
            foreach (var skill in skills)
                suggestions.Add(new SlashSuggestion { Name = skill.Name, Description = skill.Description, SkillId = skill.Id, IsSelected = active.Contains(skill.Id) });
        }

        SlashSuggestions.Clear();
        foreach (var suggestion in suggestions)
            SlashSuggestions.Add(suggestion);

        SelectedSlashSuggestion = SlashSuggestions.FirstOrDefault(s => !s.IsHeader);
        IsSlashPaletteOpen = SlashSuggestions.Count > 0;
    }

    public void CloseSlashPalette()
    {
        IsSlashPaletteOpen = false;
        SlashSuggestions.Clear();
        SelectedSlashSuggestion = null;
    }

    public void MoveSlashSelection(int delta)
    {
        if (!IsSlashPaletteOpen || SlashSuggestions.Count == 0)
            return;

        var i = Math.Max(0, SlashSuggestions.IndexOf(SelectedSlashSuggestion!));
        for (var step = 0; step < SlashSuggestions.Count; step++)
        {
            i = (i + delta + SlashSuggestions.Count) % SlashSuggestions.Count;
            if (!SlashSuggestions[i].IsHeader)
            {
                SelectedSlashSuggestion = SlashSuggestions[i];
                return;
            }
        }
    }

    /// <summary>执行面板当前选中的命令，或切换选中技能的激活状态（回车/Tab）。</summary>
    public bool ExecuteSelectedSlashCommand()
    {
        var selected = SelectedSlashSuggestion;
        if (selected is null)
            return false;
        if (selected.Command is { } command)
            return TryExecuteSlashCommand(command, string.Empty);
        if (selected.SkillId is { } skillId)
        {
            ToggleSkillInline(skillId);
            return true;
        }
        return false;
    }

    /// <summary>在斜杠面板内联切换技能激活（保持面板打开并刷新 ✓）。</summary>
    private void ToggleSkillInline(string skillId)
    {
        var ids = new List<string>(_skills.ActiveIds);
        if (ids.RemoveAll(id => string.Equals(id, skillId, StringComparison.OrdinalIgnoreCase)) == 0)
            ids.Add(skillId);

        ApplySkillSelection(ids);
        UpdateSlashPalette();
        SelectedSlashSuggestion = SlashSuggestions.FirstOrDefault(
            s => s.SkillId is not null && string.Equals(s.SkillId, skillId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>输入首位是已知命令时直接执行（输入框回车）；支持「/工具 log mysql」这类带参数命令。</summary>
    public bool TryExecuteInputCommand()
    {
        var (cmd, args) = SlashCommands.Parse(_allCommands, Input);
        return cmd is not null && TryExecuteSlashCommand(cmd, args);
    }

    private bool TryExecuteSlashCommand(SlashCommand cmd, string args)
    {
        CloseSlashPalette();

        // 用户提示词模板：把正文填入输入框（不自动发送，用户可补充后回车）。
        if (cmd.Kind == SlashCommandKind.Template)
        {
            Input = cmd.Prompt ?? string.Empty;
            return true;
        }

        switch (cmd.Name)
        {
            case SlashCommands.TempChat:
                Input = string.Empty;
                TemporaryChatRequested?.Invoke(null);
                return true;
            case SlashCommands.Skills:
                Input = string.Empty;
                CloseSlashPalette();
                if (SessionSubsetLocked)
                {
                    StatusMessage = "首条消息后锁定：技能子集改动将在下个会话生效。";
                    return true;
                }
                ReloadSkillCatalog();
                SkillsPickerRequested?.Invoke();
                return true;
            case SlashCommands.CompactSession:
                Input = string.Empty;
                _ = CompactSessionAsync();
                return true;
            case SlashCommands.ClearScreen:
                Input = string.Empty;
                ClearScreenDisplay();
                return true;
            case SlashCommands.ClearContext:
                Input = string.Empty;
                _ = ClearContextAsync();
                return true;
        }

        return false;
    }

    /// <summary>只清空屏幕展示（保留会话上下文/模型记忆，不影响正在运行的任务）。</summary>
    private void ClearScreenDisplay()
    {
        Turns.Clear();
        Plan.Clear();
        RefreshContextInfo();
        ShowNotice("已清空屏幕显示（会话上下文与模型记忆保留）");
    }

    /// <summary>只重置模型上下文（保留屏幕显示与持久化记录）。</summary>
    private async Task ClearContextAsync()
    {
        if (IsBusy)
        {
            ShowNotice("任务进行中，无法清空上下文；请等任务结束后再试。");
            return;
        }
        if (_runtime is null)
        {
            await EnsureRuntimeAsync(await LoadHistoryFromStoreAsync());
            if (_runtime is null)
                return;
        }

        _runtime.Session.ClearContext();
        await PersistSummaryAsync();
        RefreshContextInfo();
        ShowNotice("已清空模型上下文（屏幕与记录保留）");
    }

    /// <summary>立即压缩当前会话上下文（较早轮次摘要后丢弃，保留最近 2 轮）。</summary>
    public async Task CompactSessionAsync()
    {
        if (IsBusy)
        {
            StatusMessage = "任务进行中，无法压缩会话；请等任务结束后再试。";
            return;
        }

        if (_runtime is null)
        {
            await EnsureRuntimeAsync(await LoadHistoryFromStoreAsync());
            if (_runtime is null)
                return;
        }

        try
        {
            StatusMessage = "正在压缩会话上下文…";
            var changed = await _runtime.Session.CompactNowAsync();
            await PersistSummaryAsync();
            RefreshContextInfo();
            StatusMessage = changed ? "已压缩会话上下文（较早轮次已摘要）" : "轮次较少，无需压缩";
            ShowNotice(changed ? "🗜 已压缩会话上下文：较早轮次已摘要并保留最近 2 轮" : "轮次较少，无需压缩");
        }
        catch (Exception ex)
        {
            StatusMessage = "压缩失败: " + ex.Message;
        }
    }

    /// <summary>
    /// 会话级闸门的派生视图：当前会话处于排队/执行/待审批（会话状态机），或测试连接进行中。
    /// 各命令的 CanExecute 仍按“当前会话”判定；后台运行中的其他会话各自持有状态，不占用这里。
    /// </summary>
    public bool IsBusy => _connectionTesting || CurrentContext.IsBusy;

    /// <summary>桌面登录态（D1 收尾，仅归因）：已登录=登录用户名，未登录=local。</summary>
    public bool IsDesktopLoggedIn => _desktopLogin?.Current is not null;

    /// <summary>侧栏归因状态文案。</summary>
    public string DesktopLoginLabel => _desktopLogin?.Current is { } state
        ? $"归因：{state.Username}"
        : "归因：未登录（local）";

    /// <summary>登录按钮文案（未登录=登录…，已登录=注销）。</summary>
    public string DesktopLoginActionText => IsDesktopLoggedIn ? "注销" : "登录…";

    /// <summary>登录态变化后刷新绑定（由窗口在登录/注销/启动恢复后调用）。</summary>
    public void NotifyDesktopLoginChanged()
    {
        OnPropertyChanged(nameof(IsDesktopLoggedIn));
        OnPropertyChanged(nameof(DesktopLoginLabel));
        OnPropertyChanged(nameof(DesktopLoginActionText));
    }

    /// <summary>IsBusy 派生量变化（状态机切换 / 测试连接起止）：统一发通知。</summary>
    private void RaiseBusyChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(SendButtonText));
        foreach (var cmd in new[] { SendCommand, StopCommand, SendOrStopCommand, NewSessionCommand, DeleteSessionCommand, ContinueCommand, RegenerateCommand, RetryStepCommand, TestConnectionCommand })
            (cmd as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnContextStateChanged(SessionRunContext ctx)
    {
        // 状态可能在工具线程包装里变化（审批等待）：统一收口到 UI 线程刷新闸门与徽章
        _uiInvoke(() =>
        {
            RaiseBusyChanged();
            OnPropertyChanged(nameof(SessionSubsetLocked));   // 运行起止会切换启用层锁定（D8）
            // 侧栏所有会话行徽章刷新（后台会话状态/停止入口的启用态都靠它）
            _sidebar.RefreshStates();
        });
        if (ReferenceEquals(ctx, CurrentContext))
            OnPropertyChanged(nameof(SessionStateText));
    }

    /// <summary>会话状态机 → 徽章文案（Idle/Done=空）。</summary>
    private static string StateTextOf(SessionRunState state) => state switch
    {
        SessionRunState.Queued => "排队中",
        SessionRunState.Running => "执行中",
        SessionRunState.WaitingApproval => "待审批",
        SessionRunState.Stopped => "已停止",
        SessionRunState.Failed => "上轮失败",
        _ => string.Empty
    };

    /// <summary>当前会话的状态文案（侧栏/状态栏徽章用；Idle/Done=空）。</summary>
    public string SessionStateText => StateTextOf(CurrentContext.State);

    /// <summary>指定会话的状态徽章文案（侧栏行绑定；无运行上下文=空）。</summary>
    public string StateTextForSession(long sessionId)
        => _contexts.TryGetValue(sessionId, out var ctx) ? StateTextOf(ctx.State) : string.Empty;

    /// <summary>指定会话是否处于占用态（侧栏右键「停止任务」启用闸）。</summary>
    public bool IsSessionRunning(long sessionId)
        => _contexts.TryGetValue(sessionId, out var ctx) && ctx.IsBusy;

    /// <summary>按会话停止任务（§4.1 停止路由）：只取消该会话的运行上下文，不影响其它会话。</summary>
    public void StopSession(long sessionId)
    {
        if (!_contexts.TryGetValue(sessionId, out var ctx) || ctx.Cts is null)
            return;
        try { ctx.Cts.Cancel(); } catch (ObjectDisposedException) { }
        StatusMessage = $"已请求停止会话 {sessionId} 的任务…";
    }

    /// <summary>
    /// D8 启用层锁定（与工作目录同一锁定原则）：会话已启动（已落库 / 工作目录已分配 / 已有轮次）或运行中
    /// → 连接器/技能子集不可再改，改动下个会话生效。
    /// </summary>
    public bool SessionSubsetLocked =>
        !McpSubsetPolicy.EnableLayerEditable(_sessionId, _sessionWorkspaceDir, Turns.Count, CurrentContext.IsBusy);

    private string _statusMessage = "加载中…";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    /// <summary>是否处于初始加载/切换会话的加载中状态（用于显示加载遮罩）。初始为 true，避免开窗瞬间空白。</summary>
    private bool _isLoading = true;
    public bool IsLoading { get => _isLoading; set => Set(ref _isLoading, value); }

    private string _loadingText = "正在加载…";
    public string LoadingText { get => _loadingText; set => Set(ref _loadingText, value); }

    private string _noticeText = string.Empty;
    public string NoticeText
    {
        get => _noticeText;
        private set { if (Set(ref _noticeText, value)) OnPropertyChanged(nameof(NoticeVisibility)); }
    }

    public Visibility NoticeVisibility => string.IsNullOrEmpty(_noticeText) ? Visibility.Collapsed : Visibility.Visible;

    private int _noticeVersion;

    /// <summary>在对话区底部弹出一条临时提示，<paramref name="seconds"/> 秒后自动消失。</summary>
    public void ShowNotice(string message, int seconds = 4)
    {
        NoticeText = message;
        var version = ++_noticeVersion;
        _ = Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ =>
        {
            if (version == _noticeVersion)
                _uiInvoke(() => NoticeText = string.Empty);
        });
    }

    private string _contextInfo = string.Empty;
    public string ContextInfo { get => _contextInfo; set => Set(ref _contextInfo, value); }

    private string _contextInfoTooltip = string.Empty;
    /// <summary>上下文圆环的悬浮详情：概要 + 构成分解（系统/工具定义/摘要/消息/缓存命中）。</summary>
    public string ContextInfoTooltip { get => _contextInfoTooltip; set => Set(ref _contextInfoTooltip, value); }

    /// <summary>当前模型连接状态：idle / connecting / ready / error（用于模型选择框的状态标识）。</summary>
    private string _modelStatus = "idle";
    public string ModelStatus { get => _modelStatus; set => Set(ref _modelStatus, value); }

    /// <summary>上下文占用比例 0..1（用于环形进度，图标展示）。</summary>
    private double _contextPercent;
    public double ContextPercent { get => _contextPercent; set => Set(ref _contextPercent, value); }

    public bool IsEditing => _editingTurn != null;

    public async Task InitializeAsync()
    {
        IsLoading = true;
        LoadingText = "正在加载…";
        try
        {
            await _store.InitializeAsync();
            var config = await _configService.LoadConfigAsync();
            _agentConfig = config.Agent ?? new AgentConfig();
            _agentConfig.LocalTerminal ??= new LocalTerminalConfig();
            LocalTerminalSettings.ShellPath = string.IsNullOrWhiteSpace(_agentConfig.LocalTerminal.ShellPath)
                ? null : _agentConfig.LocalTerminal.ShellPath;
            LocalTerminalSettings.WorkingDirectory = string.IsNullOrWhiteSpace(_agentConfig.LocalTerminal.WorkingDirectory)
                ? null : _agentConfig.LocalTerminal.WorkingDirectory;
            ReloadPromptTemplates();
            _sidebar.LoadCollapsed();
            // 裁剪放到后台，不阻塞首屏（避免历史库较大时卡在启动）。
            _ = PruneInBackgroundAsync();

            _suppressSelection = true;
            Providers.Clear();
            foreach (var p in AgentProviderGroupConfig.Flatten(_agentConfig.Providers))
                Providers.Add(p);
            RebuildComboProviders();
            SelectedProvider = PickProvider(_agentConfig.ActiveProviderId);
            RebuildComboAgents();
            SelectedAgent = PickAgent(_agentConfig.ActiveAgentId);

            LoadingText = "正在加载会话…";
            await ReloadGroupsAsync();
            await ReloadSessionsAsync();
            _suppressSelection = false;
            var target = Sessions.FirstOrDefault();
            if (target is not null)
            {
                _suppressSelection = true;
                SelectedSession = target;
                _suppressSelection = false;
                await LoadSessionAsync(target.Id);
            }
            else
            {
                // 没有任何历史会话：进入“新任务”态（不建空会话，首次发送消息才落库）。
                StartNewTask();
                await ResetRuntimeAsync(null);
            }

            if (Providers.Count == 0)
                StatusMessage = "未配置大模型：请点「AI 助手设置」添加并启用一个 OpenAI 兼容模型。";
            StartAutoRefresh();
        }
        catch (Exception ex)
        {
            StatusMessage = "初始化失败: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>后台按保留策略裁剪历史（失败忽略，不影响启动）。</summary>
    private async Task PruneInBackgroundAsync()
    {
        try
        {
            await _store.PruneAsync(_agentConfig.RetentionMaxSessions, _agentConfig.RetentionMaxMessages, _agentConfig.RetentionDays, CancellationToken.None);
            // §8.2 附件保留策略：库行被剪除后会话目录可能残留——按保留天数清扫孤儿目录（0=关闭）
            if (_agentConfig.OrphanSessionDirRetentionDays > 0)
            {
                var jobsRoot = Path.Combine(
                    WorkspaceTools.ResolveDir(_agentConfig.WorkspaceDir), WorkspaceLayout.JobsDirName);
                var alive = (await _store.ListSessionsAsync()).Select(s => s.Id).ToArray();
                WorkspaceLayout.SweepOrphanSessionDirs(jobsRoot, _agentConfig.OrphanSessionDirRetentionDays, alive);
            }
        }
        catch
        {
            // 裁剪失败忽略
        }
    }

    /// <summary>启动跨进程对账循环（每 5s：Web 等独立进程写 agent.db 后，桌面自动刷新列表/当前会话）。</summary>
    private void StartAutoRefresh()
    {
        if (_autoRefreshCts is not null)
            return;
        _autoRefreshCts = new CancellationTokenSource();
        _ = AutoRefreshLoopAsync(_autoRefreshCts.Token);
    }

    private async Task AutoRefreshLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                _uiInvoke(() => _ = AutoRefreshTickAsync());
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
        if (_autoRefreshCts is null || IsBusy || IsLoading)
            return;
        try
        {
            var rows = await _store.ListSessionsAsync();
            var snapshot = SessionAutoRefresh.Snapshot(rows);
            if (!string.Equals(snapshot, _lastSessionSnapshot, StringComparison.Ordinal))
            {
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
        Sessions.Clear();
        foreach (var s in list)
            Sessions.Add(s);
        ApplySessionFilter();
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

    private void ApplySessionFilter()
    {
        FilteredSessions.Clear();
        var keyword = SessionFilter?.Trim() ?? string.Empty;
        foreach (var s in Sessions.Where(s => keyword.Length == 0 || s.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            FilteredSessions.Add(s);
        RebuildSidebar();
    }

    private async Task LoadSessionAsync(long id)
    {
        _sessionId = id;
        SyncLocalAuditSession();
        // 需求 D：先取会话工作目录（附件/历史解析与运行时装配都依赖它；空=旧会话回退共享区）
        _sessionWorkspaceDir = await _store.GetSessionWorkspaceDirAsync(id);
        // D8 启用层：读回本会话连接器子集（空=继承全局默认启用集合）
        _sessionMcpSubset = McpSubsetPolicy.ParseSubset(await _store.GetSessionEnabledMcpAsync(id));
        var ctx = CurrentContext;
        ctx.WorkspaceDir = _sessionWorkspaceDir;
        _editingTurn = null;
        IsLoading = true;
        LoadingText = "正在加载会话…";

        try
        {
            // 切换会话：优先用该会话最后使用的模型；已删除/停用则回退全局默认 → 第一个启用模型
            var session = Sessions.FirstOrDefault(s => s.Id == id);
            _suppressSelection = true;
            SelectedProvider = PickProvider(session?.ProviderId ?? _agentConfig.ActiveProviderId);
            var storedAgentId = await _store.GetSessionAgentIdAsync(id);
            SelectedAgent = PickAgent(storedAgentId);
            _suppressSelection = false;
            if (SelectedProvider is not null && !string.Equals(session?.ProviderId, SelectedProvider.Id, StringComparison.Ordinal))
                _ = PersistProviderSelectionAsync(SelectedProvider.Id);
            // 会话记忆的智能体已被删除/从未记录：回退后写回，保持会话与实际一致
            if (SelectedAgent is not null && !string.Equals(storedAgentId, SelectedAgent.Id, StringComparison.Ordinal))
                _ = PersistAgentSelectionAsync(SelectedAgent.Id);

            if (ctx.IsBusy)
            {
                // 该会话正在后台运行（§4.1）：不读库重建、不动它的 runtime——
                // 直接采纳权威镜像恢复上屏，运行继续写这些对象，切回后无缝衔接。
                Turns.Clear();
                foreach (var turn in ctx.LiveTurns)
                    Turns.Add(turn);
                Plan.Clear();
                foreach (var item in ctx.PlanItems)
                    Plan.Add(item);
                _sessionTitled = ctx.LiveTurns.Any(t => !string.IsNullOrEmpty(t.UserText) || t.StorageText is not null);
                ResetTranscriptWindow();
                await RestoreActiveSkillsAsync(id);
                await RestorePlanModeAsync(id);
                await RestoreToolPolicyAsync(id);
                ReloadSkillCatalog();
                IsLoading = false;
                RefreshContextInfo();
                StatusMessage = $"该会话任务进行中（{SessionStateText}）：进度已恢复显示";
                return;
            }

            var (turns, history, hasUser) = await BuildTranscriptAsync(id);
            foreach (var turn in turns)
                Turns.Add(turn);
            ctx.LiveTurns = Turns.ToList();   // 权威镜像与可见集合对齐（运行期间以此为准）
            _sessionTitled = hasUser;
            ResetTranscriptWindow();

            RestorePlan(await _store.GetSessionPlanAsync(id));
            ctx.PlanItems = Plan.ToList();
            await RestoreActiveSkillsAsync(id);
            await RestorePlanModeAsync(id);
            await RestoreToolPolicyAsync(id);
            ReloadSkillCatalog();

            // 对话内容已上屏：先撤遮罩，再后台连接/重建运行时（状态点会显示"连接中…"）。
            IsLoading = false;
            await ResetRuntimeAsync(history);
        }
        finally
        {
            IsLoading = false;
            if (Sessions.FirstOrDefault(s => s.Id == id) is { } loadedRow)
                _loadedSessionUpdatedAt[id] = loadedRow.UpdatedAt;
            // 切换会话 = 换闸门：忙碌/状态文案随当前上下文重算（含“切到后台运行中会话”的早退路径）
            RaiseBusyChanged();
            OnPropertyChanged(nameof(SessionStateText));
            OnPropertyChanged(nameof(SessionSubsetLocked));   // 启用层锁定随会话工作目录/轮次重算（D8）
        }
    }

    // ---- 统一的会话记录解析（UI Turns 与模型 history 的唯一来源） ----

    private const string ToolTracePrefix = LitSSHmcp.Agent.Transcript.ToolTraceFormatter.Prefix;

    /// <summary>重启恢复时重新载入历史图片的最大张数（从最新往回；其余图片只保留文本标记）。</summary>
    private const int ReloadImageQuota = 3;

    /// <summary>
    /// 从持久化记录重建 UI Turns 与模型 history（**唯一入口**，所有加载/切换/重发路径共用）。
    /// 工具轨迹以 <c>role="tool"</c> JSON 行存储：UI 还原为可展开步骤，模型得到一条文本轨迹（不伪造 function_call 配对）。
    /// </summary>
    private async Task<(List<AgentTurn> Turns, List<ChatMessage> History, bool HasUser)> BuildTranscriptAsync(long sessionId)
    {
        var rows = await _store.GetMessagesAsync(sessionId);
        var turns = new List<AgentTurn>();
        var hasUser = rows.Any(r => r.Role == "user");

        AgentTurn? current = null;
        void EnsureCurrent(string timestamp)
        {
            if (current is null)
            {
                current = new AgentTurn(string.Empty, ParseUtc(timestamp));
                turns.Add(current);
            }
        }

        foreach (var row in rows)
        {
            switch (row.Role)
            {
                case "user":
                {
                    var (cleanText, images) = ParseStoredUser(row.Content);
                    current = new AgentTurn(cleanText, ParseUtc(row.Timestamp)) { StorageText = row.Content };
                    foreach (var image in images)
                        current.Images.Add(image);
                    turns.Add(current);
                    break;
                }
                case "tool":
                {
                    var step = AgentTranscript.Deserialize(row.Content);
                    if (step is null)
                        break;
                    EnsureCurrent(row.Timestamp);
                    current!.Steps.Add(AgentTranscript.ToStep(step));
                    break;
                }
                case "reasoning":
                {
                    EnsureCurrent(row.Timestamp);
                    current!.ReasoningText = row.Content;
                    current.ShowReasoning = false;   // 历史恢复默认折叠，点开可看
                    break;
                }
                case "assistant":
                {
                    EnsureCurrent(row.Timestamp);
                    current!.AssistantText = row.Content;
                    current.IsRunning = false;
                    current.SetTimes(LocalHms(row.Timestamp), string.Empty);
                    break;
                }
            }
        }

        var recentTurns = _agentConfig.ColdStartRecentTurns;
        var from = recentTurns > 0 ? Math.Max(0, turns.Count - recentTurns) : 0;
        var history = BuildHistoryFromTurns(turns, turns.Count, from);
        if (from > 0)
            history.Insert(0, new ChatMessage(ChatRole.System,
                $"（本会话较早的 {from} 轮对话已省略以降低冷启动上下文开销；如需关键信息请让用户补充。）"));
        return (turns, history, hasUser);
    }

    /// <summary>
    /// 由内存 Turns 构造模型 history（user + 可选最近图片 → 工具轨迹 System → assistant），
    /// 加载/切模型/编辑重发共用，保证任何路径下模型所见历史一致。
    /// <paramref name="from"/> 起（默认 0）：冷启动只注入最近 N 轮，更早的由滚动摘要代表。
    /// </summary>
    private List<ChatMessage> BuildHistoryFromTurns(IReadOnlyList<AgentTurn> turns, int upTo, int from = 0)
    {
        var vision = SelectedProvider?.SupportsVision == true;
        var reloadable = vision ? CollectReloadImages(turns, upTo) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var history = new List<ChatMessage>();

        for (var i = Math.Max(0, from); i < upTo && i < turns.Count; i++)
        {
            var turn = turns[i];
            var hasUser = turn.StorageText is not null || !string.IsNullOrEmpty(turn.UserText);
            if (hasUser)
            {
                var contents = new List<AIContent>();
                if (!string.IsNullOrEmpty(turn.UserText))
                    contents.Add(new TextContent(turn.UserText));
                foreach (var image in turn.Images)
                {
                    if (!reloadable.Contains(image) || !File.Exists(image))
                        continue;
                    try
                    {
                        var parsed = AttachmentService.Parse(image);
                        if (parsed.ImageBytes is not null)
                            contents.Add(new DataContent(parsed.ImageBytes, parsed.MediaType ?? "image/png"));
                    }
                    catch
                    {
                        // 单张图片读取失败忽略，仅保留文本
                    }
                }
                if (contents.Count == 0)
                    contents.Add(new TextContent(string.Empty));
                history.Add(new ChatMessage(ChatRole.User, contents));
            }

            var trace = AgentTranscript.BuildTrace(turn.Steps);
            if (trace.Length > 0)
                history.Add(new ChatMessage(ChatRole.System, ToolTracePrefix + trace));

            if (!string.IsNullOrEmpty(turn.AssistantText))
                history.Add(new ChatMessage(ChatRole.Assistant, turn.AssistantText));
        }

        return history;
    }

    /// <summary>从最新往回挑选可重载的图片（最多 <see cref="ReloadImageQuota"/> 张）。</summary>
    private static HashSet<string> CollectReloadImages(IReadOnlyList<AgentTurn> turns, int upTo)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        for (var i = Math.Min(upTo, turns.Count) - 1; i >= 0 && count < ReloadImageQuota; i--)
        {
            foreach (var image in turns[i].Images)
            {
                if (count >= ReloadImageQuota)
                    break;
                if (allowed.Add(image))
                    count++;
            }
        }
        return allowed;
    }

    public async Task NewSessionAsync()
    {
        // 懒建：只进入“未保存的新任务”态，不写库；首次发送消息时才落库。
        StartNewTask();
        await ResetRuntimeAsync(null);
    }

    /// <summary>进入“未保存的新任务”态（不写库；首次发送消息时才落库）。</summary>
    private void StartNewTask()
    {
        _sessionId = 0;
        _sessionWorkspaceDir = null;   // 未分配：新任务回退共享工作区，首条消息时再分配独立目录（需求 D）
        _sessionMcpSubset = null;      // D8 启用层：新任务回到“继承全局默认启用集合”态
        CurrentContext.WorkspaceDir = null;
        SyncLocalAuditSession();
        _sessionTitled = false;
        Turns.Clear();
        Plan.Clear();
        CurrentContext.LiveTurns = Turns.ToList();
        CurrentContext.PlanItems = Plan.ToList();
        _editingTurn = null;
        _skills.SetActiveIds(Array.Empty<string>());
        IsPlanMode = false;
        _toolPolicy = new Dictionary<string, AgentToolPolicy>(StringComparer.OrdinalIgnoreCase);
        ReloadSkillCatalog();
        SelectedSession = null;
        SyncSidebarSelection();
        RefreshContextInfo();
        RaiseBusyChanged();   // 换到草稿上下文：闸门与状态文案重算（如删除了运行中的当前会话）
        OnPropertyChanged(nameof(SessionStateText));
        OnPropertyChanged(nameof(SessionSubsetLocked));   // 草稿未启动：启用层回到可编辑（D8）
    }

    private Task DeleteSessionAsync() => DeleteSessionAsync(SelectedSession);

    /// <summary>从 runtime 池摘除某会话：取消其运行、断开状态回调并释放独立 runtime（§4.1 池生命周期）。</summary>
    private async Task RemoveContextAsync(long sessionId)
    {
        if (!_contexts.TryGetValue(sessionId, out var ctx))
            return;
        _contexts.Remove(sessionId);
        ctx.StateChanged -= OnContextStateChanged;
        try { ctx.Cts?.Cancel(); } catch { /* 忽略 */ }
        try
        {
            if (ctx.Runtime is not null)
                await ctx.Runtime.DisposeAsync();
        }
        catch { /* 释放失败忽略 */ }
        ctx.Runtime = null;
        try { ctx.Cts?.Dispose(); } catch { /* 忽略 */ }
        ctx.Cts = null;
    }

    /// <summary>删除会话时清理其工作目录（§5.3.5 附件/产出随会话删除）；越界/失败不影响会话删除，仅返回未删。</summary>
    private (bool Removed, int Files) CleanupSessionWorkspace(string? sessionDir)
    {
        if (string.IsNullOrWhiteSpace(sessionDir)) return (false, 0);
        try
        {
            var (removed, files, _) = WorkspaceLayout.TryRemoveSessionDir(
                WorkspaceTools.ResolveDir(_agentConfig.WorkspaceDir), sessionDir);
            return (removed, files);
        }
        catch
        {
            return (false, 0);
        }
    }

    /// <summary>删除指定会话；若删除的是当前会话才切换到其他会话，否则保持当前显示。</summary>
    public async Task DeleteSessionAsync(AgentSessionRow? session)
    {
        if (session is null) return;
        SessionRemoved?.Invoke(session.Id);
        await RemoveContextAsync(session.Id);   // 池生命周期：删除即停即释放
        var wasCurrent = session.Id == _sessionId;
        // 会话删除入审计（§10.A.1 本地工具补审计三类之一）；FilePath=该会话工作目录（§5.3.5 清理范围, M1-7 落实）
        var sessionDir = await _store.GetSessionWorkspaceDirAsync(session.Id);
        await _store.DeleteSessionAsync(session.Id);
        // 附件/会话产出随会话删除清理（§5.3.5）：仅限共享区 jobs/ 内的会话目录（PathGuard 物理判定）
        var (removedDir, cleanedFiles) = CleanupSessionWorkspace(sessionDir);
        await LocalAudit.LogAsync("session_delete", $"SESSION_DELETE: {session.Title}",
            CommandStatus.Executed,
            result: removedDir ? $"已清理会话目录({cleanedFiles} 个文件)" : null,
            filePath: string.IsNullOrWhiteSpace(sessionDir) ? null : sessionDir,
            sessionId: $"agent-{session.Id}");   // UI 动作显式归因（其他会话可能正在运行 §4.1）
        await ReloadSessionsAsync();

        var next = Sessions.FirstOrDefault();
        if (next is null)
        {
            if (wasCurrent)
            {
                StartNewTask();
                await ResetRuntimeAsync(null);
            }
            return;
        }

        _suppressSelection = true;
        SelectedSession = wasCurrent ? next : Sessions.FirstOrDefault(s => s.Id == _sessionId);
        _suppressSelection = false;
        if (wasCurrent)
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

    /// <summary>归档指定会话：移出会话列表，可在助手设置「归档记录」中恢复或删除。</summary>
    public async Task ArchiveSessionAsync(AgentSessionRow? session)
    {
        if (session is null)
            return;
        SessionRemoved?.Invoke(session.Id);
        await RemoveContextAsync(session.Id);   // 池生命周期：归档即停即释放
        var wasCurrent = session.Id == _sessionId;
        await _store.ArchiveSessionAsync(session.Id);
        await ReloadSessionsAsync();

        var next = Sessions.FirstOrDefault();
        if (next is null)
        {
            if (wasCurrent)
            {
                StartNewTask();
                await ResetRuntimeAsync(null);
            }
            return;
        }

        _suppressSelection = true;
        SelectedSession = wasCurrent ? next : Sessions.FirstOrDefault(s => s.Id == _sessionId);
        _suppressSelection = false;
        if (wasCurrent)
            await LoadSessionAsync(next.Id);
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
            CurrentContext.LiveTurns = Turns.ToList();
            _editingTurn = null;
            Plan.Clear();
            CurrentContext.PlanItems.Clear();
            _ = PersistPlanAsync();
            OnPropertyChanged(nameof(IsEditing));
            RefreshContextInfo();
        }
    }

    public void ReloadSkillCatalog() => _skills.Reload(SkillsRoot, _runtime?.AvailableGroups);

    private Task RestoreActiveSkillsAsync(long sessionId) => _skills.RestoreAsync(sessionId, _store);

    /// <summary>应用技能选择（技能选择窗口回调）：更新 chip、持久化到会话、并热更新运行时系统提示。</summary>
    public void ApplySkillSelection(IReadOnlyList<string> skillIds)
    {
        if (SessionSubsetLocked)
        {
            // D8 启用层：与连接器子集同一锁定原则，首条消息后技能子集也不可再改
            StatusMessage = "首条消息后锁定：技能子集改动将在下个会话生效。";
            return;
        }
        _skills.SetActiveIds(skillIds);
        _ = _skills.PersistAsync(_sessionId, _store);
        if (_runtime is not null && !IsBusy)
            _runtime.SetActiveSkills(_skills.ActiveIdsOrNull);
        StatusMessage = _skills.ActiveIds.Count == 0
            ? "技能：索引模式（未激活具体技能）"
            : $"已激活 {_skills.ActiveIds.Count} 个技能";
    }

    /// <summary>移除某个已激活技能（chip 上的 ×）。</summary>
    public void RemoveActiveSkill(SkillInfo skill)
    {
        var ids = _skills.ActiveIds
            .Where(id => !string.Equals(id, skill.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        ApplySkillSelection(ids);
    }

    private Task PersistActiveSkillsAsync() => _skills.PersistAsync(_sessionId, _store);

    // ---- 技能目录 / 激活（状态由 AgentSkillState 维护） ----
    // ---- 计划模式 ----

    /// <summary>切换计划模式（会话级）。计划模式下只暴露只读工具，模型先出计划待确认。</summary>
    public void SetPlanMode(bool planMode)
    {
        if (IsBusy)
        {
            StatusMessage = "任务进行中，无法切换计划模式；请先停止。";
            OnPropertyChanged(nameof(IsPlanMode));
            return;
        }
        if (_planMode == planMode)
            return;

        IsPlanMode = planMode;
        _ = PersistPlanModeAsync();
        _runtime?.SetPlanMode(planMode);
        StatusMessage = planMode
            ? "已切到计划模式：只做只读调研、可列计划，不会擅自执行；确认后切回执行模式再发指令"
            : "已切到执行模式：可正常执行写操作";
    }

    private async Task PersistPlanModeAsync()
    {
        if (_sessionId <= 0)
            return;
        try { await _store.SetSessionPlanModeAsync(_sessionId, _planMode); }
        catch { /* 保存失败不影响主流程 */ }
    }

    private async Task RestorePlanModeAsync(long sessionId)
    {
        var planMode = false;
        try { planMode = await _store.GetSessionPlanModeAsync(sessionId); }
        catch { planMode = false; }
        _planMode = planMode;
        OnPropertyChanged(nameof(IsPlanMode));
        OnPropertyChanged(nameof(PlanModeText));
        OnPropertyChanged(nameof(PlanModeIndex));
    }

    /// <summary>状态栏里的外部 MCP 连接失败提示（为空=全部正常）。</summary>
    private string McpWarningSuffix()
    {
        var count = _runtime?.McpWarnings.Count ?? 0;
        return count == 0 ? string.Empty : $" · {count} 个外部 MCP 未连接";
    }

    /// <summary>状态栏里的长期记忆禁用提示（已启用但配置不全/初始化失败时显示原因，为空=正常）。</summary>
    private string MemoryWarningSuffix()
    {
        var reason = _runtime?.MemoryDisabledReason;
        return reason is null ? string.Empty : $" · ⚠ 记忆未生效({reason})";
    }

    private async Task EnsureRuntimeAsync(IEnumerable<ChatMessage>? history)
    {
        if (SelectedProvider is null)
            return;

        // 装配目标 = 当前会话上下文；把闭包要烧的值先落到 ctx（运行期间切换会话不再改写这些值）
        var ctx = CurrentContext;
        ctx.WorkspaceDir = _sessionWorkspaceDir;
        ctx.McpSubset = _sessionMcpSubset;   // D8 启用层：会话级连接器子集随装配烧定

        if (ctx.Runtime is not null)
        {
            await ctx.Runtime.DisposeAsync();
            ctx.Runtime = null;
        }

        try
        {
            ModelStatus = "connecting";
            StatusMessage = "正在连接 MCP 服务器…";
            var extra = new List<IAgentTool>();
            extra.AddRange(BuildLocalTerminalTools(ctx));
            // 需求 D/§10.D.1：提升命令——会话目录 → 共享工作区归档（脚本类须桌面确认, 全量记审计）
            extra.Add(new Services.PromoteTool(
                () => ctx.WorkspaceDir,
                () => WorkspaceTools.ResolveDir(_agentConfig.WorkspaceDir),
                (cmd, dir, ct) => RequestLocalApprovalAsync(ctx, cmd, dir, ct),
                () => ctx.SessionKey));
            var localTools = extra.Count > 0 ? extra.ToArray() : null;
            // 资产图谱快照注入系统提示（仅 topology 工具组启用时；由 AgentRuntime 按 availableGroups 再过滤）
            string? assetGraph = null;
            try
            {
                var fullConfig = await _configService.LoadConfigAsync();
                assetGraph = Core.Services.Topology.AssetGraphPromptFormatter.Format(fullConfig);
            }
            catch
            {
                // 资产渲染失败不阻断会话启动
            }
            // 需求 D/D1：会话工作目录与归属用户随运行时装配（根与提示词在装配期烧定；归因经 spawn 注入 MCP 子进程）
            var ownerUserId = ctx.SessionId > 0 ? await _store.GetSessionOwnerUserIdAsync(ctx.SessionId) : "local";
            ctx.Runtime = await AgentRuntime.StartAsync(_agentConfig, SelectedProvider, _bundledSkillsDir, history,
                items => UpdatePlanFor(ctx, items),
                _skills.ActiveIdsOrNull, _planMode, SelectedAgent?.ToPromptContext(),
                extraTools: localTools, assetGraph: assetGraph,
                sessionWorkspaceDir: string.IsNullOrWhiteSpace(ctx.WorkspaceDir) ? null : ctx.WorkspaceDir,
                ownerUserId: ownerUserId,
                enabledMcpIds: _sessionMcpSubset);
            ctx.RuntimeVersion = _poolVersion;
            await RestoreSummaryFromStoreAsync();
            StatusMessage = $"已就绪 · {SelectedProvider.DisplayName} · {ctx.Runtime!.ToolCount} 个工具"
                            + (_agentConfig.ReadOnly ? " · 只读模式" : "")
                            + McpWarningSuffix()
                            + MemoryWarningSuffix();
            ModelStatus = "ready";
            RefreshContextInfo();
            ReloadSkillCatalog();
            ctx.Runtime!.SetToolPolicy(_toolPolicy);
        }
        catch (Exception ex)
        {
            ModelStatus = "error";
            StatusMessage = "连接失败: " + ChatErrorClassifier.Describe(ex);
        }
    }

    private IReadOnlyList<IAgentTool> BuildLocalTerminalTools(SessionRunContext ctx)
    {
        var cfg = _agentConfig.LocalTerminal;
        if (cfg is null || !cfg.Enabled)
            return Array.Empty<IAgentTool>();

        var tools = new List<IAgentTool>();
        if (LocalTerminalResolver is not null)
        {
            tools.Add(new LocalTerminalTool(
                () => ctx.SessionKey,
                key => LocalTerminalResolver?.Invoke(key),
                cfg,
                (cmd, dir, ct) => RequestLocalApprovalAsync(ctx, cmd, dir, ct),
                PersistLocalTerminalConfig,
                _localExemptions,
                LocalTerminalBroker));
        }

        if (LocalTerminalBroker is not null)
        {
            tools.Add(new LocalShellOpenTool(
                () => ctx.SessionKey,
                LocalTerminalBroker,
                cfg,
                (cmd, dir, ct) => RequestLocalApprovalAsync(ctx, cmd, dir, ct),
                PersistLocalTerminalConfig,
                _localExemptions));
            tools.Add(new LocalShellManageTool(() => ctx.SessionKey, LocalTerminalBroker));
            tools.Add(new LocalShellStopTool(() => ctx.SessionKey, LocalTerminalBroker));
        }

        return tools;
    }

    /// <summary>
    /// 本机终端审批：用 BeginInvoke 把弹窗投递到 UI 线程，工具线程只 await TaskCompletionSource。
    /// 旧实现是 Dispatcher.Invoke + ShowDialog 同步阻塞——UI 线程被审批占用期间所有 Dispatcher 操作排队，
    /// 且工具线程无法被取消（评估报告 §4.2）。弹窗期间该会话状态机进 WaitingApproval（§4.1）。
    /// </summary>
    private Task<LocalApprovalDecision> RequestLocalApprovalAsync(SessionRunContext ctx, string command, string workingDirectory, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<LocalApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.SetState(SessionRunState.WaitingApproval);

        void Show()
        {
            try
            {
                var decision = LitSSHmcp.App.Views.LocalCommandApprovalWindow.Prompt(
                    command, workingDirectory, _localExemptions.SessionAllGranted(ctx.SessionKey), ctx.SessionKey);
                tcs.TrySetResult(decision);
            }
            catch (Exception)
            {
                tcs.TrySetResult(LocalApprovalDecision.Deny);
            }
        }

        // 弹窗带 Topmost，取消后用户仍可能点选——TrySetResult 保证只有第一个结果生效（取消优先）。
        var registration = ct.Register(() => tcs.TrySetResult(LocalApprovalDecision.Deny));
        _ = tcs.Task.ContinueWith(_ => registration.Dispose(), TaskContinuationOptions.ExecuteSynchronously);
        // 审批结束回到执行态（取消/异常同样恢复；终态 Done/Failed 由本轮收尾覆盖）
        _ = tcs.Task.ContinueWith(_ => ctx.SetState(SessionRunState.Running),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _uiBeginInvoke(Show);
        return tcs.Task;
    }

    /// <summary>把「总是允许」的命令写回配置（后台，不阻塞工具调用）。</summary>
    private void PersistLocalTerminalConfig()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var cfg = await _configService.LoadConfigAsync();
                var agent = cfg.Agent ??= new AgentConfig();
                agent.LocalTerminal ??= new LocalTerminalConfig();
                agent.LocalTerminal.AutoApprovedCommands = _agentConfig.LocalTerminal?.AutoApprovedCommands ?? Array.Empty<string>();
                await _configService.SaveConfigAsync(cfg);
            }
            catch
            {
                // 记忆失败不影响执行
            }
        });
    }

    /// <summary>切换会话/模型：复用该会话已连接的 MCP 宿主，仅重建对话会话（不重连 MCP）。</summary>
    private async Task ResetRuntimeAsync(IEnumerable<ChatMessage>? history)
    {
        if (SelectedProvider is null)
            return;

        var ctx = CurrentContext;
        // 复用条件（§4.1 池）：有本会话运行时 + 根一致（根在装配期烧进工具与提示词，运行中不可变）
        // + 配置代际未变（设置/连接器重载后旧池元素须重建）
        if (ctx.Runtime is null || RuntimeRootMismatched || ctx.RuntimeVersion != _poolVersion)
        {
            await EnsureRuntimeAsync(history);
            return;
        }

        try
        {
            var rt = ctx.Runtime!;
            rt.ResetSession(SelectedProvider, history);
            rt.SetActiveSkills(_skills.ActiveIdsOrNull);
            rt.SetActiveAgent(SelectedAgent?.ToPromptContext());
            rt.SetPlanMode(_planMode);
            rt.SetToolPolicy(_toolPolicy);
            await RestoreSummaryFromStoreAsync();
            StatusMessage = $"已就绪 · {SelectedProvider.DisplayName} · {rt.ToolCount} 个工具"
                            + (_agentConfig.ReadOnly ? " · 只读模式" : "")
                            + McpWarningSuffix()
                            + MemoryWarningSuffix();
            ModelStatus = "ready";
            RefreshContextInfo();
            ReloadSkillCatalog();
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
        _poolVersion++;   // 配置代际：池内旧运行时下次激活时重建（设置/连接器已变）
        try
        {
            var config = await _configService.LoadConfigAsync();
            _agentConfig = config.Agent ?? new AgentConfig();
            _suppressSelection = true;
            Providers.Clear();
            foreach (var p in AgentProviderGroupConfig.Flatten(_agentConfig.Providers))
                Providers.Add(p);
            RebuildComboProviders();
            RebuildComboAgents();
            var session = Sessions.FirstOrDefault(s => s.Id == _sessionId);
            SelectedProvider = PickProvider(session?.ProviderId ?? _agentConfig.ActiveProviderId);
            // 设置保存后：保持当前智能体 id（设置里可能改了名称/提示词）；已删除则回退全局 → 第一个
            SelectedAgent = PickAgent(_selectedAgent?.Id);
            _suppressSelection = false;
            if (_runtime is not null)
                _runtime.SetActiveAgent(SelectedAgent?.ToPromptContext());

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

    /// <summary>按配置与本会话启用子集刷新「连接器」列表（每次打开菜单时调用；勾选值=本会话是否生效）。</summary>
    public async Task RefreshConnectorsAsync()
    {
        var config = await _configService.LoadConfigAsync();
        var servers = AgentMcpServerConfig.Normalize((config.Agent ?? new AgentConfig()).McpServers);
        // D8 启用层：本会话生效集合 = 全局启用 ∧ 会话子集（null 子集=继承全局启用集合）
        var effectiveIds = McpSubsetPolicy.EffectiveServers(servers, _sessionMcpSubset)
            .Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var statusById = new Dictionary<string, McpHostStatus>(StringComparer.Ordinal);
        foreach (var status in _runtime?.McpHostStatuses ?? Array.Empty<McpHostStatus>())
            statusById[status.ServerId] = status;

        Connectors.Clear();
        foreach (var server in servers)
        {
            var item = new McpConnectorItem
            {
                Id = server.Id,
                Name = string.IsNullOrWhiteSpace(server.Name) ? server.Id : server.Name,
                Builtin = server.Builtin,
                Transport = server.Transport,
                GlobalEnabled = server.Enabled,
                IsEnabled = server.Enabled && effectiveIds.Contains(server.Id)
            };
            item.StatusText = FormatConnectorStatus(server, item.IsEnabled, statusById);
            Connectors.Add(item);
        }
    }

    private static string FormatConnectorStatus(AgentMcpServerConfig server, bool sessionOn, Dictionary<string, McpHostStatus> statusById)
    {
        if (!server.Enabled)
            return "全局已停用";   // 配置层：会话不能反向开启（两级模型，设置窗口才管全局）
        if (!sessionOn)
            return "本会话已停用";
        if (!statusById.TryGetValue(server.Id, out var status))
            return "未连接";   // 运行时尚未建立（如未选模型）
        if (status.Error is not null)
            return "未连接: " + (status.Error.Length > 60 ? status.Error[..60] + "…" : status.Error);
        return $"已连接 · {status.ToolCount} 个工具";
    }

    /// <summary>
    /// 会话窗口勾选 / 取消一台连接器：**只改本会话启用子集（D8 启用层）**，不写全局配置——
    /// 全局增删改/启停与外部启用前校验都在「AI 助手设置 → MCP 服务器」（配置层，会话不可改）。
    /// 首条消息后锁定（见 <see cref="SessionSubsetLocked"/>）：改动下个会话生效。
    /// </summary>
    /// <returns>结果描述（供菜单展示 / 状态栏）。</returns>
    public async Task<string> ToggleSessionConnectorAsync(McpConnectorItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (SessionSubsetLocked)
            return "首条消息后锁定：连接器子集改动将在下个会话生效（全局配置见「AI 助手设置 → MCP 服务器」）。";
        if (!item.GlobalEnabled)
            return $"「{item.Name}」全局已停用：请在「AI 助手设置 → MCP 服务器」启用。";

        var config = await _configService.LoadConfigAsync();
        var servers = AgentMcpServerConfig.Normalize((config.Agent ?? new AgentConfig()).McpServers);
        if (!servers.Any(s => string.Equals(s.Id, item.Id, StringComparison.Ordinal)))
            return "配置中找不到该连接器（可能已被删除）。";

        // 当前子集材料化（null=继承 → 以全局启用集合为基线），再翻转目标项
        var baseline = McpSubsetPolicy.EffectiveServers(servers, _sessionMcpSubset)
            .Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        if (baseline.Contains(item.Id))
            baseline.Remove(item.Id);
        else
            baseline.Add(item.Id);

        // 翻转后与全局启用集合一致 → 回到继承态（null），后续全局新增自动进入本会话
        var globalOn = servers.Where(s => s.Enabled).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        _sessionMcpSubset = baseline.SetEquals(globalOn) ? null : baseline.ToArray();

        if (_sessionId > 0)
            await _store.SetSessionEnabledMcpAsync(_sessionId, McpSubsetPolicy.SerializeSubset(_sessionMcpSubset));
        if (_runtime is not null)
            await EnsureRuntimeAsync(await LoadHistoryFromStoreAsync());

        var on = baseline.Contains(item.Id);
        if (item.Builtin)
            return on ? "本会话已启用内置 MCP" : "本会话已停用内置 MCP（仅本会话，AI 无法操作本机工具）";
        return on ? $"本会话已启用「{item.Name}」" : $"本会话已停用「{item.Name}」（仅本会话，全局配置不变）";
    }

    private async Task SwitchProviderAsync()
    {
        if (IsBusy || SelectedProvider is null) return;
        var history = await LoadHistoryFromStoreAsync();
        StatusMessage = $"切换到 {SelectedProvider.DisplayName} …";
        await ResetRuntimeAsync(history);
    }

    /// <summary>从持久化记录重建模型历史（与 <see cref="LoadSessionAsync"/> 共用同一解析，保证一致）。</summary>
    private async Task<List<ChatMessage>> LoadHistoryFromStoreAsync()
    {
        var (_, history, _) = await BuildTranscriptAsync(_sessionId);
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

    /// <summary>待发送附件（图片 / 文档）。</summary>
    public ObservableCollection<AgentAttachment> Attachments => _attachmentManager.Items;

    public bool HasAttachments => _attachmentManager.HasAttachments;

    public void AddAttachmentFiles(IEnumerable<string> paths) => _attachmentManager.AddFiles(paths);

    public void RemoveAttachment(AgentAttachment attachment) => _attachmentManager.Remove(attachment);

    /// <summary>清除全部待发送附件。</summary>
    public void ClearAttachments()
    {
        if (!_attachmentManager.HasAttachments)
            return;
        _attachmentManager.Clear();
        StatusMessage = "已清除全部附件";
    }

    /// <summary>把某一轮用户消息的附件载入输入区（供“编辑并重发”）。</summary>
    public void LoadAttachmentsFromTurn(AgentTurn turn)
    {
        Attachments.Clear();

        var (images, files) = ExtractTurnAttachments(turn.StorageText);
        if (images.Count == 0 && files.Count == 0 && turn.Images.Count > 0)
            foreach (var abs in turn.Images)
                images.Add((System.IO.Path.GetFileName(abs), abs));

        foreach (var (name, abs) in images)
        {
            if (!File.Exists(abs))
                continue;
            try
            {
                Attachments.Add(new AgentAttachment
                {
                    Name = name,
                    Path = abs,
                    Kind = AttachmentKind.Image,
                    MediaType = MediaTypeFromExt(abs),
                    ImageBytes = File.ReadAllBytes(abs)
                });
            }
            catch { /* 忽略无法读取的附件 */ }
        }

        foreach (var (name, abs) in files)
        {
            if (!File.Exists(abs))
                continue;
            try
            {
                Attachments.Add(new AgentAttachment
                {
                    Name = name,
                    Path = abs,
                    Kind = AttachmentKind.Document,
                    Text = File.ReadAllText(abs)
                });
            }
            catch { /* 忽略无法读取的附件 */ }
        }

        _attachmentManager.NotifyChanged();
    }

    private const int InlineAttachmentChars = 8000;

    private sealed record AttachmentBuild(string Display, string Persisted, string ModelText, List<AIContent> Images, List<string> ImagePaths);

    /// <summary>把附件落盘持久化并拼进本轮输入：返回(展示文本, 持久化文本, 模型文本, 图片内容块, 图片绝对路径)。</summary>
    private async Task<AttachmentBuild> BuildAttachmentPrompt(string text, IReadOnlyList<AgentAttachment> attachments)
    {
        var vision = SelectedProvider?.SupportsVision == true;
        var workspaceRoot = CurrentWorkspaceRoot;      // 需求 D：附件落盘随会话工作目录（未分配=共享区）
        var display = new StringBuilder(text);
        var persisted = new StringBuilder(text);
        var model = new StringBuilder(text);
        var images = new List<AIContent>();
        var imagePaths = new List<string>();

        foreach (var attachment in attachments)
        {
            var relative = PersistAttachment(workspaceRoot, attachment);
            var absolute = System.IO.Path.GetFullPath(System.IO.Path.Combine(workspaceRoot, relative));
            // 附件落盘入审计（§10.A.1 本地工具补审计三类之一；FilePath=绝对路径, 供 M1-7 随会话删除追溯）
            await LocalAudit.LogAsync("attachment_persist", $"ATTACH: {attachment.Name} -> {relative}",
                CommandStatus.Executed, filePath: absolute,
                sessionId: _sessionId > 0 ? $"agent-{_sessionId}" : null);   // 显式归因（别的会话可能正在运行 §4.1）

            if (attachment.IsImage)
            {
                persisted.Append($"\n🖼 图片: {attachment.Name} [{relative}]");
                if (vision && attachment.ImageBytes is not null)
                {
                    images.Add(new DataContent(attachment.ImageBytes, attachment.MediaType ?? "image/png"));
                    imagePaths.Add(absolute);
                }
                else
                {
                    display.Append($"\n🖼 图片: {attachment.Name}（当前模型未开启视觉，已忽略）");
                }
                continue;
            }

            display.Append($"\n📎 文件: {attachment.Name}");
            persisted.Append($"\n📎 文件: {attachment.Name} [{relative}]");
            if (attachment.Text.Length <= InlineAttachmentChars)
            {
                model.Append($"\n\n【附件 {attachment.Name}】\n{attachment.Text}");
            }
            else
            {
                var preview = attachment.Text[..1200];
                model.Append($"\n\n【附件 {attachment.Name}】内容较大（{attachment.Text.Length} 字符），已保存到工作区: {relative}；"
                             + $"请用 ops_doc_read(path=\"{relative}\") 读取完整内容。开头预览:\n{preview}");
            }
        }

        return new AttachmentBuild(display.ToString(), persisted.ToString(), model.ToString(), images, imagePaths);
    }

    private static string BuildSessionTitle(string userText, bool hasImage, bool hasFile)
    {
        var text = string.Join(" ", userText
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith("🖼") && !l.StartsWith("📎")))
            .Trim();

        if (string.IsNullOrWhiteSpace(text))
            text = hasImage ? "[图片]" : hasFile ? "[文件]" : "运维会话";
        return text.Length <= 20 ? text : text[..20];
    }

    /// <summary>附件落盘到工作区 attachments/：图片复制原文件；文档写解析文本。返回相对路径。</summary>
    private static string PersistAttachment(string workspaceRoot, AgentAttachment attachment)
    {
        var dir = System.IO.Path.Combine(workspaceRoot, "attachments");
        Directory.CreateDirectory(dir);
        var safe = string.Concat(attachment.Name.Where(ch => !System.IO.Path.GetInvalidFileNameChars().Contains(ch)));
        if (string.IsNullOrWhiteSpace(safe))
            safe = "attachment";
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");

        try
        {
            if (attachment.IsImage && File.Exists(attachment.Path))
            {
                var ext = System.IO.Path.GetExtension(attachment.Name);
                if (string.IsNullOrEmpty(ext))
                    ext = ".png";
                var fileName = $"{stamp}-{safe}{ext}";
                File.Copy(attachment.Path, System.IO.Path.Combine(dir, fileName), overwrite: true);
                return "attachments/" + fileName;
            }

            var textName = $"{stamp}-{System.IO.Path.GetFileNameWithoutExtension(safe)}.txt";
            File.WriteAllText(System.IO.Path.Combine(dir, textName), attachment.Text);
            return "attachments/" + textName;
        }
        catch
        {
            return "attachments/" + safe;
        }
    }

    /// <summary>发送一轮对话；<paramref name="fixedText"/> 非空时使用该文本（用于"继续"）。</summary>
    private async Task SendTextAsync(string? fixedText)
    {
        // 懒建会话提前到附件落盘与运行时装配之前：新会话首条消息即分配独立工作目录（需求 D，
        // 会话创建即分配、首条消息后锁定），保证附件与运行时都用上会话目录而不是共享区。
        if (_sessionId <= 0)
        {
            var seed = (fixedText ?? Input)?.Trim();
            if (!string.IsNullOrEmpty(seed) || Attachments.Count > 0)
                await CreateSessionWithWorkspaceAsync(BuildSessionTitle(seed ?? string.Empty,
                    Attachments.Any(a => a.IsImage), Attachments.Any(a => !a.IsImage)), titled: false);
        }

        if (_runtime is null || RuntimeRootMismatched)
            await EnsureRuntimeAsync(await LoadHistoryFromStoreAsync());
        if (_runtime is null)
            return;

        // 编辑模式下：截断该轮之后的所有轮次与会话上下文，并用当前输入+附件替换该轮
        if (_editingTurn is not null)
        {
            var editTurn = _editingTurn;
            var idx = Turns.IndexOf(editTurn);
            _editingTurn = null;
            OnPropertyChanged(nameof(IsEditing));
            (CancelEditCommand as RelayCommand)?.RaiseCanExecuteChanged();

            if (idx >= 0)
            {
                for (var i = Turns.Count - 1; i > idx; i--)
                    Turns.RemoveAt(i);
                CurrentContext.LiveTurns = Turns.ToList();   // 编辑截断同步权威镜像
                ResetTranscriptWindow();
                RebuildSessionHistoryUpTo(idx);

                var editText = (fixedText ?? Input)?.Trim();
                var editPending = Attachments.ToList();
                if (!string.IsNullOrEmpty(editText) || editPending.Count > 0)
                {
                    Input = string.Empty;
                    var editDisplay = editText ?? string.Empty;
                    string? editPersisted = null;
                    var editImagePaths = new List<string>();
                    IReadOnlyList<AIContent>? editContents = null;
                    if (editPending.Count > 0)
                    {
                        var build = await BuildAttachmentPrompt(editDisplay, editPending);
                        editDisplay = build.Display;
                        editPersisted = build.Persisted;
                        editImagePaths = build.ImagePaths;
                        var list = new List<AIContent> { new TextContent(build.ModelText) };
                        list.AddRange(build.Images);
                        editContents = list;
                        _attachmentManager.Clear();
                    }

                    editTurn.ResetForRerun(editDisplay);
                    editTurn.StorageText = editPersisted;
                    editTurn.Images.Clear();
                    foreach (var imagePath in editImagePaths)
                        editTurn.Images.Add(imagePath);
                    await RunTurnAsync(editTurn, editContents);
                    return;
                }
            }
        }

        var text = (fixedText ?? Input)?.Trim();
        var pending = Attachments.ToList();
        if (string.IsNullOrEmpty(text) && pending.Count == 0)
            return;

        if (fixedText is null)
        {
            Input = string.Empty;
            if (Plan.Count > 0)
            {
                Plan.Clear();   // 新指令 = 新任务：清掉上一轮残留的计划(模型如需会重新 update_plan)
                CurrentContext.PlanItems.Clear();
                _ = PersistPlanAsync();
            }
        }

        var displayText = text ?? string.Empty;
        string? persistedText = null;
        List<string> imagePaths = new();
        IReadOnlyList<AIContent>? contents = null;
        if (fixedText is null && pending.Count > 0)
        {
            var build = await BuildAttachmentPrompt(displayText, pending);
            displayText = build.Display;
            persistedText = build.Persisted;
            imagePaths = build.ImagePaths;
            var list = new List<AIContent> { new TextContent(build.ModelText) };
            list.AddRange(build.Images);
            contents = list;
            _attachmentManager.Clear();
        }

        var turn = new AgentTurn(displayText, DateTime.UtcNow) { StorageText = persistedText, UiOrdinal = Turns.Count };
        foreach (var imagePath in imagePaths)
            turn.Images.Add(imagePath);
        Turns.Add(turn);
        CurrentContext.LiveTurns.Add(turn);   // 权威镜像与可见集合同步（运行后以其为准）
        await RunTurnAsync(turn, contents);
    }

    /// <summary>
    /// 创建会话行并**立即分配独立会话工作目录**（需求 D：会话创建即分配、首条消息后锁定；
    /// 归因用户：桌面可选登录后为新会话归属登录用户，未登录=local，评估报告 D1）。
    /// <paramref name="titled"/>=false 时保留“首条消息正式命名”的既有口径（SendText 预建用临时标题，
    /// RunTurnAsync 按展示文本重命名）。
    /// </summary>
    private async Task CreateSessionWithWorkspaceAsync(string title, bool titled)
    {
        _sessionId = await _store.CreateSessionAsync(title);
        _sessionTitled = titled;
        SyncLocalAuditSession();

        // D1：桌面可选登录——已登录则新会话归因为登录用户名，未登录为 local（桌面仅归因不限权）
        var owner = _desktopLogin?.Current?.Username ?? "local";
        var dir = WorkspaceLayout.ResolveSessionDir(
            WorkspaceTools.ResolveDir(_agentConfig.WorkspaceDir), owner, _sessionId);
        await _store.SetSessionOwnerUserIdAsync(_sessionId, owner);
        await _store.SetSessionWorkspaceDirAsync(_sessionId, dir);
        // D8 启用层：会话建立即把首条消息前选好的连接器子集落库（null=继承，存空串）；之后随工作目录一起锁定
        await _store.SetSessionEnabledMcpAsync(_sessionId, McpSubsetPolicy.SerializeSubset(_sessionMcpSubset));
        _sessionWorkspaceDir = dir;
        CurrentContext.WorkspaceDir = dir;   // 池元素同步（装配闭包与后台运行读它，不读显示态字段）
        OnPropertyChanged(nameof(SessionSubsetLocked));   // 工作目录已分配 → 启用层锁定（D8，与工作目录同一原则）

        if (SelectedProvider is not null)
            await PersistProviderSelectionAsync(SelectedProvider.Id);
        if (SelectedAgent is not null)
            await PersistAgentSelectionAsync(SelectedAgent.Id);
        await PersistActiveSkillsAsync();
        await PersistPlanModeAsync();
        await PersistToolPolicyAsync();
        await ReloadSessionsAsync();
        _suppressSelection = true;
        SelectedSession = Sessions.FirstOrDefault(s => s.Id == _sessionId);
        _suppressSelection = false;
    }

    /// <summary>会话成本预算检查：达到 agent.budget.maxSessionCost 上限时返回阻止原因（null=可继续；未配置/未填价格时放行）。</summary>
    private string? SessionCostBudgetBlock()
    {
        var limit = _agentConfig.Budget?.MaxSessionCost ?? 0;
        if (limit <= 0)
            return null;
        var session = _runtime?.Session;
        var provider = SelectedProvider;
        if (session is null || provider is null)
            return null;
        if (provider.InputPricePerMTok <= 0 && provider.OutputPricePerMTok <= 0)
            return null;   // 模型未填价格无法判定成本
        var cost = session.SessionInputTokens / 1_000_000.0 * provider.InputPricePerMTok
                   + session.SessionOutputTokens / 1_000_000.0 * provider.OutputPricePerMTok;
        return cost >= limit
            ? $"会话估算成本已达预算上限 ¥{cost:F4} ≥ ¥{limit:F4}，已阻止发送（AI 助手设置 → 上下文与输出 → 会话成本上限可调整）"
            : null;
    }

    private async Task RunTurnAsync(AgentTurn turn, IReadOnlyList<AIContent>? contents = null)
    {
        var ctx = CurrentContext;
        if (ctx.Runtime is null)
            return;
        if (ctx.IsBusy)
        {
            // 双触发防线（回车连点等）：会话闸门已占用时不再开第二轮（AgentSession 还有硬闸兜底）
            turn.Note = "上一轮仍在进行中，请稍候。";
            turn.MarkDone(DateTime.UtcNow);
            return;
        }

        // 会话成本预算：达到上限后阻止新的发送（防止失控烧钱）
        var budgetBlock = SessionCostBudgetBlock();
        if (budgetBlock is not null)
        {
            turn.Note = budgetBlock;
            StatusMessage = budgetBlock;
            ShowNotice(budgetBlock, seconds: 6);
            return;
        }

        // 懒建会话：点“新建任务”不写库；首次真正发送消息时才落库并出现在侧栏。
        if (_sessionId <= 0)
        {
            var title = BuildSessionTitle(turn.UserText, turn.Images.Count > 0, turn.UserText.Contains("📎"));
            await CreateSessionWithWorkspaceAsync(title, titled: true);
            ctx = CurrentContext;   // 草稿 → 落库会话：上下文可能已换
        }
        else if (_sessionTitled is false)
        {
            // 旧库中“有会话但尚未发过消息”的空会话：同样以首条消息命名。
            await _store.RenameSessionAsync(_sessionId, BuildSessionTitle(turn.UserText, turn.Images.Count > 0, turn.UserText.Contains("📎")));
            _sessionTitled = true;
            await ReloadSessionsAsync();
        }

        // 运行期固定引用：本轮只属于 ctx（其间显示层可随意切换会话）
        var runtime = ctx.Runtime;
        if (runtime is null)
            return;

        // 状态机（§4.1）：排队 → 执行中 → （待审批由审批包装置位）→ 完成/失败/停止
        ctx.SetState(SessionRunState.Queued);
        RaiseBusyChanged();
        var cts = new CancellationTokenSource();
        ctx.Cts = cts;
        ctx.ResetBuffers();
        var assistantText = new StringBuilder();
        var progress = new SyncProgress(e => OnAgentEvent(e, turn, assistantText, ctx), _uiInvoke);
        var turnSucceeded = false;
        // 归因作用域：本轮派生的工具线程/续延（本地工具审计）都挂到本会话，不随显示切换串号（§4.1）
        using var auditScope = LocalAudit.BeginRunScope(ctx.AuditId);

        try
        {
            ctx.SetState(SessionRunState.Running);
            RaiseBusyChanged();
            await runtime.Session.SendAsync(contents ?? new AIContent[] { new TextContent(turn.UserText) }, progress, cts.Token);
            turnSucceeded = true;
            if (ReferenceEquals(ctx, CurrentContext))
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
            if (ReferenceEquals(ctx, CurrentContext))
                StatusMessage = reason;
        }
        finally
        {
            auditScope.Dispose();
            FlushDelta(turn, ctx);
            FlushReasoning(turn, ctx);
            turn.MarkDone(DateTime.UtcNow);
            ctx.SetState(turnSucceeded ? SessionRunState.Done
                : (cts.IsCancellationRequested ? SessionRunState.Stopped : SessionRunState.Failed));
            if (ReferenceEquals(ctx.Cts, cts))
            {
                ctx.Cts = null;
                cts.Dispose();
            }
            // 落库/摘要/触达一律按**运行所属会话**（ctx），杜绝后台完成的轮写进当前显示的会话；
            // 会话可能已被并行删除（删除即停），落库失败一律吞掉，绝不让收尾半途抛出
            try
            {
                await PersistTranscriptAsync(ctx);
                await PersistSummaryAsync(ctx);
            }
            catch
            {
                // 落库失败忽略（会话已删除/磁盘故障）
            }
            try
            {
                await _store.PruneAsync(_agentConfig.RetentionMaxSessions, _agentConfig.RetentionMaxMessages, _agentConfig.RetentionDays, CancellationToken.None);
            }
            catch
            {
                // 裁剪失败忽略
            }
            try
            {
                await _store.TouchSessionAsync(ctx.SessionId);
            }
            catch
            {
                // 触达时间失败忽略（会话可能已删除）
            }
            try
            {
                // 质量门：仅成功轮入库 raw 记忆（取消/失败轮不污染召回）
                if (ctx.Runtime is not null && turnSucceeded)
                    await ctx.Runtime.IndexTurnAsync(turn.UserText, assistantText.ToString());
            }
            catch
            {
                // 记忆写入失败忽略
            }
            await ReloadSessionsAsync();
            if (ReferenceEquals(ctx, CurrentContext))
            {
                _suppressSelection = true;
                SelectedSession = Sessions.FirstOrDefault(s => s.Id == ctx.SessionId);
                _suppressSelection = false;
                SyncSidebarSelection();
                RefreshContextInfo();
            }
            // 终态（Done/Failed/Stopped）保留到下一轮开始：不占用闸门（IsBusy 只认排队/执行/待审批），
            // 同时让状态文案可见（“已停止/上轮失败”）。
            RaiseBusyChanged();
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
        var contents = await BuildContentsForTurnAsync(turn.UserText, turn.StorageText);
        await RunTurnAsync(turn, contents);
    }

    /// <summary>进入编辑：把该轮用户指令载入输入框，发送时将替换该指令及其后的对话。</summary>
    public void BeginEdit(AgentTurn? turn)
    {
        if (turn is null || IsBusy)
            return;
        _editingTurn = turn;
        Input = GetUserPlainText(turn);
        LoadAttachmentsFromTurn(turn);
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

        _connectionTesting = true;
        RaiseBusyChanged();
        try
        {
            var client = ChatClientFactory.Create(SelectedProvider);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await client.GetResponseAsync(
                new[] { new ChatMessage(ChatRole.User, "ping") },
                new ChatOptions { MaxOutputTokens = 8, Temperature = 0 },
                CancellationToken.None);
            stopwatch.Stop();
            StatusMessage = $"连接正常 · {SelectedProvider.DisplayName}（{stopwatch.ElapsedMilliseconds} ms）";
        }
        catch (Exception ex)
        {
            StatusMessage = "连接失败: " + ChatErrorClassifier.Describe(ex);
        }
        finally
        {
            _connectionTesting = false;
            RaiseBusyChanged();
        }
    }

    private void OnAgentEvent(AgentEvent e, AgentTurn turn, StringBuilder assistantText, SessionRunContext ctx)
    {
        switch (e.Kind)
        {
            case AgentEventKind.AssistantText:
                var chunk = e.Text ?? string.Empty;
                if (chunk.Length == 0) break;
                assistantText.Append(chunk);
                ctx.PendingDelta.Append(chunk);
                if ((DateTime.UtcNow - ctx.LastFlushUtc).TotalMilliseconds >= StreamFlushMs)
                    FlushDelta(turn, ctx);
                break;

            case AgentEventKind.AssistantReasoning:
                var reasoningChunk = e.Text ?? string.Empty;
                if (reasoningChunk.Length == 0) break;
                ctx.PendingReasoning.Append(reasoningChunk);
                if ((DateTime.UtcNow - ctx.LastReasoningFlushUtc).TotalMilliseconds >= StreamFlushMs)
                    FlushReasoning(turn, ctx);
                break;

            case AgentEventKind.ToolCall:
                FlushDelta(turn, ctx);
                FlushReasoning(turn, ctx);
                turn.Steps.Add(new AgentStep(e.ToolName ?? "工具", e.ArgumentsJson ?? string.Empty, e.Destructive, e.CallId));
                break;

            case AgentEventKind.ToolResult:
                FlushDelta(turn, ctx);
                FlushReasoning(turn, ctx);
                var body = Truncate(e.Text, 20000);
                var denied = LooksApprovalDenied(e.Text);
                var pending = e.CallId is null
                    ? turn.Steps.LastOrDefault(s => s.IsRunning && s.Tool == (e.ToolName ?? "工具"))
                    : turn.Steps.LastOrDefault(s => s.IsRunning && s.CallId == e.CallId)
                      ?? turn.Steps.LastOrDefault(s => s.IsRunning && s.Tool == (e.ToolName ?? "工具"));
                pending?.Complete(e.ToolSuccess && !denied, denied ? "（审批未通过）\n" + body : body, e.DurationMs);
                break;

            case AgentEventKind.MemoryRecall:
                // 记忆召回可见性：以"信息步骤"展示本轮注入的记忆（不参与重试/落库/模型轨迹）
                FlushDelta(turn, ctx);
                FlushReasoning(turn, ctx);
                var recallStep = new AgentStep("记忆召回", string.Empty, isInfo: true);
                recallStep.Complete(true, e.Text ?? string.Empty, 0);
                turn.Steps.Add(recallStep);
                break;

            case AgentEventKind.AssetGraphContext:
                // Graph-RAG 可见性：展示本轮自动注入的资产邻域，便于核对模型依据
                FlushDelta(turn, ctx);
                FlushReasoning(turn, ctx);
                var graphStep = new AgentStep("资产图谱", string.Empty, isInfo: true);
                graphStep.Complete(true, e.Text ?? string.Empty, 0);
                turn.Steps.Add(graphStep);
                break;

            case AgentEventKind.Error:
                FlushDelta(turn, ctx);
                FlushReasoning(turn, ctx);
                turn.Note = e.Text ?? "错误";
                break;

            case AgentEventKind.Done:
                FlushDelta(turn, ctx);
                FlushReasoning(turn, ctx);
                break;
        }
    }

    private void FlushDelta(AgentTurn turn, SessionRunContext ctx)
    {
        if (ctx.PendingDelta.Length == 0)
            return;
        turn.AssistantText += ctx.PendingDelta.ToString();
        ctx.PendingDelta.Clear();
        ctx.LastFlushUtc = DateTime.UtcNow;
    }

    private void FlushReasoning(AgentTurn turn, SessionRunContext ctx)
    {
        if (ctx.PendingReasoning.Length == 0)
            return;
        turn.ReasoningText += ctx.PendingReasoning.ToString();
        ctx.PendingReasoning.Clear();
        ctx.LastReasoningFlushUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// update_plan 路由（§4.1 进度按会话）：运行时装配期把会话 ctx 烧进回调——
    /// 计划先写该会话的权威镜像；当前正好显示该会话时同步可见计划面板并持久化。
    /// </summary>
    private void UpdatePlanFor(SessionRunContext ctx, IReadOnlyList<PlanItem> items) => _uiInvoke(() =>
    {
        ctx.PlanItems.Clear();
        foreach (var item in items)
            ctx.PlanItems.Add(new PlanItemVM(item));
        if (ReferenceEquals(ctx, CurrentContext))
        {
            Plan.Clear();
            foreach (var item in ctx.PlanItems)
                Plan.Add(item);
            _ = PersistPlanAsync(ctx);
        }
    });

    private sealed record StoredPlanItem(string Text, bool Done);

    /// <summary>把任务计划持久化到会话行（跨重启恢复）；<paramref name="ctx"/> 缺省=当前会话。</summary>
    private async Task PersistPlanAsync(SessionRunContext? ctx = null)
    {
        ctx ??= CurrentContext;
        if (ctx.SessionId <= 0)
            return;
        try
        {
            IEnumerable<PlanItemVM> source = ReferenceEquals(ctx, CurrentContext) ? Plan : ctx.PlanItems;
            var json = !source.Any()
                ? string.Empty
                : JsonSerializer.Serialize(source.Select(p => new StoredPlanItem(p.Text, p.Done)).ToList());
            await _store.SetSessionPlanAsync(ctx.SessionId, json);
        }
        catch
        {
            // 计划保存失败不影响主流程
        }
    }

    /// <summary>恢复持久化的任务计划（无效 JSON 时视为无计划）；镜像到当前会话上下文。</summary>
    private void RestorePlan(string? json)
    {
        Plan.Clear();
        if (string.IsNullOrWhiteSpace(json))
        {
            CurrentContext.PlanItems = Plan.ToList();
            return;
        }
        try
        {
            var items = JsonSerializer.Deserialize<List<StoredPlanItem>>(json);
            if (items is null)
            {
                CurrentContext.PlanItems = Plan.ToList();
                return;
            }
            foreach (var item in items)
                Plan.Add(new PlanItemVM(new PlanItem(item.Done, item.Text)));
        }
        catch
        {
            // 损坏的计划 JSON 忽略
        }
        CurrentContext.PlanItems = Plan.ToList();
    }

    private static bool LooksApprovalDenied(string? text) =>
        !string.IsNullOrEmpty(text) &&
        (text.Contains("approval_timeout", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("approval_unavailable", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("\"status\":\"rejected\"", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("\"status\": \"rejected\"", StringComparison.OrdinalIgnoreCase));

    /// <summary>从库里恢复该会话的滚动摘要到当前运行时（会话/模型切换、启动时调用）。</summary>
    private async Task RestoreSummaryFromStoreAsync()
    {
        if (_runtime is null || _sessionId <= 0)
            return;
        try
        {
            _runtime.Session.RestoreSummary(await _store.GetSessionSummaryAsync(_sessionId));
        }
        catch
        {
            // 恢复失败不影响主流程
        }
    }

    /// <summary>把该会话滚动摘要持久化（每轮结束、手动压缩、清空上下文后调用）；<paramref name="ctx"/> 缺省=当前会话。</summary>
    private async Task PersistSummaryAsync(SessionRunContext? ctx = null)
    {
        ctx ??= CurrentContext;
        if (ctx.SessionId <= 0)
            return;
        try
        {
            await _store.SetSessionSummaryAsync(ctx.SessionId, ctx.Runtime?.Session.Summary ?? string.Empty);
        }
        catch
        {
            // 保存失败不影响主流程
        }
    }

    private void RebuildSessionHistoryUpTo(int turnIndex)
    {
        if (_runtime is null) return;
        var recent = _agentConfig.ColdStartRecentTurns;
        var from = recent > 0 ? Math.Max(0, turnIndex - recent) : 0;
        _runtime.Session.ReplaceHistory(BuildHistoryFromTurns(Turns, turnIndex, from));
    }

    /// <summary>把该会话的权威轮次列表落库（<paramref name="ctx"/> 缺省=当前会话；轮结束按运行所属会话调用）。</summary>
    private async Task PersistTranscriptAsync(SessionRunContext? ctx = null)
    {
        ctx ??= CurrentContext;
        IEnumerable<AgentTurn> turns = ReferenceEquals(ctx, CurrentContext) ? Turns : ctx.LiveTurns;
        var messages = new List<(string Role, string Content)>();
        foreach (var turn in turns)
        {
            if (!string.IsNullOrEmpty(turn.UserText) || string.IsNullOrEmpty(turn.StorageText) is false)
                messages.Add(("user", turn.StorageText ?? turn.UserText));
            if (!string.IsNullOrEmpty(turn.ReasoningText))
                messages.Add(("reasoning", turn.ReasoningText));
            foreach (var step in turn.Steps)
                messages.Add(("tool", AgentTranscript.Serialize(step)));   // IsInfo 随行落库，仅展示、不进模型轨迹
            if (!string.IsNullOrEmpty(turn.AssistantText))
                messages.Add(("assistant", turn.AssistantText));
        }
        await _store.ReplaceMessagesAsync(ctx.SessionId, messages);
    }

    private void RefreshContextInfo()
    {
        var session = _runtime?.Session;
        var tokens = session?.EstimatedTokensCalibrated ?? 0;
        var tools = _runtime?.ToolCount ?? 0;
        var limit = _agentConfig.ContextTokenLimit;
        ContextPercent = limit > 0 ? Math.Min(1.0, (double)tokens / limit) : 0;
        var pct = limit > 0 ? $" ({ContextPercent * 100:F0}%)" : string.Empty;
        var usage = session is { HasRealUsage: true }
            ? $" · 真实用量 in {session.SessionInputTokens} / out {session.SessionOutputTokens} tokens"
              + (session.SessionCachedInputTokens > 0
                  ? $" · 缓存命中 {session.SessionCachedInputTokens} ({(session.SessionInputTokens > 0 ? session.SessionCachedInputTokens * 100 / session.SessionInputTokens : 0)}%)"
                  : string.Empty)
            : " · 估算值(该模型未返回用量)";
        ContextInfo = $"轮次 {Turns.Count} · 上下文 ~{tokens} tokens{pct}{usage} · 上限 {_agentConfig.ContextLimit} 条 / {limit} tokens{( _agentConfig.AutoSummarize ? " · 自动摘要" : "")} · {tools} 工具";

        if (session is null)
        {
            ContextInfoTooltip = ContextInfo;
            return;
        }

        var b = session.GetContextBreakdown();
        var lines = new List<string>
        {
            ContextInfo,
            "── 构成（估算）──",
            $"系统提示 ~{b.SystemPrompt}",
            $"工具定义 ~{b.ToolDefinitions}（固定前缀，不计入上限）",
            $"摘要 ~{b.Summary}",
            $"用户 ~{b.User} · 助手 ~{b.Assistant} · 工具结果 ~{b.ToolResults}",
            $"消息合计 ~{b.MessagesTotal} · 请求合计(含工具) ~{b.RequestTotal}"
        };
        ContextInfoTooltip = string.Join("\n", lines);
    }

    /// <summary>解析持久化的用户消息：剥离附件标记行，收集图片绝对路径，文件以名字回显。</summary>
    private (string Text, List<string> Images) ParseStoredUser(string content)
    {
        var workspaceRoot = CurrentWorkspaceRoot;      // 需求 D：附件标记解析随会话工作目录
        var clean = new List<string>();
        var images = new List<string>();
        var fileNames = new List<string>();

        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("🖼"))
            {
                var rel = ExtractBracket(line);
                if (!string.IsNullOrEmpty(rel))
                    images.Add(Path.GetFullPath(Path.Combine(workspaceRoot, rel)));
            }
            else if (line.StartsWith("📎"))
            {
                var name = line.Replace("📎 文件:", string.Empty).Trim();
                var bracket = name.IndexOf('[');
                if (bracket > 0)
                    name = name[..bracket].Trim();
                if (name.Length > 0)
                    fileNames.Add(name);
            }
            else
            {
                clean.Add(raw);
            }
        }

        var text = string.Join("\n", clean).Trim();
        if (fileNames.Count > 0)
            text += "\n📎 文件: " + string.Join(", ", fileNames);
        return (text, images);
    }

    private static string? ExtractBracket(string line)
    {
        var open = line.LastIndexOf('[');
        var close = line.LastIndexOf(']');
        return open >= 0 && close > open ? line[(open + 1)..close] : null;
    }

    private static string ExtractMarkerName(string line)
    {
        var text = line;
        var colon = text.IndexOf(':');
        if (colon >= 0)
            text = text[(colon + 1)..].Trim();
        var bracket = text.IndexOf('[');
        if (bracket > 0)
            text = text[..bracket].Trim();
        return text;
    }

    /// <summary>从持久化文本还原附件（图片/文件）的绝对路径。</summary>
    private (List<(string Name, string Abs)> Images, List<(string Name, string Abs)> Files) ExtractTurnAttachments(string? storageText)
    {
        var images = new List<(string, string)>();
        var files = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(storageText))
            return (images, files);

        var workspaceRoot = CurrentWorkspaceRoot;      // 需求 D：附件还原随会话工作目录
        foreach (var raw in storageText.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            string? rel = null;
            if (line.StartsWith("🖼"))
            {
                rel = ExtractBracket(line);
                if (rel is not null)
                    images.Add((ExtractMarkerName(line), Path.GetFullPath(Path.Combine(workspaceRoot, rel))));
            }
            else if (line.StartsWith("📎"))
            {
                rel = ExtractBracket(line);
                if (rel is not null)
                    files.Add((ExtractMarkerName(line), Path.GetFullPath(Path.Combine(workspaceRoot, rel))));
            }
        }
        return (images, files);
    }

    private static string MediaTypeFromExt(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };

    /// <summary>用给定文本 + 历史附件重建模型输入（供“重发/编辑并重发”）。</summary>
    private async Task<IReadOnlyList<AIContent>> BuildContentsForTurnAsync(string text, string? storageText)
    {
        var (images, files) = ExtractTurnAttachments(storageText);
        if (images.Count == 0 && files.Count == 0)
            return new AIContent[] { new TextContent(text) };

        var vision = SelectedProvider?.SupportsVision == true;
        var model = new StringBuilder(text);
        var dataParts = new List<AIContent>();

        foreach (var (name, abs) in images)
        {
            if (vision && File.Exists(abs))
            {
                try
                {
                    var parsed = AttachmentService.Parse(abs);
                    if (parsed.ImageBytes is not null)
                        dataParts.Add(new DataContent(parsed.ImageBytes, parsed.MediaType ?? "image/png"));
                }
                catch { model.Append($"\n（图片 {name} 读取失败）"); }
            }
            else
            {
                model.Append($"\n（图片 {name} 未附带：文件缺失或模型未开启视觉）");
            }
        }

        foreach (var (name, abs) in files)
        {
            if (File.Exists(abs))
            {
                try { model.Append($"\n\n【附件 {name}】\n{await File.ReadAllTextAsync(abs)}"); }
                catch { model.Append($"\n📎 文件 {name}（读取失败）"); }
            }
            else
            {
                model.Append($"\n📎 文件 {name}（已删除）");
            }
        }

        var list = new List<AIContent> { new TextContent(model.ToString()) };
        list.AddRange(dataParts);
        return list;
    }

    /// <summary>用户消息的纯文本（不含附件标记行），用于编辑/复制。</summary>
    public string GetUserPlainText(AgentTurn turn)
    {
        var source = string.IsNullOrWhiteSpace(turn.StorageText) ? turn.UserText : turn.StorageText!;
        return string.Join("\n", source.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => !l.TrimStart().StartsWith("🖼") && !l.TrimStart().StartsWith("📎")))
            .Trim();
    }

    /// <summary>用户消息附件的绝对路径（图片 + 文件），用于复制。</summary>
    public IReadOnlyList<string> GetAttachmentPaths(AgentTurn turn)
    {
        var result = new List<string>(turn.Images);
        var (images, files) = ExtractTurnAttachments(turn.StorageText);
        foreach (var (_, abs) in images)
            if (!result.Contains(abs, StringComparer.OrdinalIgnoreCase))
                result.Add(abs);
        foreach (var (_, abs) in files)
            if (!result.Contains(abs, StringComparer.OrdinalIgnoreCase))
                result.Add(abs);
        return result;
    }

    private static DateTime ParseUtc(string timestamp) =>        DateTime.TryParse(timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt.ToUniversalTime() : DateTime.UtcNow;

    private static string LocalHms(string timestamp) => ParseUtc(timestamp).ToLocalTime().ToString("HH:mm:ss");

    private static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) ? string.Empty : (text.Length <= max ? text : text[..max] + " …(截断)");

    public async ValueTask DisposeAsync()
    {
        try { _autoRefreshCts?.Cancel(); } catch { /* 忽略 */ }
        _autoRefreshCts?.Dispose();
        _autoRefreshCts = null;
        // 窗口关闭：停掉池内所有会话的运行并释放各自 runtime（含 MCP 子进程）
        foreach (var ctx in _contexts.Values)
        {
            try { ctx.Cts?.Cancel(); } catch { /* 忽略 */ }
        }
        foreach (var ctx in _contexts.Values)
        {
            try
            {
                if (ctx.Runtime is not null)
                    await ctx.Runtime.DisposeAsync();
            }
            catch { /* 释放失败忽略 */ }
            ctx.Runtime = null;
        }
        _contexts.Clear();
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
public partial class AgentTurn : INotifyPropertyChanged
{
    private DateTime _startedLocal;

    public AgentTurn(string userText, DateTime timeUtc)
    {
        _userText = userText;
        _startedLocal = timeUtc.ToLocalTime();
        _userTime = _startedLocal.ToString("HH:mm:ss");
        Steps.CollectionChanged += OnStepsChanged;
    }

    private string _userText;
    public string UserText { get => _userText; set => SetField(ref _userText, value); }

    /// <summary>持久化用的原始文本（含附件标记与工作区相对路径）；无则用 UserText。</summary>
    public string? StorageText { get; set; }

    /// <summary>在会话 Turns 中的序号（0 起，用于转录分页过滤）。</summary>
    public int UiOrdinal { get; set; }

    private string _userTime;
    public string UserTime { get => _userTime; private set => SetField(ref _userTime, value); }

    public ObservableCollection<AgentStep> Steps { get; } = new();

    /// <summary>本轮附带的图片文件路径（用于气泡内缩略图预览）。</summary>
    public ObservableCollection<string> Images { get; } = new();
    public bool HasSteps => Steps.Count > 0;
    public int FailedToolCount => Steps.Count(s => !s.IsRunning && !s.Success);
    public bool HasFailedTools => FailedToolCount > 0;
    public bool HasDestructiveTools => Steps.Any(s => s.Destructive);
    public Visibility ToolsVisibility => HasSteps ? Visibility.Visible : Visibility.Collapsed;

    public string ToolHeader
    {
        get
        {
            if (Steps.Count == 0)
                return string.Empty;

            var text = $"工具过程 · {Steps.Count} 个";
            if (FailedToolCount > 0)
                text += $" · {FailedToolCount} 失败";
            if (HasDestructiveTools)
                text += " · 含写操作";
            return text;
        }
    }

    private bool _showTools = true;
    public bool ShowTools { get => _showTools; set => SetField(ref _showTools, value); }

    private string _assistantText = string.Empty;
    public string AssistantText
    {
        get => _assistantText;
        set
        {
            if (!SetField(ref _assistantText, value)) return;
            OnPropertyChanged(nameof(AssistantVisibility));
            OnPropertyChanged(nameof(RunningText));
        }
    }

    private string _reasoningText = string.Empty;
    /// <summary>本轮模型输出的思考过程（仅展示，不回喂上下文、不计入回答）。</summary>
    public string ReasoningText
    {
        get => _reasoningText;
        set
        {
            if (!SetField(ref _reasoningText, value)) return;
            OnPropertyChanged(nameof(HasReasoning));
            OnPropertyChanged(nameof(ReasoningVisibility));
            OnPropertyChanged(nameof(RunningText));
        }
    }

    public bool HasReasoning => _reasoningText.Length > 0;
    public Visibility ReasoningVisibility => _reasoningText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private bool _showReasoning = true;
    /// <summary>思考过程展开/折叠：运行中默认展开，完成后自动折叠（可手动展开）。</summary>
    public bool ShowReasoning { get => _showReasoning; set => SetField(ref _showReasoning, value); }

    private bool _isRunning = true;
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (!SetField(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(Footer));
            OnPropertyChanged(nameof(RunningVisibility));
            OnPropertyChanged(nameof(RunningText));
            OnPropertyChanged(nameof(AssistantVisibility));
        }
    }
    public Visibility RunningVisibility => _isRunning ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>运行占位文案：正在输出思考过程时显示"思考中…"，否则"运行中…"。</summary>
    public string RunningText => HasReasoning && string.IsNullOrEmpty(_assistantText) ? "思考中…" : "运行中…";

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
        ShowTools = HasFailedTools;
        ShowReasoning = false;
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
        ReasoningText = string.Empty;
        Note = string.Empty;
        Steps.Clear();
        _endTime = string.Empty;
        _duration = string.Empty;
        ShowTools = true;
        ShowReasoning = true;
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
    public AgentStep(string tool, string argsJson, bool destructive = false, string? callId = null, bool isInfo = false)
    {
        Tool = tool;
        ArgsJson = argsJson;
        _displayArgs = string.IsNullOrWhiteSpace(argsJson) ? string.Empty : ToolResultFormatter.Format(argsJson, pretty: true);
        Destructive = destructive;
        CallId = callId;
        IsInfo = isInfo;
    }

    public string Tool { get; }
    public string ArgsJson { get; }
    public bool Destructive { get; }

    /// <summary>信息类步骤（如"记忆召回"）：仅展示，不参与重试、不进模型工具轨迹；随会话记录落库以便历史回看。</summary>
    public bool IsInfo { get; }

    /// <summary>模型侧的工具调用 Id（并行执行时用于把结果精确回填到对应步骤）。</summary>
    public string? CallId { get; }

    private readonly string _displayArgs;

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

    /// <summary>结果的可读文本（解析 JSON 后重排、中文不转义）；仅用于展示，持久化/重试仍用原始 <see cref="Result"/>。</summary>
    private string _displayResult = string.Empty;

    private long _durationMs;
    public long DurationMs { get => _durationMs; private set { _durationMs = value; Raise(); Raise(nameof(Tail)); } }

    public string Label => IsInfo ? Tool : $"调用 {Tool}";
    public string Tail => IsRunning
        ? (Destructive ? "调用中…（可能等待人工审批）" : "调用中…")
        : (IsInfo ? "已注入本轮上下文" : $"{DurationMs} ms");
    public string Detail => (_displayArgs.Length == 0 ? string.Empty : "入参: " + _displayArgs + "\n\n") + (_displayResult.Length > 0 ? "结果:\n" + _displayResult : string.Empty);
    public Visibility RetryVisibility => !IsRunning && !Success ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DestructiveVisibility => Destructive ? Visibility.Visible : Visibility.Collapsed;

    private bool _showDetail;
    /// <summary>步骤详情默认折叠；失败时自动展开，成功时保持折叠（可手动切换）。</summary>
    public bool ShowDetail
    {
        get => _showDetail;
        set { if (_showDetail == value) return; _showDetail = value; Raise(); }
    }

    public void Complete(bool success, string result, long durationMs)
    {
        Success = success;
        Result = result;
        _displayResult = ToolResultFormatter.Format(result, pretty: true);
        DurationMs = durationMs;
        IsRunning = false;
        ShowDetail = !success;
        Raise(nameof(Detail));
        Raise(nameof(RetryVisibility));
    }

    public void Restart()
    {
        IsRunning = true;
        Result = string.Empty;
        _displayResult = string.Empty;
        DurationMs = 0;
        ShowDetail = false;
        Raise(nameof(Detail));
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
