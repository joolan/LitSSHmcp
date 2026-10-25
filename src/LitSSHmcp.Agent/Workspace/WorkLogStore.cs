using System.Globalization;
using System.Text;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Agent;

/// <summary>一次对话轮次的工作日志记录（由 AgentSession 在工具循环中填充）。</summary>
public sealed class WorkLogTurn
{
    public WorkLogTurn(string? sessionId, string userText, DateTime startedAt)
    {
        SessionId = sessionId;
        UserText = userText ?? string.Empty;
        StartedAt = startedAt;
    }

    public string? SessionId { get; }
    public string UserText { get; }
    public DateTime StartedAt { get; }
    public List<WorkLogToolCall> Calls { get; } = new();
    public string? AssistantText { get; set; }
    public bool Succeeded { get; set; }
    public bool Cancelled { get; set; }
    public string? Error { get; set; }

    public void RecordToolCall(string name, IDictionary<string, object?>? arguments, bool ok,
        bool readOnly, bool destructive, long durationMs, string? resultSummary)
        => Calls.Add(new WorkLogToolCall(name, arguments, ok, readOnly, destructive, durationMs, resultSummary));
}

public sealed record WorkLogToolCall(string Name, IDictionary<string, object?>? Arguments, bool Ok,
    bool ReadOnly, bool Destructive, long DurationMs, string? ResultSummary);

/// <summary>
/// 桌面端工作日志写入器：按天追加 Markdown 到 <c>&lt;工作区&gt;/worklogs/YYYY/YYYY-MM-DD.md</c>。
/// 由宿主在运行时装配（模型不能改路径）；写前统一脱敏，跨进程追加用文件锁串行化。
/// Web 端暂不启用（AgentWebRuntimeFactory 显式关闭）。
/// </summary>
public sealed class WorkLogStore
{
    public const string DirName = "worklogs";
    private const int MaxRequestChars = 500;
    private const int MaxResultChars = 400;
    private const int MaxErrorChars = 400;
    private const int MaxActionChars = 300;
    private const int MaxNoteChars = 2000;

    private readonly string _root;
    private readonly string? _ownerSegment;
    private readonly bool _includeFailures;
    private readonly bool _includeReadOnly;

    public bool ModelSummary { get; }
    public string RootDir => _ownerSegment is null ? _root : Path.Combine(_root, _ownerSegment);

    private WorkLogStore(string root, string? ownerSegment, AgentWorkLogConfig config)
    {
        _root = root;
        _ownerSegment = ownerSegment;
        _includeFailures = config.IncludeFailures;
        _includeReadOnly = config.IncludeReadOnly;
        ModelSummary = config.ModelSummary;
    }

    /// <summary>创建写入器；未启用或目录不可用时返回 null（不影响会话启动）。</summary>
    public static WorkLogStore? TryCreate(AgentWorkLogConfig? config, string workspaceRoot,
        string? ownerUserId, bool scopeByOwner)
    {
        if (config?.Enabled != true)
            return null;

        try
        {
            var root = string.IsNullOrWhiteSpace(config.Dir)
                ? Path.Combine(Path.GetFullPath(workspaceRoot), DirName)
                : Path.IsPathRooted(config.Dir.Trim())
                    ? Path.GetFullPath(config.Dir.Trim())
                    : Path.GetFullPath(Path.Combine(Path.GetFullPath(workspaceRoot), config.Dir.Trim()));
            var owner = scopeByOwner ? WorkspaceLayout.SanitizeSegment(ownerUserId, "local") : null;
            return new WorkLogStore(root, owner, config);
        }
        catch
        {
            return null;
        }
    }

