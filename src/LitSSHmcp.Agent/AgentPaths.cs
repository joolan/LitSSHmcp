namespace LitSSHmcp.Agent;

/// <summary>启动本机 MCP 服务器的命令（exe 直启，或用 dotnet 运行 dll）。</summary>
public sealed record McpServerLaunch(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory);

/// <summary>定位/启动 MCP 服务器。</summary>
public static class AgentPaths
{
    /// <summary>
    /// 解析 MCP 服务器启动方式：显式配置路径 → App 目录 mcp/ → App 目录同级 → 仓库开发产物。
    /// 返回 null 表示未找到（调用方给出"请配置 agent.mcpServerPath"提示）。
    /// </summary>
    public static McpServerLaunch? ResolveLaunch(string? configuredPath = null)
    {
        foreach (var candidate in EnumerateCandidates(configuredPath))
        {
            if (candidate.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(candidate))
                    return new McpServerLaunch("dotnet", new[] { candidate }, Path.GetDirectoryName(candidate));
            }
            else if (File.Exists(candidate))
            {
                return new McpServerLaunch(candidate, Array.Empty<string>(), Path.GetDirectoryName(candidate));
            }
        }
        return null;
    }

    private static IEnumerable<string> EnumerateCandidates(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            yield return configuredPath!.Trim();

        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "mcp", "LitSSHmcp.McpServer.exe");
        yield return Path.Combine(baseDir, "mcp", "LitSSHmcp.McpServer.dll");
        yield return Path.Combine(baseDir, "LitSSHmcp.McpServer.exe");
        yield return Path.Combine(baseDir, "LitSSHmcp.McpServer.dll");

        // 开发环境：从 App 输出目录向上查找仓库内 MCP 服务器产物
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var serverBin = Path.Combine(dir.FullName, "src", "LitSSHmcp.McpServer", "bin");
            if (!Directory.Exists(serverBin))
                continue;

            foreach (var exe in Directory.EnumerateFiles(serverBin, "LitSSHmcp.McpServer.exe", SearchOption.AllDirectories)
                         .OrderByDescending(File.GetLastWriteTimeUtc))
                yield return exe;
        }
    }
}
