using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.McpServer.Tools;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

public class JobOperationCatalogTests
{
    [Fact]
    public void Usage_guide_and_docs_cover_all_registered_operation_policies()
    {
        var guideJson = UsageGuideTools.BuildGuide(null, includeInternalTools: false);
        using var guide = JsonDocument.Parse(guideJson);
        var operations = guide.RootElement.GetProperty("job_operations").EnumerateArray()
            .Select(e => e.GetProperty("operation").GetString())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToArray();
        var docs = File.ReadAllText(FindRepoFile(Path.Combine("docs", "TOOLS.md")));

        foreach (var policy in JobOperationPolicies.All)
        {
            Assert.Contains(policy.Operation, operations);
            Assert.Contains(policy.Operation, docs, StringComparison.Ordinal);
            Assert.NotNull(policy.ArgumentsSchema);
            Assert.NotEmpty(policy.ArgumentsSchema!);
        }

        var inventory = guide.RootElement.GetProperty("job_operations").EnumerateArray()
            .Single(e => e.GetProperty("operation").GetString() == "fleet_inventory");
        Assert.Contains(inventory.GetProperty("argumentsSchema").EnumerateArray(),
            arg => arg.GetProperty("name").GetString() == "serverIds" &&
                   arg.GetProperty("required").GetBoolean());
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            if (File.Exists(Path.Combine(dir.FullName, "LitSSHmcp.slnx")))
                return Path.Combine(dir.FullName, relativePath);
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"找不到仓库文件: {relativePath}");
    }
}
