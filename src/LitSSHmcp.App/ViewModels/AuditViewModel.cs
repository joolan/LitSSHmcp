using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public class AuditViewModel : INotifyPropertyChanged
{
    private readonly AuditLogService _audit = new();
    private bool _initialized;
    private string _statusMessage = string.Empty;

    public ObservableCollection<CommandAuditLog> CommandLogs { get; } = new();
    public ObservableCollection<SqlAuditLog> SqlLogs { get; } = new();
    public ObservableCollection<AuditSession> Sessions { get; } = new();

    public string FilterText { get; set; } = string.Empty;
    public string Keyword { get; set; } = string.Empty;
    public string LimitText { get; set; } = "200";

    /// <summary>按 MCP 会话 ID 过滤（可选）。</summary>
    public string SessionFilter { get; set; } = string.Empty;

    /// <summary>是否包含超期已归档的历史记录（永久保留）。</summary>
    public bool IncludeHistory { get; set; }

    /// <summary>事件类型过滤：0=全部, 1=Exec, 2=Gate, 3=Probe, 4=Meta, 5=Transfer。</summary>
    public int CategoryIndex { get; set; }

    public string[] CategoryOptions { get; } = { "全部", "执行 (Exec)", "审批拦截 (Gate)", "只读探测 (Probe)", "列表元数据 (Meta)", "文件传输 (Transfer)" };

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public async void Load()
    {
        try
        {
            if (!_initialized)
            {
                await _audit.InitializeAsync();
                _initialized = true;
            }

            var filter = string.IsNullOrWhiteSpace(FilterText) ? null : FilterText.Trim();
            var keyword = string.IsNullOrWhiteSpace(Keyword) ? null : Keyword.Trim();
            var session = string.IsNullOrWhiteSpace(SessionFilter) ? null : SessionFilter.Trim();
            var limit = int.TryParse(LimitText, out var parsed) && parsed > 0 ? parsed : 200;

            var category = CategoryIndex switch
            {
                1 => AuditCategory.Exec,
                2 => AuditCategory.Gate,
                3 => AuditCategory.Probe,
                4 => AuditCategory.Meta,
                5 => AuditCategory.Transfer,
                _ => (AuditCategory?)null
            };

            var commands = await _audit.GetLogsAsync(filter, limit, keyword, IncludeHistory, sessionId: session, category: category);
            CommandLogs.Clear();
            foreach (var item in commands)
                CommandLogs.Add(item);

            var sqls = await _audit.GetSqlLogsAsync(filter, limit, keyword, IncludeHistory, sessionId: session, category: category);
            SqlLogs.Clear();
            foreach (var item in sqls)
                SqlLogs.Add(item);

            var sessions = await _audit.GetSessionsAsync(200);
            Sessions.Clear();
            foreach (var item in sessions)
                Sessions.Add(item);

            StatusMessage = $"命令审计 {commands.Length} 条, SQL审计 {sqls.Length} 条, 会话 {sessions.Length} 个" + (IncludeHistory ? "（含归档）" : "");
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败: {ex.Message}";
        }
    }

    public async void VerifyIntegrity()
    {
        try
        {
            if (!_initialized)
            {
                await _audit.InitializeAsync();
                _initialized = true;
            }

            var result = await _audit.VerifyChainAsync();
            StatusMessage = result.Ok
                ? $"审计完整性校验: 通过（{result.Checked} 条链记录）"
                : $"审计完整性校验: 失败！首个异常 Seq={result.FirstBadSeq}：{result.Message}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"审计完整性校验出错: {ex.Message}";
        }
    }

    public string BuildCommandsCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("时间,会话,工具,类型,服务器,命令,状态,决策,退出码,是否文件传输,文件,结果");
        foreach (var log in CommandLogs)
        {
            sb.AppendLine(string.Join(',',
                Escape(log.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")),
                Escape(log.SessionId ?? string.Empty),
                Escape(log.Tool ?? string.Empty),
                Escape(log.Category.ToString()),
                Escape(log.ServerName),
                Escape(log.Command),
                Escape(log.Status.ToString()),
                Escape(log.Decision ?? string.Empty),
                Escape(log.ExitCode?.ToString() ?? string.Empty),
                Escape(log.IsFileTransfer ? "是" : "否"),
                Escape(log.FilePath ?? string.Empty),
                Escape(log.Result ?? string.Empty)));
        }

        return sb.ToString();
    }

    public string BuildSqlCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("时间,会话,工具,数据源,类型,操作,SQL,状态,决策,影响行数,耗时ms,结果");
        foreach (var log in SqlLogs)
        {
            sb.AppendLine(string.Join(',',
                Escape(log.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")),
                Escape(log.SessionId ?? string.Empty),
                Escape(log.Tool ?? string.Empty),
                Escape(log.DataSourceName),
                Escape(log.Category.ToString()),
                Escape(log.Operation.ToString()),
                Escape(log.Sql),
                Escape(log.Status.ToString()),
                Escape(log.Decision ?? string.Empty),
                Escape(log.RowsAffected?.ToString() ?? string.Empty),
                Escape(log.DurationMs?.ToString("0") ?? string.Empty),
                Escape(log.Result ?? string.Empty)));
        }

        return sb.ToString();
    }

    public string BuildSessionsCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("会话ID,客户端,版本,首次活动,最近活动");
        foreach (var s in Sessions)
        {
            sb.AppendLine(string.Join(',',
                Escape(s.SessionId),
                Escape(s.ClientName ?? string.Empty),
                Escape(s.ClientVersion ?? string.Empty),
                Escape(s.StartedAt.ToString("yyyy-MM-dd HH:mm:ss")),
                Escape(s.LastSeenAt.ToString("yyyy-MM-dd HH:mm:ss"))));
        }

        return sb.ToString();
    }

    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";

        return value;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
