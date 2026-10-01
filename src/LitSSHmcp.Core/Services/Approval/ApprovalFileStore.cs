using System.Text.Json;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Core.Services.Approval;

/// <summary>带外审批（CLI/IPC）待决请求文件内容。</summary>
public sealed class ApprovalRequestFile
{
    public string Id { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int TimeoutSeconds { get; set; }
}

/// <summary>带外审批决策文件内容。</summary>
public sealed class ApprovalDecisionFile
{
    public string Id { get; set; } = string.Empty;
    public bool Approved { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
    public string? Channel { get; set; }
}

/// <summary>
/// 带外审批的文件交换：MCP 写 pending-&lt;id&gt;.json 等待；操作员用 <c>litssh approve/deny &lt;id&gt;</c> 写 decision-&lt;id&gt;.json。
/// 目录默认 %APPDATA%\LitSSH\approvals（同用户可读写）。
/// </summary>
public static class ApprovalFileStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string PendingPath(string dir, string id) => Path.Combine(dir, $"pending-{id}.json");
    public static string DecisionPath(string dir, string id) => Path.Combine(dir, $"decision-{id}.json");

    public static void WriteRequest(string dir, ApprovalRequestFile request)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(PendingPath(dir, request.Id), JsonSerializer.Serialize(request, Json));
    }

    public static IReadOnlyList<ApprovalRequestFile> ListPending(string dir)
    {
        if (!Directory.Exists(dir))
            return Array.Empty<ApprovalRequestFile>();

        var list = new List<ApprovalRequestFile>();
        foreach (var file in Directory.EnumerateFiles(dir, "pending-*.json"))
        {
            try
            {
                var request = JsonSerializer.Deserialize<ApprovalRequestFile>(File.ReadAllText(file), Json);
                if (request != null)
                    list.Add(request);
            }
            catch
            {
                // 忽略损坏/半写入的文件
            }
        }

        return list.OrderBy(r => r.CreatedAt).ToArray();
    }

    public static void WriteDecision(string dir, ApprovalDecisionFile decision)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(DecisionPath(dir, decision.Id), JsonSerializer.Serialize(decision, Json));
    }

    public static ApprovalDecisionFile? TryReadDecision(string dir, string id)
    {
        var path = DecisionPath(dir, id);
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ApprovalDecisionFile>(File.ReadAllText(path), Json);
        }
        catch
        {
            return null;
        }
    }

    public static void Remove(string dir, string id)
    {
        try { File.Delete(PendingPath(dir, id)); } catch { /* ignore */ }
        try { File.Delete(DecisionPath(dir, id)); } catch { /* ignore */ }
    }
}
