using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.McpServer.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

/// <summary>
/// 保证"已注册工具 ↔ docs/TOOLS.md ↔ get_usage_guide 内置清单"三者一致(单一事实来源的一致性守门测试)。
/// 工具新增/改名/删除后若忘记同步文档, 本测试会失败。
/// </summary>
public class ToolCatalogConsistencyTests
{
    private const int ExpectedToolCount = 49;

    [Fact]
    public async Task Registered_tools_match_docs_and_usage_guide()
    {
        var registered = GetRegisteredTools();      // name -> description (来自 [McpServerTool]/[Description])
        var docs = ParseDocsTools();                // (分组键, 工具名) 来自 docs/TOOLS.md
        var guide = await GetGuideToolNamesAsync(); // 来自 get_usage_guide 的返回

        // 1) 数量与集合: 注册工具 == docs == 期望数量
        Assert.Equal(ExpectedToolCount, registered.Count);
        var registeredNames = registered.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var docNames = docs.Select(d => d.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(registeredNames, docNames);

        // 2) get_usage_guide 内置清单 == 注册工具
        Assert.Equal(registeredNames, guide.OrderBy(x => x, StringComparer.Ordinal).ToArray());

        // 3) 每个工具都必须归属某个分组, 且 docs 分组键集合/顺序 == ToolGroups.All
        Assert.All(docs, d => Assert.NotNull(d.Group));
        var docGroups = docs.Select(d => d.Group!).Distinct().ToArray();
        Assert.Equal(ToolGroups.All, docGroups);
    }

    private static Dictionary<string, string> GetRegisteredTools()
    {
        var assembly = typeof(ServerTools).Assembly;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var type in assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var tool = method.GetCustomAttribute<McpServerToolAttribute>();
                if (tool?.Name is null)
                    continue;

                result[tool.Name] = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
            }
        }

        return result;
    }

    private static async Task<string[]> GetGuideToolNamesAsync()
    {
        var json = await new UsageGuideTools().GetUsageGuide();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("tools")
            .EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!)
            .ToArray();
    }

    private static List<(string? Group, string Name)> ParseDocsTools()
    {
        var path = FindRepoFile(Path.Combine("docs", "TOOLS.md"));
        var result = new List<(string? Group, string Name)>();
        string? group = null;

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.TrimEnd();
            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                group = ExtractGroupKey(trimmed[3..].Trim());
            }
            else if (trimmed.StartsWith("### `", StringComparison.Ordinal))
            {
                // "### `name`" -> 名称位于第 5 个字符起, 到下一个反引号为止
                var end = trimmed.IndexOf('`', 5);
                if (end > 5)
                    result.Add((group, trimmed[5..end]));
            }
        }

        return result;
    }

    private static string? ExtractGroupKey(string title)
    {
        var open = title.LastIndexOf('（');
        var close = title.LastIndexOf('）');
        if (open < 0 || close <= open)
            return null;

        var key = title[(open + 1)..close].Trim();
        return ToolGroups.All.Contains(key, StringComparer.OrdinalIgnoreCase) ? key : null;
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"找不到仓库文件: {relativePath} (从 {AppContext.BaseDirectory} 向上查找)");
    }
}
