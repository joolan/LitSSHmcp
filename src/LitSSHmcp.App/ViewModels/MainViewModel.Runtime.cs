using System.ComponentModel;
using LitSSHmcp.App.Views;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    public int ActiveSessionCount => Sessions.Count;
    public bool HasActiveSessions => ActiveSessionCount > 0;

    private int _pendingApprovalCount;
    public int PendingApprovalCount
    {
        get => _pendingApprovalCount;
        private set
        {
            if (_pendingApprovalCount == value) return;
            _pendingApprovalCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPendingApprovals));
        }
    }

    public bool HasPendingApprovals => PendingApprovalCount > 0;

    private AgentViewModel? _agentStatusVm;

    private void RaiseSessionCountsChanged()
    {
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ActiveSessionCount));
        OnPropertyChanged(nameof(HasActiveSessions));
    }

    private void AttachAgentStatus(AgentWindow window)
    {
        DetachAgentStatus();
        _agentStatusVm = window.ViewModel;
        _agentStatusVm.PropertyChanged += OnAgentStatusChanged;
        PendingApprovalCount = _agentStatusVm.PendingApprovalCount;
    }

    private void DetachAgentStatus()
    {
        if (_agentStatusVm is not null)
            _agentStatusVm.PropertyChanged -= OnAgentStatusChanged;
        _agentStatusVm = null;
        PendingApprovalCount = 0;
    }

    private void OnAgentStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentViewModel.PendingApprovalCount))
            PendingApprovalCount = _agentStatusVm?.PendingApprovalCount ?? 0;
    }

    private AgentWindow EnsureAgentWindow()
    {
        if (_agentWindow is { IsLoaded: true })
        {
            _agentWindow.Activate();
            return _agentWindow;
        }

        _agentWindow = _windows.CreateAgentWindow(
            _configService,
            _services.CreateAgentContextStore(),
            _services.BundledSkillsDir,
            ResolveSessionTerminal,
            AiTerminals);
        AttachAgentStatus(_agentWindow);
        _agentWindow.ViewModel.SessionRemoved += CloseSessionTerminal;
        _agentWindow.Closed += (_, _) =>
        {
            if (_agentWindow is not null)
                _agentWindow.ViewModel.SessionRemoved -= CloseSessionTerminal;
            DetachAgentStatus();
            _agentWindow = null;
        };
        _agentWindow.Show();
        return _agentWindow;
    }
}
