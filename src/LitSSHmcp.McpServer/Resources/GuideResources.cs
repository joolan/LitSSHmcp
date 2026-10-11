using System.ComponentModel;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Tools;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Resources;

/// <summary>
/// MCP 资源：使用指南（等价 <c>mcp_usage_guide</c> 的返回），供客户端直接挂载为上下文。
/// 按启用的工具分组裁剪，与工具/说明保持一致。
/// </summary>
[McpServerResourceType]
public class GuideResources
{
    private readonly IConfigService _configService;

    public GuideResources(IConfigService configService) => _configService = configService;

    [McpServerResource(UriTemplate = "litssh://usage-guide", Name = "litssh-usage-guide",
        Title = "LitSSH 使用指南", MimeType = "application/json")]
    [Description("LitSSH MCP 使用指南：当前可用工具清单 + 意图路由 + 注意事项（等价 mcp_usage_guide）。")]
    public async Task<string> GetGuide()
    {
        IReadOnlySet<string>? enabled = null;
        try
        {
            var config = await _configService.LoadConfigAsync();
            enabled = ToolGroups.ResolveEnabled(config.Tools);
        }
        catch
        {
            enabled = null;   // 配置读取失败按“全部启用”降级
        }

        return UsageGuideTools.BuildGuide(enabled);
    }
}
