using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Views;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    private AgentView? _agentView;
    private AgentWindow? _agentWindow;
    private bool _agentDetached;
    private (double ScrollOffset, int CaretIndex) _agentViewState;

    /// <summary>嵌入主界面的 AI 助手视图；独立窗口打开时为 null，主页面显示占位卡。</summary>
    public AgentView? AgentPageView => _agentDetached ? null : _agentView;

    public bool IsAgentDetached => _agentDetached;

    public int AgentRunningCount => _agentView?.ViewModel.RunningSessionCount ?? 0;

    public bool HasAgentRunningTasks => AgentRunningCount > 0;

    public string AgentBadgeText => AgentRunningCount > 9 ? "9+" : AgentRunningCount.ToString();

    public bool ShowAgentBadge => HasAgentRunningTasks && CurrentPage != MainPage.Agent;

    public string AgentBadgeTooltip
    {
        get
        {
            var running = AgentRunningCount;
            var pending = _agentView?.ViewModel.PendingApprovalSessionCount ?? 0;
            if (pending > 0)
                return $"AI 运维助手：{running} 个任务运行中，{pending} 个等待审批";
            return $"AI 运维助手：{running} 个任务运行中";
        }
    }

    private RelayCommand? _detachAgentCommand;
    public ICommand DetachAgentCommand => _detachAgentCommand ??= new RelayCommand(
        _ => DetachAgent(), _ => !_agentDetached);

    private RelayCommand? _activateAgentCommand;
    public ICommand ActivateAgentCommand => _activateAgentCommand ??= new RelayCommand(
        _ => ActivateAgent(), _ => _agentDetached);

    private RelayCommand? _dockAgentCommand;
    public ICommand DockAgentCommand => _dockAgentCommand ??= new RelayCommand(
        _ => DockAgent(), _ => _agentDetached);

    private AgentView EnsureAgentPage()
    {
        if (_agentView is not null)
            return _agentView;

        var view = new AgentView(
            _configService,
            _services.CreateAgentContextStore(),
            _services.BundledSkillsDir,
            ResolveSessionTerminal,
            AiTerminals,
            _services);
        view.ViewModel.SessionRemoved += CloseSessionTerminal;
        view.ViewModel.PropertyChanged += OnAgentViewModelPropertyChanged;
        view.DetachRequested += OnAgentDetachRequested;
        view.RelatedTargetMenuRequested += OnRelatedTargetMenuRequested;
        _agentView = view;
        RaiseAgentPageStateChanged();
        RaiseAgentBadgeChanged();
        return view;
    }

    private void NavigateAgent()
    {
        EnsureAgentPage();
        CurrentPage = MainPage.Agent;
        if (_agentDetached)
            ActivateAgent();
    }

    private void OnAgentDetachRequested(object? sender, EventArgs e) => DetachAgent();

    private void DetachAgent()
    {
        var view = EnsureAgentPage();
        if (_agentDetached)
        {
            ActivateAgent();
            return;
        }

        _agentViewState = view.CaptureViewState();
        _agentDetached = true;
        RaiseAgentPageStateChanged();

        var window = _windows.CreateAgentWindow(view);
        _agentWindow = window;
        window.DockRequested += OnAgentDockRequested;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_agentWindow, window))
                _agentWindow = null;
        };
        window.Show();
        view.RestoreViewState(_agentViewState.ScrollOffset, _agentViewState.CaretIndex);
    }

    private void ActivateAgent()
    {
        if (_agentWindow is { IsLoaded: true })
        {
            _agentWindow.Activate();
            _agentWindow.Focus();
            return;
        }

        if (_agentDetached)
            DockAgent();
    }

    private void DockAgent()
    {
        if (!_agentDetached)
            return;

        var window = _agentWindow;
        if (window is { IsLoaded: true })
        {
            window.Close();
            return;
        }

        _agentDetached = false;
        _agentView?.SetDetached(false);
        RaiseAgentPageStateChanged();
        if (_agentView is not null)
            _agentView.RestoreViewState(_agentViewState.ScrollOffset, _agentViewState.CaretIndex);
    }

    private void OnAgentDockRequested(object? sender, EventArgs e)
    {
        if (!_agentDetached)
            return;

        _agentWindow = null;
        _agentDetached = false;
        _agentView?.SetDetached(false);
        RaiseAgentPageStateChanged();
        if (_agentView is not null)
            _agentView.RestoreViewState(_agentViewState.ScrollOffset, _agentViewState.CaretIndex);
    }

    private void OnAgentViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AgentViewModel.RunningSessionCount)
            or nameof(AgentViewModel.HasRunningSessions)
            or nameof(AgentViewModel.PendingApprovalSessionCount)
            or nameof(AgentViewModel.HasPendingApprovalSessions))
        {
            RaiseAgentBadgeChanged();
        }
    }

    private void RaiseAgentPageStateChanged()
    {
        OnPropertyChanged(nameof(AgentPageView));
        OnPropertyChanged(nameof(IsAgentDetached));
        OnPropertyChanged(nameof(ShowAgentBadge));
        (DetachAgentCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ActivateAgentCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DockAgentCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RaiseAgentBadgeChanged()
    {
        OnPropertyChanged(nameof(AgentRunningCount));
        OnPropertyChanged(nameof(HasAgentRunningTasks));
        OnPropertyChanged(nameof(AgentBadgeText));
        OnPropertyChanged(nameof(ShowAgentBadge));
        OnPropertyChanged(nameof(AgentBadgeTooltip));
    }

    /// <summary>主窗口真正退出时释放嵌入页面；最小化到托盘不会调用。</summary>
    public void DisposeAgentPage()
    {
        var window = _agentWindow;
        _agentWindow = null;
        if (window is not null)
        {
            window.DockRequested -= OnAgentDockRequested;
            try { window.Close(); } catch { /* ignore */ }
        }

        var view = _agentView;
        _agentView = null;
        _agentDetached = false;
        if (view is not null)
        {
            view.ViewModel.SessionRemoved -= CloseSessionTerminal;
            view.ViewModel.PropertyChanged -= OnAgentViewModelPropertyChanged;
            view.DetachRequested -= OnAgentDetachRequested;
            view.RelatedTargetMenuRequested -= OnRelatedTargetMenuRequested;
            AsyncOperation.Run("释放 AI 助手", () => view.DisposeAsync());
        }

        RaiseAgentPageStateChanged();
        RaiseAgentBadgeChanged();
    }
}