    public string GetDailyPath(DateTime localTime)
    {
        var dir = Path.Combine(RootDir, localTime.ToString("yyyy", CultureInfo.InvariantCulture));
        return Path.Combine(dir, localTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md");
    }

    /// <summary>开始记录一轮（会话归因取当前运行作用域，缺省=本机会话）。</summary>
    public WorkLogTurn BeginTurn(string userText) =>
        new(LocalAudit.AmbientSessionId, userText, DateTime.Now);

    /// <summary>
    /// 追加一轮记录。仅当本轮包含「变更类操作」或显式开启只读记录时才落盘；
    /// 未成功的轮次受 <see cref="AgentWorkLogConfig.IncludeFailures"/> 控制。
    /// 返回写入的日志文件路径（未写入返回 null）。
    /// </summary>
    public async Task<string?> AppendTurnAsync(WorkLogTurn turn, CancellationToken ct = default)
    {
        try
        {
            var ops = turn.Calls.Where(c => !IsWorkLogTool(c.Name)).ToList();
            var mutating = ops.Where(c => !c.ReadOnly || c.Destructive).ToList();
            if (mutating.Count == 0 && !(_includeReadOnly && ops.Count > 0))
                return null;
            if (!turn.Succeeded && !_includeFailures)
                return null;

            var recorded = _includeFailures ? ops : ops.Where(c => c.Ok).ToList();
            if (recorded.Count == 0)
                return null;
            if (!_includeReadOnly && !recorded.Any(c => !c.ReadOnly || c.Destructive))
                return null;

            var now = DateTime.Now;
            var hasFailure = recorded.Any(c => !c.Ok);
            var status = !turn.Succeeded
                ? (turn.Cancelled ? "中断" : "失败")
                : hasFailure ? "成功(含失败重试)" : (recorded.Any(c => !c.ReadOnly || c.Destructive) ? "成功" : "调查");

            var sb = new StringBuilder();
            sb.AppendLine($"## {now:HH:mm:ss} · {SessionLabel(turn.SessionId)} · {status}");

            var request = OneLine(Clip(turn.UserText, MaxRequestChars));
            if (request.Length > 0)
                sb.AppendLine($"- 请求: {request}");

            sb.AppendLine("- 操作:");
            foreach (var op in recorded)
            {
                var hint = BuildOperationHint(op);
                sb.AppendLine($"  - [{(op.Ok ? "成功" : "失败")}] `{op.Name}`{hint}{DurationSuffix(op.DurationMs)}");
                if (!op.Ok && !string.IsNullOrWhiteSpace(op.ResultSummary))
                {
                    var failure = OneLine(Clip(op.ResultSummary, 240));
                    if (failure.Length > 0)
                        sb.AppendLine($"    - {failure}");
                }
            }

            var result = OneLine(Clip(turn.AssistantText, MaxResultChars));
            if (result.Length > 0)
                sb.AppendLine($"- 结果: {result}");

            var error = OneLine(Clip(turn.Error, MaxErrorChars));
            if (error.Length > 0)
                sb.AppendLine($"- 错误: {error}");

            sb.AppendLine($"- 会话: {SessionLabel(turn.SessionId)}");
            sb.AppendLine();

            var path = GetDailyPath(now);
            await AppendAsync(path, SecretRedactor.Redact(sb.ToString()), ct);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把模型提供的任务复盘追加到当天日志（由 ops_worklog_write 调用）。</summary>
    public async Task<string> AppendNoteAsync(string? content, CancellationToken ct = default)
    {
        var text = content?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "错误: content 不能为空";
        if (text.Length > MaxNoteChars)
            text = text[..MaxNoteChars];

        try
        {
            var now = DateTime.Now;
            var sb = new StringBuilder();
            sb.AppendLine($"### 复盘 · {now:HH:mm:ss} · {SessionLabel(LocalAudit.AmbientSessionId)}");
            sb.AppendLine(SecretRedactor.Redact(text));
            sb.AppendLine();
            var path = GetDailyPath(now);
            await AppendAsync(path, sb.ToString(), ct);
            return $"已追加今日工作日志复盘（{path}）";
        }
        catch (Exception ex)
        {
            return "工作日志写入失败: " + ex.Message;
        }
    }

    /// <summary>
    /// 上下文压缩点：仅当被丢弃的旧轮次包含变更类工具时追加一条索引，避免压缩后无法追溯。
    /// </summary>
    public async Task AppendCompactionAsync(string? sessionId, int droppedTurns, int droppedMessages,
        IReadOnlyList<string> userTexts, IReadOnlyList<string> mutatingTools, CancellationToken ct = default)
    {
        if (mutatingTools.Count == 0)
            return;

        try
        {
            var now = DateTime.Now;
            var sb = new StringBuilder();
            sb.AppendLine($"## {now:HH:mm:ss} · {SessionLabel(sessionId)} · 上下文压缩");
            sb.AppendLine($"- 已压缩较早对话: {droppedTurns} 轮 / {droppedMessages} 条消息");
            sb.AppendLine($"- 涉及变更操作: {string.Join(", ", mutatingTools.Distinct().Take(8))}");
            var last = userTexts.LastOrDefault(t => !string.IsNullOrWhiteSpace(t));
            if (!string.IsNullOrWhiteSpace(last))
                sb.AppendLine($"- 最近请求: {OneLine(Clip(last, MaxRequestChars))}");
            sb.AppendLine();

            await AppendAsync(GetDailyPath(now), SecretRedactor.Redact(sb.ToString()), ct);
        }
        catch
        {
            // 日志失败不影响上下文压缩
        }
    }

    private async Task AppendAsync(string path, string entry, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(entry))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lockPath = Path.Combine(Path.GetTempPath(), "LitSSH", "worklog.lock");
        using var _ = await CrossProcessFileLock.AcquireAsync(lockPath, TimeSpan.FromSeconds(5), ct);
        var header = $"# {DateTime.Now:yyyy-MM-dd} 工作日志{Environment.NewLine}{Environment.NewLine}";
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            await File.WriteAllTextAsync(path, header + entry, new UTF8Encoding(false), ct);
        else
            await File.AppendAllTextAsync(path, entry, new UTF8Encoding(false), ct);
    }

    private static bool IsWorkLogTool(string name) =>
        string.Equals(name, WorkLogTools.WriteToolName, StringComparison.Ordinal);

    private static string SessionLabel(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? "本机会话" : "会话 " + sessionId;

    private static string BuildOperationHint(WorkLogToolCall op)
    {
        var parts = new List<string>();
        var target = TargetOf(op.Arguments);
        if (target is not null)
            parts.Add(target);
        var action = ActionOf(op.Arguments);
        if (action is not null)
            parts.Add(action);
        return parts.Count == 0 ? string.Empty : " · " + string.Join(" · ", parts);
    }

    private static string? TargetOf(IDictionary<string, object?>? args)
    {
        if (args is null)
            return null;

        var values = new List<string>();
        void Add(string key, string prefix)
        {
            var value = ArgumentReader.ReadString(args, key);
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(prefix + value.Trim());
        }

        Add("serverId", "ssh:");
        Add("serverName", "ssh:");
        Add("host", "ssh:");
        Add("datasourceId", "ds:");
        Add("appId", "app:");
        Add("container", "container:");
        Add("service", "service:");
        Add("pid", "pid:");
        if (values.Count == 0)
            Add("path", "path:");

        return values.Count == 0
            ? null
            : string.Join(", ", values.Distinct(StringComparer.OrdinalIgnoreCase).Take(3));
    }

    private static string? ActionOf(IDictionary<string, object?>? args)
    {
        if (args is null)
            return null;

        var action = ArgumentReader.ReadString(args, "command")
                     ?? ArgumentReader.ReadString(args, "sql")
                     ?? ArgumentReader.ReadString(args, "action")
                     ?? ArgumentReader.ReadString(args, "query")
                     ?? ArgumentReader.ReadString(args, "remotePath")
                     ?? ArgumentReader.ReadString(args, "source")
                     ?? ArgumentReader.ReadString(args, "target")
                     ?? ArgumentReader.ReadString(args, "path")
                     ?? ArgumentReader.ReadString(args, "mode");
        if (string.IsNullOrWhiteSpace(action))
            return null;

        return OneLine(Clip(action, MaxActionChars));
    }

    private static string DurationSuffix(long durationMs) =>
        durationMs <= 0 ? string.Empty : $" ({durationMs / 1000.0:0.#}s)";

    private static string Clip(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var normalized = text.Trim();
        return normalized.Length <= max ? normalized : normalized[..max] + "…";
    }

    private static string OneLine(string text) =>
        text.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Trim();
}
