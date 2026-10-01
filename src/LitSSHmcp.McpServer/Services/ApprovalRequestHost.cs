using System.Text.Json;
using System.Windows.Forms;

namespace LitSSHmcp.McpServer.Services;

/// <summary>审批请求（父进程写入临时文件，子进程读取）。不含任何密码等敏感信息。</summary>
public sealed class ApprovalRequest
{
    public string Server { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    public bool TopMost { get; set; } = true;
}

/// <summary>
/// 审批子进程宿主：以独立进程显示审批对话框，通过退出码回传结果。
/// 退出码：0=允许，1=拒绝，2=超时。
/// </summary>
public static class ApprovalRequestHost
{
    public const int ExitAllow = 0;
    public const int ExitDeny = 1;
    public const int ExitTimeout = 2;

    public const string ArgumentName = "--approval-request";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize(ApprovalRequest request) => JsonSerializer.Serialize(request, Json);

    public static int Run(string requestPath)
    {
        ApprovalRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<ApprovalRequest>(File.ReadAllText(requestPath), Json);
        }
        catch
        {
            return ExitDeny;
        }

        if (request == null)
            return ExitDeny;

        var code = ExitDeny;

        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new ApprovalDialog(
                    request.Server,
                    request.Operation,
                    request.Command,
                    request.FilePath,
                    request.TimeoutSeconds,
                    request.TopMost);

                var result = dialog.ShowDialog();
                code = result == DialogResult.OK
                    ? ExitAllow
                    : dialog.TimedOut ? ExitTimeout : ExitDeny;
            }
            catch
            {
                code = ExitDeny;
            }
        })
        {
            Name = "ApprovalDialog-STA",
            IsBackground = false
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var waitMs = (request.TimeoutSeconds > 0 ? request.TimeoutSeconds + 30 : 600) * 1000;
        if (!thread.Join(waitMs))
            return ExitTimeout;

        try { File.Delete(requestPath); } catch { /* ignore */ }
        return code;
    }
}
