using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

public partial class AgentViewModel
{
    /// <summary>是否处于初始加载/切换会话的加载中状态。初始为 true，避免开窗瞬间空白。</summary>
    private bool _isLoading = true;
    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (!Set(ref _isLoading, value))
                return;
            (CancelLoadingCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private string _loadingText = "正在加载";
    public string LoadingText { get => _loadingText; set => Set(ref _loadingText, value); }

    private string _loadingDetail = string.Empty;
    /// <summary>加载阶段明细（读取配置 / 读取会话 / 连接 MCP 等）。</summary>
    public string LoadingDetail { get => _loadingDetail; set => Set(ref _loadingDetail, value); }

    /// <summary>系统是否允许动画（减少动画偏好）。</summary>
    public bool AnimationsEnabled => MotionPreferences.AnimationsEnabled;

    private CancellationTokenSource? _loadingCts;
    private RelayCommand? _cancelLoadingCommand;

    public ICommand CancelLoadingCommand =>
        _cancelLoadingCommand ??= new RelayCommand(_ => CancelLoading(), _ => IsLoading);

    private void CancelLoading()
    {
        _loadingCts?.Cancel();
        IsLoading = false;
        LoadingDetail = string.Empty;
        StatusMessage = "已取消加载";
    }

    /// <summary>开始一段可取消的加载：取消旧令牌并返回本次令牌。旧操作结束时不会误伤新操作。</summary>
    private (CancellationTokenSource Cts, CancellationToken Token) BeginLoading(string text, string? detail = null)
    {
        _loadingCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadingCts = cts;
        IsLoading = true;
        LoadingText = text;
        LoadingDetail = detail ?? string.Empty;
        return (cts, cts.Token);
    }

    /// <summary>更新当前加载阶段（不替换令牌，供初始化内部切换阶段）。</summary>
    private void SetLoadingStage(string text, string? detail = null)
    {
        IsLoading = true;
        LoadingText = text;
        LoadingDetail = detail ?? string.Empty;
    }

    private void EndLoading(CancellationTokenSource cts)
    {
        cts.Dispose();
        if (!ReferenceEquals(_loadingCts, cts))
            return;

        _loadingCts = null;
        IsLoading = false;
        LoadingDetail = string.Empty;
    }

    public async Task InitializeAsync()
    {
        var (cts, ct) = BeginLoading("正在初始化", "读取本地配置…");
        try
        {
            await _store.InitializeAsync();
            var config = await _configService.LoadConfigAsync();
            _fullConfig = config;
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

            SetLoadingStage("正在加载会话列表", "读取会话数据库…");
            await ReloadGroupsAsync();
            await ReloadSessionsAsync();
            _suppressSelection = false;
            var target = Sessions.FirstOrDefault();
            if (target is not null)
            {
                _suppressSelection = true;
                SelectedSession = target;
                _suppressSelection = false;
                SyncSidebarSelection();
                await LoadSessionCoreAsync(target.Id, ct);
            }
            else
            {
                StartNewTask();
                IsLoading = false;   // 先撤遮罩：MCP 连接慢/离线时不阻塞会话区
                await ResetRuntimeAsync(null, ct);
            }

            if (Providers.Count == 0)
                StatusMessage = "未配置大模型：请点「AI 助手设置」添加并启用一个 OpenAI 兼容模型。";
            StartAutoRefresh();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消加载";
        }
        catch (Exception ex)
        {
            StatusMessage = "初始化失败: " + ex.Message;
        }
        finally
        {
            EndLoading(cts);
        }
    }
}
