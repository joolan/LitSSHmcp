using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;

namespace LitSSHmcp.App.ViewModels;

public sealed class DatasourceAiStep : INotifyPropertyChanged
{
    public DatasourceAiStep(string tool, string? argsJson)
    {
        Tool = tool;
        ArgsJson = argsJson ?? string.Empty;
        CommandText = ExtractSql(ArgsJson);
    }

    public string Tool { get; }
    public string ArgsJson { get; }
    public string CommandText { get; }

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

    public Visibility ResultVisibility => _result.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Summary => IsRunning ? "运行中" : Success ? $"完成 · {DurationMs} ms" : $"失败 · {DurationMs} ms";

    private string _resultMeta = string.Empty;
    public string ResultMeta
    {
        get => _resultMeta;
        private set
        {
            if (_resultMeta == value) return;
            _resultMeta = value;
            Raise();
            Raise(nameof(ResultMetaVisibility));
        }
    }

    public Visibility ResultMetaVisibility =>
        _resultMeta.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public void SetResultMeta(int returned, int injected, bool totalKnown)
    {
        ResultMeta = $"取回 {returned:N0} · 展示 {injected:N0} · 总数{(totalKnown ? "已知" : "未知")}";
    }

    public void Complete(bool success, string? result, long durationMs)
    {
        Success = success;
        Result = result ?? string.Empty;
        DurationMs = durationMs;
        IsRunning = false;
    }

    private static string ExtractSql(string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
            return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("sql", out var sql) &&
                sql.ValueKind == JsonValueKind.String)
                return sql.GetString() ?? string.Empty;
        }
        catch
        {
            // 非 JSON 时原样展示
        }
        return argsJson;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DatasourceAiTurn : INotifyPropertyChanged
{
    public DatasourceAiTurn(string userText)
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
    public ObservableCollection<DatasourceAiStep> Steps { get; } = new();

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

    private string _resultMeta = string.Empty;
    public string ResultMeta
    {
        get => _resultMeta;
        set
        {
            if (_resultMeta == value) return;
            _resultMeta = value;
            Raise();
            Raise(nameof(ResultMetaVisibility));
        }
    }

    public Visibility ResultMetaVisibility =>
        _resultMeta.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StepsVisibility => Steps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string StepsHeader => Steps.Count == 0 ? "工具记录" : $"工具记录 · {Steps.Count} 项";

    private bool _isStepsExpanded;
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

    public string Footer => _isRunning ? "正在分析…" : "完成";

    public void MarkDone()
    {
        IsRunning = false;
        Raise(nameof(Footer));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
