using System.IO;
using System.Text;
using LitSSHmcp.Agent.Scheduling;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services.AiTasks;

/// <summary>AI 定时任务报告落盘：每次运行生成可回看的 Markdown 报告，路径稳定可预测。</summary>
public static class AiTaskReportStore
{
    public static string Dir => Path.Combine(ConfigPaths.AppDataDir, "reports", "ai-tasks");

    public static string PathFor(string taskId, long runId)
    {
        var safe = new string((taskId ?? string.Empty)
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            .ToArray());
        if (safe.Length == 0)
            safe = "task";
        return Path.Combine(Dir, $"{safe}-{runId}.md");
    }

    public static async Task<string> SaveAsync(AiTaskConfig task, long runId, AiTaskRunResult result,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Dir);
        var path = PathFor(task.Id, runId);
        var status = result.Cancelled ? "cancelled" : result.Success ? "succeeded" : "failed";
        var sb = new StringBuilder();
        sb.AppendLine($"# {task.Name}");
        sb.AppendLine();
        sb.AppendLine($"- 时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 状态: {status}");
        sb.AppendLine($"- 调度: {task.DescribeSchedule()}");
        sb.AppendLine($"- 只读: {(task.ReadOnly ? "是" : "否")}");
        if (!string.IsNullOrWhiteSpace(result.SessionId))
            sb.AppendLine($"- AI 会话: {result.SessionId}");
        sb.AppendLine($"- Token: {result.InputTokens} in / {result.OutputTokens} out");
        if (!string.IsNullOrWhiteSpace(result.Error))
            sb.AppendLine($"- 错误: {result.Error}");
        sb.AppendLine();
        sb.AppendLine("## 报告");
        sb.AppendLine(string.IsNullOrWhiteSpace(result.Summary) ? "（无输出）" : result.Summary);
        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct);
        return path;
    }
}
