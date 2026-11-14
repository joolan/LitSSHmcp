using System.Text.Json;
using LitSSHmcp.Agent;

namespace LitSSHmcp.App.ViewModels;

public partial class AgentViewModel
{
    public bool CanManageJobs => _runtime is not null;

    /// <summary>调用 job_* 工具（列表/状态/取消）；失败时返回结构化错误 JSON，便于 UI 统一展示。</summary>
    public async Task<string> InvokeJobToolAsync(string toolName, IDictionary<string, object?> arguments,
        CancellationToken ct = default)
    {
        if (_runtime is null)
            return JsonSerializer.Serialize(new { success = false, status = "not_ready", error = "运行时尚未就绪，请先等待会话加载" });

        try
        {
            var result = await _runtime.InvokeBuiltinToolAsync(toolName, arguments, ct);
            var text = McpToolResultNormalizer.Normalize(result).Text;
            var trimmed = text.TrimStart();
            if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
                return text;
            return JsonSerializer.Serialize(new
            {
                success = false,
                status = "tool_error",
                error = string.IsNullOrWhiteSpace(text) ? "(空工具结果)" : text
            });
        }
        catch (OperationCanceledException)
        {
            return JsonSerializer.Serialize(new { success = false, status = "cancelled", error = "操作已取消" });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, status = "error", error = ex.Message });
        }
    }
}
