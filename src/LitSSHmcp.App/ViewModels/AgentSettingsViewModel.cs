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
    public AgentProviderEdit? SelectedProvider { get => _selectedProvider; set => Set(ref _selectedProvider, value); }

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

    private string _contextTokenLimit = "24000";
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

    /// <summary>界面主题（全局）：system / light / dark；切换即时生效。</summary>
    public IReadOnlyList<string> Themes { get; } = new[] { "system", "light", "dark" };

    private string _theme = "system";
    public string Theme
    {
        get => _theme;
        set
        {
            if (string.Equals(_theme, value, StringComparison.Ordinal))
                return;
            Set(ref _theme, value);
            ThemeService.Apply(value, AccentToHex(Accent));
        }
    }

    /// <summary>强调色选项。</summary>
    public IReadOnlyList<string> Accents { get; } = new[] { "系统默认", "蓝色", "紫色", "绿色", "橙色", "红色" };

    private string _accent = "系统默认";
    public string Accent
    {
        get => _accent;
        set
        {
            if (string.Equals(_accent, value, StringComparison.Ordinal))
                return;
            Set(ref _accent, value);
            ThemeService.Apply(Theme, AccentToHex(value));
        }
    }

    private static string AccentToHex(string? label) => label switch
    {
        "蓝色" => "#0078D4",
        "紫色" => "#8B5CF6",
        "绿色" => "#10B981",
        "橙色" => "#F59E0B",
        "红色" => "#EF4444",
        _ => string.Empty
    };

    private static string HexToAccent(string? hex) => (hex ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "#0078D4" => "蓝色",
        "#8B5CF6" => "紫色",
        "#10B981" => "绿色",
        "#F59E0B" => "橙色",
        "#EF4444" => "红色",
        _ => "系统默认"
    };

    public RelayCommand AddProviderCommand => _addProviderCommand ??= new RelayCommand(_ =>
    {
        var p = new AgentProviderEdit { Name = "新模型", Type = "openai", Enabled = true };
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
            var client = ChatClientFactory.Create(SelectedProvider.ToModel());
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await client.GetResponseAsync(
                new[] { new ChatMessage(ChatRole.User, "ping") },
                new ChatOptions { MaxOutputTokens = 8, Temperature = 0 },
                CancellationToken.None);
            sw.Stop();
            StatusMessage = $"连接正常 · {SelectedProvider.Name}（{sw.ElapsedMilliseconds} ms）";
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
            SelectedProvider = Providers.FirstOrDefault(p => p.Id == agent.ActiveProviderId) ?? Providers.FirstOrDefault();

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

            Theme = string.IsNullOrWhiteSpace(config.Ui?.Theme) ? "system" : config.Ui!.Theme;
            Accent = HexToAccent(config.Ui?.Accent);

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
            config.Agent.ActiveProviderId = SelectedProvider?.Id ?? string.Empty;
            config.Agent.SystemPrompt = SystemPrompt ?? string.Empty;
            config.Agent.SkillsDir = SkillsDir ?? string.Empty;
            config.Agent.WorkspaceDir = WorkspaceDir ?? string.Empty;
            config.Agent.McpServerPath = McpServerPath ?? string.Empty;
            config.Agent.ContextLimit = (int)Math.Max(0, ParseLong(ContextLimit, 40));
            config.Agent.ContextTokenLimit = (int)Math.Max(0, ParseLong(ContextTokenLimit, 24000));
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

            config.Ui ??= new UiConfig();
            config.Ui.Theme = string.IsNullOrWhiteSpace(Theme) ? "system" : Theme;
            config.Ui.Accent = AccentToHex(Accent);

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
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>设置界面里可编辑的模型配置。</summary>
public class AgentProviderEdit : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    private string _name = string.Empty;
    public string Name { get => _name; set => Set(ref _name, value); }

    private string _type = "openai";
    public string Type { get => _type; set => Set(ref _type, value); }

    private string _endpoint = string.Empty;
    public string Endpoint { get => _endpoint; set => Set(ref _endpoint, value); }

    private string _model = string.Empty;
    public string Model { get => _model; set => Set(ref _model, value); }

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

    public static AgentProviderEdit From(AgentProviderConfig p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Type = p.Type,
        Endpoint = p.Endpoint,
        Model = p.Model,
        ApiKey = p.ApiKey ?? string.Empty,
        Enabled = p.Enabled,
        Temperature = p.Temperature.ToString("0.##"),
        MaxTokens = p.MaxTokens.ToString(),
        TimeoutSeconds = p.TimeoutSeconds.ToString(),
        MaxRetries = p.MaxRetries.ToString(),
        MaxConcurrency = p.MaxConcurrency.ToString(),
        SupportsVision = p.SupportsVision
    };

    public AgentProviderConfig ToModel() => new()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
        Name = Name ?? string.Empty,
        Type = string.IsNullOrWhiteSpace(Type) ? "openai" : Type.Trim(),
        Endpoint = Endpoint ?? string.Empty,
        Model = Model ?? string.Empty,
        ApiKey = ApiKey,
        Enabled = Enabled,
        Temperature = double.TryParse(Temperature, out var t) ? t : 0.3,
        MaxTokens = int.TryParse(MaxTokens, out var m) && m > 0 ? m : 0,
        TimeoutSeconds = int.TryParse(TimeoutSeconds, out var to) && to > 0 ? to : 120,
        MaxRetries = int.TryParse(MaxRetries, out var mr) && mr >= 0 ? mr : 1,
        MaxConcurrency = int.TryParse(MaxConcurrency, out var mc) && mc > 0 ? mc : 3,
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
