using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using LitSSHmcp.App.Services.LocalTerminal;

namespace LitSSHmcp.App.ViewModels;

/// <summary>桌面端本机命令的内联审批请求：挂在当前轮次上，由对话内卡片完成允许/拒绝。</summary>
public sealed class LocalApprovalRequest : INotifyPropertyChanged
{
    private readonly TaskCompletionSource<LocalApprovalDecision> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _resolved;

    public LocalApprovalRequest(string command, string workingDirectory, string? sessionLabel, bool sessionAllGranted)
    {
        Command = command ?? string.Empty;
        WorkingDirectory = workingDirectory ?? string.Empty;
        SessionLabel = sessionLabel ?? string.Empty;
        SessionAllGranted = sessionAllGranted;

        AllowOnceCommand = new RelayCommand(_ => Resolve(LocalApprovalDecision.Allow, "已允许一次"));
        AllowRoundCommand = new RelayCommand(_ => Resolve(LocalApprovalDecision.AllowRound, "本轮次内已允许全部命令"));
        AllowSessionCommand = new RelayCommand(_ => Resolve(LocalApprovalDecision.AllowSession, "本次会话内已允许全部命令"));
        AlwaysAllowCommand = new RelayCommand(_ => Resolve(LocalApprovalDecision.AlwaysAllow, "已永久允许此命令"));
        DenyCommand = new RelayCommand(_ => Resolve(LocalApprovalDecision.Deny, "已拒绝"));
    }

    public string Command { get; }
    public string WorkingDirectory { get; }
    public string SessionLabel { get; }
    public bool SessionAllGranted { get; }
    public Task<LocalApprovalDecision> DecisionTask => _completion.Task;

    public ICommand AllowOnceCommand { get; }
    public ICommand AllowRoundCommand { get; }
    public ICommand AllowSessionCommand { get; }
    public ICommand AlwaysAllowCommand { get; }
    public ICommand DenyCommand { get; }

    private bool _isPending = true;
    public bool IsPending
    {
        get => _isPending;
        private set
        {
            if (_isPending == value) return;
            _isPending = value;
            Raise();
            Raise(nameof(PendingVisibility));
            Raise(nameof(ResolvedVisibility));
        }
    }

    private string _resolutionText = string.Empty;
    public string ResolutionText
    {
        get => _resolutionText;
        private set { _resolutionText = value; Raise(); }
    }

    public Visibility PendingVisibility => IsPending ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ResolvedVisibility => IsPending ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SessionTextVisibility => string.IsNullOrWhiteSpace(SessionLabel) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility WorkingDirectoryVisibility => string.IsNullOrWhiteSpace(WorkingDirectory) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SessionHintVisibility => SessionAllGranted ? Visibility.Visible : Visibility.Collapsed;

    public void Resolve(LocalApprovalDecision decision, string? resolutionText = null)
    {
        if (Interlocked.Exchange(ref _resolved, 1) != 0)
            return;

        ResolutionText = resolutionText ?? decision switch
        {
            LocalApprovalDecision.Allow => "已允许一次",
            LocalApprovalDecision.AllowRound => "本轮次内已允许全部命令",
            LocalApprovalDecision.AllowSession => "本次会话内已允许全部命令",
            LocalApprovalDecision.AlwaysAllow => "已永久允许此命令",
            _ => "已拒绝"
        };
        IsPending = false;
        _completion.TrySetResult(decision);
    }

    public void Cancel() => Resolve(LocalApprovalDecision.Deny, "已取消");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
