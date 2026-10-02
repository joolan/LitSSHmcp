using LitSSHmcp.Core.Services.Security;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Services;

/// <summary>
/// MCP 全局开关：当 <c>security.enabled=false</c> 时，通过请求中间件拒绝**所有**工具调用。
/// 读取的是 <see cref="ISecurityOptionsProvider"/>（按配置文件 mtime 热更新），因此关闭/开启**无需重启** MCP。
/// </summary>
public static class McpGlobalSwitch
{
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CreateFilter()
    {
        McpRequestFilter<CallToolRequestParams, CallToolResult> filter = next =>
            (context, ct) =>
            {
                var provider = context.Services?.GetService(typeof(ISecurityOptionsProvider)) as ISecurityOptionsProvider;
                if (provider is { Enabled: false })
                {
                    return ValueTask.FromResult(new CallToolResult
                    {
                        IsError = true,
                        Content = new List<ContentBlock>
                        {
                            new TextContentBlock
                            {
                                Text = "MCP 服务已在「安全设置」中被禁用（security.enabled=false），所有工具调用被拒绝。"
                            }
                        }
                    });
                }

                return next(context, ct);
            };

        return filter;
    }
}
