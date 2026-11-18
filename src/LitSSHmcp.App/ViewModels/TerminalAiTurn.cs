using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;

namespace LitSSHmcp.App.ViewModels;

public sealed class TerminalAiStep : INotifyPropertyChanged
{
    public TerminalAiStep(string tool, string? argsJson, bool destructive)
    {
        Tool = tool;
        ArgsJson = argsJson ?? string.Empty;
        CommandText = ExtractCommand(ArgsJson);
        Destructive = destructive;
    }

    public string Tool { get; }
    public string ArgsJson { get; }
    public string CommandText { get; }
    public bool Destructive { get; }

    private static string ExtractCommand(string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
            return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("command", out var command) &&
                command.ValueKind == JsonValueKind.String)
                return command.GetString() ?? argsJson;
        }
        catch
        {
            // 非 JSON 参数直接原样显示。
        }
        return argsJson;
    }

    private bool _isRunning = true;
    public bool IsRunning
    {
        get => _isRunning;
        private set { if (_isRunning == value) return; _isRunning = value; Raise(); Raise(nameof(Summary)); }
    }

    private bool _success = true;
    public bool Success
    {
        get => _success;
        private set { if (_success == value) return; _success = value; Raise(); Raise(nameof(Summary)); }
    }

    private string _result = string.Empty;
    public string Result
    {
        get => _result;
        private set { if (_result == value) return; _result = value; Raise(); Raise(nameof(ResultVisibility)); }
    }

    private long _durationMs;
    public long DurationMs
    {
        get => _durationMs;
        private set { if (_durationMs == value) return; _durationMs = value; Raise(); Raise(nameof(Summary)); }
    }

    private bool _manualRequired;
    public bool ManualRequired
    {
        get => _manualRequired;
        private set { if (_manualRequired == value) return; _manualRequired = value; Raise(); Raise(nameof(Summary)); }
    }

    public void MarkManualRequired() => ManualRequired = true;

    private bool _injected;
    public bool Injected
    {
        get => _injected;
        private set { if (_injected == value) return; _injected = value; Raise(); Raise(nameof(Summary)); }
    }

    public void MarkInjected() => Injected = true;

    public Visibility ResultVisibility => _result.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string Summary
    {
        get
        {
            if (_isRunning)
                return "执行中…";
            if (_injected)
                return "已填入终端";
            if (_manualRequired)
                return "需手动执行";
            var text = (_success ? "完成" : "失败") + (_durationMs > 0 ? $" · {_durationMs} ms" : string.Empty);
            return Destructive && Tool.Contains("sudo", StringComparison.OrdinalIgnoreCase)
                ? text + " · 提权"
                : text;
        }
    }

    public void Complete(bool success, string? result, long durationMs)
    {
        Success = success;
        Result = result ?? string.Empty;
        DurationMs = durationMs;
        IsRunning = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class TerminalAiTurn : INotifyPropertyChanged
{
    public TerminalAiTurn(string userText)
    {
        UserText = userText;
        UserTime = DateTime.Now.ToString("HH:mm:ss");
        Steps.CollectionChanged += (_, _) =>
        {
            Raise(nameof(StepsVisibility));
            Raise(nameof(StepsHeader));
        };
    }

    public string UserText { get; }
    public string UserTime { get; }
    public ObservableCollection<TerminalAiStep> Steps { get; } = new();

    private string _assistantText = string.Empty;
    public string AssistantText
    {
        get => _assistantText;
        set { if (_assistantText == value) return; _assistantText = value; Raise(); Raise(nameof(AssistantVisibility)); }
    }

    private string _note = string.Empty;
    public string Note
    {
        get => _note;
        set { if (_note == value) return; _note = value; Raise(); Raise(nameof(NoteVisibility)); }
    }

    public Visibility NoteVisibility => _note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AssistantVisibility => _assistantText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StepsVisibility => Steps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string StepsHeader => Steps.Count == 0 ? "工具过程" : $"工具过程 · {Steps.Count} 个";

    private string _manualCommand = string.Empty;
    public string ManualCommand
    {
        get => _manualCommand;
        private set
        {
            if (_manualCommand == value) return;
            _manualCommand = value;
            Raise();
            Raise(nameof(ManualCommandVisibility));
        }
    }

    public Visibility ManualCommandVisibility =>
        _manualCommand.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public void SetManualCommand(string command)
    {
        ManualCommand = command;
        IsStepsExpanded = true;
    }

    private bool _isStepsExpanded = true;
    public bool IsStepsExpanded
    {
        get => _isStepsExpanded;
        set { if (_isStepsExpanded == value) return; _isStepsExpanded = value; Raise(); }
    }

    private bool _isRunning = true;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            Raise();
            Raise(nameof(RunningVisibility));
            Raise(nameof(Footer));
        }
    }

    public Visibility RunningVisibility => _isRunning ? Visibility.Visible : Visibility.Collapsed;

    private string _footer = "运行中…";
    public string Footer
    {
        get => _footer;
        private set { if (_footer == value) return; _footer = value; Raise(); }
    }

    public void MarkDone()
    {
        IsRunning = false;
        IsStepsExpanded = false;   // 完成后折叠过程，让最终结论保持在底部可见。
        Footer = DateTime.Now.ToString("HH:mm:ss");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
