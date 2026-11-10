using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace LitSSHmcp.App.ViewModels;

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
        InitializeRelatedTargets();
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

    /// <summary>本轮是否未完成（错误/取消/截断）；失败轮次也要写入上下文，避免“继续”误接上一个成功轮。</summary>
    public bool Failed { get; internal set; }

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
        Failed = false;
        Steps.Clear();
        ClearRelatedTargets();
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
