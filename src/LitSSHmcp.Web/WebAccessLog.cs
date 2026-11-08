using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Web;

/// <summary>
/// Web 访问日志（评估报告 §10.A.3）：登录成功/失败/锁定、登出、越权、会话启停、文件下载。
/// 独立落盘 %APPDATA%\LitSSH\logs\web-access-yyyyMMdd.log（与 mcp-*.log 同目录、按天滚动），
/// 行格式：UTC ISO 时间 | 事件 | 用户 | 来源 IP | 详情（竖线/换行净化，写失败静默）。
/// </summary>
public sealed class WebAccessLog
{
    private readonly string _dir;
    private readonly object _gate = new();
    private DateTime _lastCleanupDate = DateTime.MinValue;

    public WebAccessLog(string? dir = null)
    {
        _dir = string.IsNullOrWhiteSpace(dir) ? ConfigPaths.LogsDir : dir!;
    }

    public void Write(string evt, string? user, string? ip, string? detail = null)
    {
        var line = string.Join('|',
            DateTime.UtcNow.ToString("O"),
            Clean(evt) ?? "-",
            Clean(user) ?? "-",
            Clean(ip) ?? "-",
            Clean(detail) ?? "-");
        try
        {
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, $"web-access-{DateTime.UtcNow:yyyyMMdd}.log");
            lock (_gate)
            {
                CleanupOldLogs();
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // 访问日志失败不影响主流程（与 FileLoggerProvider 先例一致）
        }
    }

    private void CleanupOldLogs()
    {
        if (_lastCleanupDate.Date == DateTime.UtcNow.Date)
            return;

        _lastCleanupDate = DateTime.UtcNow.Date;
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var file in Directory.EnumerateFiles(_dir, "web-access-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch { /* 单个日志删除失败忽略 */ }
            }
        }
        catch { /* 日志目录不可用忽略 */ }
    }

    private static string? Clean(string? s)
        => string.IsNullOrEmpty(s) ? null : s.Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
}
