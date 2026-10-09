using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LitSSHmcp.Agent;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.App.ViewModels;

/// <summary>AI 助手设置：多模型接入（启用/停用）+ 系统提示/技能/上下文/只读/服务器路径。</summary>
public class AgentSettingsViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService = new ConfigService();
    private readonly IContextStore _contextStore = new ContextStore();

    public AgentSettingsViewModel() => Load();

    public ObservableCollection<AgentProviderEdit> Providers { get; } = new();

    /// <summary>已归档的会话（“归档记录”标签页）。</summary>
    public ObservableCollection<AgentSessionRow> ArchivedSessions { get; } = new();

    /// <summary>加载已归档会话列表。</summary>
    public async Task LoadArchivedAsync()
    {
        try
        {
            await _contextStore.InitializeAsync();
            var list = await _contextStore.ListArchivedSessionsAsync();
            ArchivedSessions.Clear();
            foreach (var s in list)
                ArchivedSessions.Add(s);
            StatusMessage = list.Count == 0 ? "暂无归档会话" : $"已归档 {list.Count} 个会话";
        }
        catch (Exception ex)
        {
            StatusMessage = "读取归档失败: " + ex.Message;
        }
    }

    /// <summary>把归档会话恢复到会话区。</summary>
    public async Task RestoreArchivedAsync(AgentSessionRow? session)
    {
        if (session is null)
            return;
        await _contextStore.RestoreSessionAsync(session.Id);
        ArchivedSessions.Remove(session);
        StatusMessage = $"已恢复到会话区: {session.Title}";
    }

    /// <summary>永久删除归档会话。</summary>
    public async Task DeleteArchivedAsync(AgentSessionRow? session)
    {
        if (session is null)
            return;
        await _contextStore.DeleteSessionAsync(session.Id);
        ArchivedSessions.Remove(session);
        StatusMessage = $"已删除归档会话: {session.Title}";
    }

    private AgentProviderEdit? _selectedProvider;
    public AgentProviderEdit? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (ReferenceEquals(_selectedProvider, value)) return;
            if (_selectedProvider is not null)
                _selectedProvider.PropertyChanged -= OnSelectedProviderPropertyChanged;
            Set(ref _selectedProvider, value);
            if (value is not null)
                value.PropertyChanged += OnSelectedProviderPropertyChanged;
            SyncPresetFromProvider();
        }
    }

    /// <summary>服务商预设列表（类型下拉）。</summary>
    public IReadOnlyList<ModelProviderPreset> ProviderPresets => ModelProviderPreset.All;

    private ModelProviderPreset _selectedPreset = ModelProviderPreset.Custom;

    /// <summary>当前选中的服务商预设；切换时自动覆盖填写 Endpoint 与模型列表。</summary>
    public ModelProviderPreset SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (value is null || ReferenceEquals(_selectedPreset, value)) return;
            _selectedPreset = value;
            OnPropertyChanged(nameof(SelectedPreset));
            OnPropertyChanged(nameof(SelectedPresetModelNames));
            ApplyPreset(value);
        }
    }

    /// <summary>当前预设的模型名候选（“添加模型”下拉可选项，仍可手动输入任意模型名）。</summary>
    public IReadOnlyList<string> SelectedPresetModelNames =>
        _selectedPreset.Models.Select(m => m.Name).ToArray();

    private string _newModelName = string.Empty;

    /// <summary>“添加模型”输入框内容。</summary>
    public string NewModelName { get => _newModelName; set => Set(ref _newModelName, value); }

    /// <summary>按已填 Endpoint 反向匹配预设（不覆盖任何字段）。</summary>
    private void SyncPresetFromProvider()
    {
        var preset = ModelProviderPreset.FindByEndpoint(_selectedProvider?.Endpoint);
        var changed = !ReferenceEquals(_selectedPreset, preset);
        _selectedPreset = preset;
        if (changed)
            OnPropertyChanged(nameof(SelectedPreset));
        OnPropertyChanged(nameof(SelectedPresetModelNames));
    }

    private void OnSelectedProviderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentProviderEdit.Endpoint))
            SyncPresetFromProvider();
    }

    /// <summary>应用预设：覆盖 Type/Endpoint，并用预设模型重建模型列表（自定义预设不动任何字段）。</summary>
    private void ApplyPreset(ModelProviderPreset preset)
    {
        if (preset.IsCustom || SelectedProvider is null)
            return;
        SelectedProvider.Type = preset.Type;
        SelectedProvider.Endpoint = preset.Endpoint;
        if (string.IsNullOrWhiteSpace(SelectedProvider.Name) || SelectedProvider.Name is "新模型" or "新服务商")
            SelectedProvider.Name = preset.Name;

        SelectedProvider.Models.Clear();
        foreach (var m in preset.Models)
            SelectedProvider.Models.Add(new ModelItemEdit { Name = m.Name, Enabled = true, SupportsVision = m.Vision });

        StatusMessage = $"已应用预设「{preset.Name}」：Endpoint 与 {preset.Models.Count} 个模型已自动填写，请输入 API Key，并按需勾选模型/视觉";
    }

    /// <summary>添加一个模型（可从预设候选选择，也可手动输入任意模型名）。</summary>
    public void AddModel()
    {
        if (SelectedProvider is null)
            return;
        var name = NewModelName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = "请输入或从下拉选择要添加的模型名";
            return;
        }
        if (SelectedProvider.Models.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"模型已存在: {name}";
            return;
        }

        var presetHit = _selectedPreset.Models.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        SelectedProvider.Models.Add(new ModelItemEdit
        {
            Name = name,
            Enabled = true,
            SupportsVision = presetHit?.Vision ?? SelectedProvider.SupportsVision
        });
        NewModelName = string.Empty;
        StatusMessage = $"已添加模型 {name}（点右下角「保存」生效）";
    }

    /// <summary>删除一个模型条目（调用方在行内“删除”按钮触发）。</summary>
    public void RemoveModel(ModelItemEdit? model)
    {
        if (model is null || SelectedProvider is null)
            return;
        SelectedProvider.Models.Remove(model);
    }

    private string _systemPrompt = string.Empty;
    public string SystemPrompt { get => _systemPrompt; set => Set(ref _systemPrompt, value); }

    private string _skillsDir = string.Empty;
    public string SkillsDir { get => _skillsDir; set => Set(ref _skillsDir, value); }

    private string _workspaceDir = string.Empty;
    public string WorkspaceDir { get => _workspaceDir; set => Set(ref _workspaceDir, value); }

    private string _mcpServerPath = string.Empty;
    public string McpServerPath { get => _mcpServerPath; set => Set(ref _mcpServerPath, value); }

    private string _contextLimit = "40";
    public string ContextLimit { get => _contextLimit; set => Set(ref _contextLimit, value); }

    private string _contextTokenLimit = "96000";
    public string ContextTokenLimit { get => _contextTokenLimit; set => Set(ref _contextTokenLimit, value); }

    private bool _autoSummarize = true;
    public bool AutoSummarize { get => _autoSummarize; set => Set(ref _autoSummarize, value); }

    private string _toolResultMaxChars = "4000";
    public string ToolResultMaxChars { get => _toolResultMaxChars; set => Set(ref _toolResultMaxChars, value); }

    private bool _compactToolDescriptions = true;
    public bool CompactToolDescriptions { get => _compactToolDescriptions; set => Set(ref _compactToolDescriptions, value); }

    private bool _spillLargeToolResults = true;
    public bool SpillLargeToolResults { get => _spillLargeToolResults; set => Set(ref _spillLargeToolResults, value); }

    private string _spillDir = string.Empty;
    public string SpillDir { get => _spillDir; set => Set(ref _spillDir, value); }

    private string _spillRetentionDays = "7";
    public string SpillRetentionDays { get => _spillRetentionDays; set => Set(ref _spillRetentionDays, value); }

    private bool _enableSubAgent = true;
    public bool EnableSubAgent { get => _enableSubAgent; set => Set(ref _enableSubAgent, value); }

    /// <summary>回答风格选项。</summary>
    public IReadOnlyList<string> ResponseStyles { get; } = new[] { "concise", "standard", "detailed" };

    private string _responseStyle = "concise";
    public string ResponseStyle { get => _responseStyle; set => Set(ref _responseStyle, value); }

    private string _maxToolIterations = "20";
    public string MaxToolIterations { get => _maxToolIterations; set => Set(ref _maxToolIterations, value); }

    private bool _readOnly;
    public bool ReadOnly { get => _readOnly; set => Set(ref _readOnly, value); }

    /// <summary>允许的工具分组勾选项（全勾=不限制）。</summary>
    public ObservableCollection<GroupOption> ToolGroupOptions { get; } = new();

    private bool _memoryEnabled;
    public bool MemoryEnabled { get => _memoryEnabled; set => Set(ref _memoryEnabled, value); }

    private string _memoryEndpoint = string.Empty;
    public string MemoryEndpoint { get => _memoryEndpoint; set => Set(ref _memoryEndpoint, value); }

    private string _memoryModel = string.Empty;
    public string MemoryModel { get => _memoryModel; set => Set(ref _memoryModel, value); }

    private string _memoryApiKey = string.Empty;
    public string MemoryApiKey { get => _memoryApiKey; set => Set(ref _memoryApiKey, value); }

    private string _memoryTopK = "5";
    public string MemoryTopK { get => _memoryTopK; set => Set(ref _memoryTopK, value); }

    private string _retentionMaxSessions = "50";
    public string RetentionMaxSessions { get => _retentionMaxSessions; set => Set(ref _retentionMaxSessions, value); }

    private string _retentionMaxMessages = "200";
    public string RetentionMaxMessages { get => _retentionMaxMessages; set => Set(ref _retentionMaxMessages, value); }

    private string _retentionDays = "90";
    public string RetentionDays { get => _retentionDays; set => Set(ref _retentionDays, value); }

    private string _statusMessage = string.Empty;
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    /// <summary>当前选用的模型 Id（厂家下具体模型；本窗口只读保留，删除模型后由主窗口按需回退）。</summary>
    private string _activeProviderId = string.Empty;

    public RelayCommand AddProviderCommand => _addProviderCommand ??= new RelayCommand(_ =>
    {
        var p = new AgentProviderEdit { Name = "新服务商", Type = "openai", Enabled = true };
        Providers.Add(p);
        SelectedProvider = p;
    });
    private RelayCommand? _addProviderCommand;

    public void RemoveSelectedProvider()
    {
        if (SelectedProvider is null) return;
        var idx = Providers.IndexOf(SelectedProvider);
        Providers.Remove(SelectedProvider);
        SelectedProvider = Providers.Count > 0 ? Providers[Math.Max(0, Math.Min(idx, Providers.Count - 1))] : null;
    }

    /// <summary>删除所选模型并**立即保存生效**（调用方负责弹窗确认）。</summary>
    public async Task RemoveSelectedProviderAsync()
    {
        if (SelectedProvider is null)
            return;
        var name = SelectedProvider.Name;
        RemoveSelectedProvider();
        StatusMessage = $"已删除 {name}";
        await SaveAsync();
    }

    public void AddProvider() => (AddProviderCommand).Execute(null);

    /// <summary>切换所选模型的启用/停用（右键菜单），**立即保存生效**。</summary>
    public async Task ToggleSelectedEnabledAsync()
    {
        if (SelectedProvider is null)
            return;
        SelectedProvider.Enabled = !SelectedProvider.Enabled;
        StatusMessage = SelectedProvider.Enabled ? $"已启用 {SelectedProvider.Name}" : $"已停用 {SelectedProvider.Name}";
        await SaveAsync();
    }

    /// <summary>测试所选模型连通性（右键菜单），结果写入状态栏。</summary>
    public async Task TestSelectedProviderAsync()
    {
        if (SelectedProvider is null)
            return;

        StatusMessage = $"测试连接中… ({SelectedProvider.Name})";
        try
        {
            var flat = AgentProviderGroupConfig.Flatten(new[] { SelectedProvider.ToModel() }).FirstOrDefault();
            if (flat is null)
            {
                StatusMessage = "无法测试：请填写 Endpoint 并在模型列表中至少启用一个模型";
                return;
            }
            var client = ChatClientFactory.Create(flat);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await client.GetResponseAsync(
                new[] { new ChatMessage(ChatRole.User, "ping") },
                new ChatOptions { MaxOutputTokens = 8, Temperature = 0 },
                CancellationToken.None);
            sw.Stop();
            StatusMessage = $"连接正常 · {SelectedProvider.Name} / {flat.Model}（{sw.ElapsedMilliseconds} ms）";
        }
        catch (Exception ex)
        {
            StatusMessage = "连接失败: " + ChatErrorClassifier.Describe(ex);
        }
    }

    public async void Load()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var agent = config.Agent ?? new AgentConfig();
            Providers.Clear();
            foreach (var p in agent.Providers)
                Providers.Add(AgentProviderEdit.From(p));
            _activeProviderId = agent.ActiveProviderId ?? string.Empty;
            SelectedProvider = Providers.FirstOrDefault();

            SystemPrompt = agent.SystemPrompt;
            SkillsDir = agent.SkillsDir;
            WorkspaceDir = agent.WorkspaceDir;
            McpServerPath = agent.McpServerPath;
            ContextLimit = agent.ContextLimit.ToString();
            ContextTokenLimit = agent.ContextTokenLimit.ToString();
            AutoSummarize = agent.AutoSummarize;
            ToolResultMaxChars = agent.ToolResultMaxChars.ToString();
            CompactToolDescriptions = agent.CompactToolDescriptions;
            SpillLargeToolResults = agent.SpillLargeToolResults;
            SpillDir = agent.SpillDir;
            SpillRetentionDays = agent.SpillRetentionDays.ToString();
            EnableSubAgent = agent.EnableSubAgent;
            ResponseStyle = string.IsNullOrWhiteSpace(agent.ResponseStyle) ? "concise" : agent.ResponseStyle;
            MaxToolIterations = agent.MaxToolIterations.ToString();
            ReadOnly = agent.ReadOnly;

            ToolGroupOptions.Clear();
            var enabledGroups = new HashSet<string>(agent.AllowedToolGroups ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var group in ToolGroups.All)
                ToolGroupOptions.Add(new GroupOption(group, GroupLabel(group), enabledGroups.Count == 0 || enabledGroups.Contains(group)));

            MemoryEnabled = agent.Memory?.Enabled ?? false;
            MemoryEndpoint = agent.Memory?.Endpoint ?? string.Empty;
            MemoryModel = agent.Memory?.Model ?? string.Empty;
            MemoryApiKey = agent.Memory?.ApiKey ?? string.Empty;
            MemoryTopK = (agent.Memory?.TopK ?? 5).ToString();

            RetentionMaxSessions = agent.RetentionMaxSessions.ToString();
            RetentionMaxMessages = agent.RetentionMaxMessages.ToString();
            RetentionDays = agent.RetentionDays.ToString();

            StatusMessage = "已加载";
        }
        catch (Exception ex)
        {
            StatusMessage = "加载失败: " + ex.Message;
        }
    }

    public async void Save() => await SaveAsync();

    public async Task SaveAsync()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            config.Agent ??= new AgentConfig();
            config.Agent.Providers = Providers.Select(p => p.ToModel()).ToArray();
            config.Agent.ActiveProviderId = _activeProviderId ?? string.Empty;
            config.Agent.SystemPrompt = SystemPrompt ?? string.Empty;
            config.Agent.SkillsDir = SkillsDir ?? string.Empty;
            config.Agent.WorkspaceDir = WorkspaceDir ?? string.Empty;
            config.Agent.McpServerPath = McpServerPath ?? string.Empty;
            config.Agent.ContextLimit = (int)Math.Max(0, ParseLong(ContextLimit, 40));
            config.Agent.ContextTokenLimit = (int)Math.Max(0, ParseLong(ContextTokenLimit, 96000));
            config.Agent.AutoSummarize = AutoSummarize;
            config.Agent.ToolResultMaxChars = (int)Math.Max(0, ParseLong(ToolResultMaxChars, 4000));
            config.Agent.CompactToolDescriptions = CompactToolDescriptions;
            config.Agent.SpillLargeToolResults = SpillLargeToolResults;
            config.Agent.SpillDir = SpillDir ?? string.Empty;
            config.Agent.SpillRetentionDays = (int)Math.Max(0, ParseLong(SpillRetentionDays, 7));
            config.Agent.EnableSubAgent = EnableSubAgent;
            config.Agent.ResponseStyle = string.IsNullOrWhiteSpace(ResponseStyle) ? "concise" : ResponseStyle;
            config.Agent.MaxToolIterations = (int)Math.Max(1, ParseLong(MaxToolIterations, 20));
            config.Agent.ReadOnly = ReadOnly;

            var checkedGroups = ToolGroupOptions.Where(o => o.IsChecked).Select(o => o.Key).ToArray();
            config.Agent.AllowedToolGroups = checkedGroups.Length == ToolGroups.All.Length ? Array.Empty<string>() : checkedGroups;

            config.Agent.Memory ??= new AgentMemoryConfig();
            config.Agent.Memory.Enabled = MemoryEnabled;
            config.Agent.Memory.Endpoint = MemoryEndpoint ?? string.Empty;
            config.Agent.Memory.Model = MemoryModel ?? string.Empty;
            config.Agent.Memory.ApiKey = MemoryApiKey;
            config.Agent.Memory.TopK = (int)Math.Max(1, ParseLong(MemoryTopK, 5));

            config.Agent.RetentionMaxSessions = (int)Math.Max(0, ParseLong(RetentionMaxSessions, 50));
            config.Agent.RetentionMaxMessages = (int)Math.Max(0, ParseLong(RetentionMaxMessages, 200));
            config.Agent.RetentionDays = (int)Math.Max(0, ParseLong(RetentionDays, 90));

            await _configService.SaveConfigAsync(config);
            StatusMessage = "已保存（重新打开「AI 运维助手」生效）";
        }
        catch (Exception ex)
        {
            StatusMessage = "保存失败: " + ex.Message;
        }
    }

    private static long ParseLong(string? text, long fallback) =>
        long.TryParse(text?.Trim(), out var v) && v >= 0 ? v : fallback;

    private static string GroupLabel(string key) => key switch
    {
        "ssh" => "SSH服务器/快照",
        "command" => "命令执行/提权",
        "fileTransfer" => "文件传输",
        "datasource" => "数据源",
        "mysql" => "MySQL",
        "postgres" => "PostgreSQL",
        "redis" => "Redis",
        "docker" => "Docker容器",
        "service" => "systemd服务",
        "log" => "日志文件",
        "java" => "JVM",
        "topology" => "拓扑",
        "app" => "应用体检",
        "guide" => "指南/自检",
        _ => key
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>设置界面里可编辑的大模型厂家配置（endpoint/key 共享，下挂多个模型）。</summary>
public class AgentProviderEdit : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    private string _name = string.Empty;
    public string Name { get => _name; set => Set(ref _name, value); }

    private string _type = "openai";
    public string Type { get => _type; set => Set(ref _type, value); }

    private string _endpoint = string.Empty;
    public string Endpoint { get => _endpoint; set => Set(ref _endpoint, value); }

    /// <summary>该厂家下的模型列表（勾选启用 + 逐模型视觉）。</summary>
    public ObservableCollection<ModelItemEdit> Models { get; } = new();

    private string _apiKey = string.Empty;
    public string ApiKey { get => _apiKey; set => Set(ref _apiKey, value); }

    private bool _enabled = true;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    private string _temperature = "0.3";
    public string Temperature { get => _temperature; set => Set(ref _temperature, value); }

    private string _maxTokens = "0";
    public string MaxTokens { get => _maxTokens; set => Set(ref _maxTokens, value); }

    private string _timeoutSeconds = "120";
    public string TimeoutSeconds { get => _timeoutSeconds; set => Set(ref _timeoutSeconds, value); }

    private string _maxRetries = "1";
    public string MaxRetries { get => _maxRetries; set => Set(ref _maxRetries, value); }

    private string _maxConcurrency = "3";
    public string MaxConcurrency { get => _maxConcurrency; set => Set(ref _maxConcurrency, value); }

    private bool _supportsVision;
    public bool SupportsVision { get => _supportsVision; set => Set(ref _supportsVision, value); }

    public static AgentProviderEdit From(AgentProviderGroupConfig p)
    {
        var edit = new AgentProviderEdit
        {
            Id = p.Id,
            Name = p.Name,
            Type = p.Type,
            Endpoint = p.Endpoint,
            ApiKey = p.ApiKey ?? string.Empty,
            Enabled = p.Enabled,
            Temperature = p.Temperature.ToString("0.##"),
            MaxTokens = p.MaxTokens.ToString(),
            TimeoutSeconds = p.TimeoutSeconds.ToString(),
            MaxRetries = p.MaxRetries.ToString(),
            MaxConcurrency = p.MaxConcurrency.ToString(),
            SupportsVision = p.SupportsVision
        };
        foreach (var m in p.Models ?? Array.Empty<AgentModelConfig>())
        {
            if (m is not null)
                edit.Models.Add(ModelItemEdit.From(m));
        }
        return edit;
    }

    public AgentProviderGroupConfig ToModel() => new()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
        Name = Name ?? string.Empty,
        Type = string.IsNullOrWhiteSpace(Type) ? "openai" : Type.Trim(),
        Endpoint = Endpoint ?? string.Empty,
        ApiKey = ApiKey,
        Enabled = Enabled,
        Temperature = double.TryParse(Temperature, out var t) ? t : 0.3,
        MaxTokens = int.TryParse(MaxTokens, out var m) && m > 0 ? m : 0,
        TimeoutSeconds = int.TryParse(TimeoutSeconds, out var to) && to > 0 ? to : 120,
        MaxRetries = int.TryParse(MaxRetries, out var mr) && mr >= 0 ? mr : 1,
        MaxConcurrency = int.TryParse(MaxConcurrency, out var mc) && mc > 0 ? mc : 3,
        SupportsVision = SupportsVision,
        Models = Models.Where(x => !string.IsNullOrWhiteSpace(x.Name)).Select(x => x.ToModel()).ToArray()
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>设置界面里可编辑的单个模型条目（模型名 + 启用 + 视觉）。</summary>
public class ModelItemEdit : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    private string _name = string.Empty;
    public string Name { get => _name; set => Set(ref _name, value); }

    private bool _enabled = true;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    private bool _supportsVision;
    public bool SupportsVision { get => _supportsVision; set => Set(ref _supportsVision, value); }

    public static ModelItemEdit From(AgentModelConfig m) => new()
    {
        Id = m.Id,
        Name = m.Name,
        Enabled = m.Enabled,
        SupportsVision = m.SupportsVision
    };

    public AgentModelConfig ToModel() => new()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
        Name = Name?.Trim() ?? string.Empty,
        Enabled = Enabled,
        SupportsVision = SupportsVision
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>工具分组勾选项。</summary>
public class GroupOption : INotifyPropertyChanged
{
    public GroupOption(string key, string label, bool isChecked)
    {
        Key = key;
        Label = label;
        _isChecked = isChecked;
    }

    public string Key { get; }
    public string Label { get; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
