using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace LitSSHmcp.App.ViewModels;

/// <summary>工具调用达到单轮上限时的内联续跑确认：挂在当前轮次上，由输入区卡片完成继续/停止。</summary>
public sealed class ToolLimitApprovalRequest : INotifyPropertyChanged
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _resolved;

    public ToolLimitApprovalRequest(int used, int limit)
    {
        Used = used;
        Limit = limit;
        ContinueCommand = new RelayCommand(_ => Resolve(true, "已继续，本轮工具次数重新累计"));
        StopCommand = new RelayCommand(_ => Resolve(false, "已停止本轮"));
    }

    public int Used { get; }
    public int Limit { get; }
    public string Title => $"工具调用达到上限（{Used}/{Limit} 次）";
    public string Message =>
        "可以继续执行剩余步骤：继续后本轮工具调用次数会从 0 重新累计；" +
        "如果是明显的死循环或不需要继续，请选择停止。";
    public Task<bool> DecisionTask => _completion.Task;
    public ICommand ContinueCommand { get; }
    public ICommand StopCommand { get; }

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

    public void Resolve(bool approved, string? resolutionText = null)
    {
        if (Interlocked.Exchange(ref _resolved, 1) != 0)
            return;
        ResolutionText = resolutionText ?? (approved ? "已继续" : "已停止");
        IsPending = false;
        _completion.TrySetResult(approved);
    }

    public void Cancel() => Resolve(false, "已取消");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
