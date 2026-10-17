using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

/// <summary>
/// M1-6（评估报告 §10.A.1）：Agent 本地工具补审计——ops_doc_* 变更与脚本登记必须进哈希链
/// （此前 Agent 项目零审计调用，"谁把什么文件写进共享脚本库"无据可查）。
/// 读/list 为只读不记，避免噪音。
/// </summary>
public class WorkspaceToolsAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litssh-audit-ws-" + Guid.NewGuid().ToString("N"));
    private readonly List<CommandAuditLog> _records = new();

    public WorkspaceToolsAuditTests()
    {
        LocalAudit.AmbientSessionId = "agent-77";
        // 捕获器; 并行用例可能混入无关记录, 断言一律带唯一文件名过滤
        LocalAudit.OverrideSink = log => { lock (_records) _records.Add(log); return Task.CompletedTask; };
    }

    public void Dispose()
    {
        LocalAudit.OverrideSink = _ => Task.CompletedTask;   // 还原为程序集默认空操作
        LocalAudit.AmbientSessionId = null;
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    private List<CommandAuditLog> Snapshot()
    {
        lock (_records) return _records.ToList();
    }

    [Fact]
    public async Task Doc_mutations_emit_local_audit_reads_do_not()
    {
        var tools = WorkspaceTools.Create(_root);
        var marker = Guid.NewGuid().ToString("N")[..8];
        var rel = $"audit-{marker}.md";
        var full = Path.Combine(_root, rel);

        await Tool(tools, "ops_doc_write").InvokeAsync(Args(("content", "v1"), ("path", rel)), default);
        await Tool(tools, "ops_doc_append").InvokeAsync(Args(("content", "v2"), ("path", rel)), default);
        await Tool(tools, "ops_doc_patch").InvokeAsync(Args(("find", "v1"), ("replace", "v1x"), ("path", rel)), default);
        var readsBefore = Snapshot().Count;
        await Tool(tools, "ops_doc_read").InvokeAsync(Args(("path", rel)), default);
        await Tool(tools, "ops_doc_list").InvokeAsync(Args(), default);

        var mine = Snapshot().Where(r => r.FilePath == full).ToList();
        Assert.Equal(3, mine.Count);                                   // write+append+patch 各一条
        Assert.All(mine, r =>
        {
            Assert.Equal(AuditCategory.Local, r.Category);
            Assert.Equal(CommandStatus.Executed, r.Status);
            Assert.Equal("agent-77", r.SessionId);                     // 会话归因
            Assert.Equal("local", r.ServerId);
        });
        // 按写入顺序: write → append → patch
        Assert.Equal(new[] { "ops_doc_write", "ops_doc_append", "ops_doc_patch" },
            mine.Select(r => r.Tool).ToArray());

        // 只读操作不新增审计（防噪音）
        Assert.Equal(readsBefore, Snapshot().Count);
    }

    [Fact]
    public async Task Script_register_emits_local_audit()
    {
        var wsTools = WorkspaceTools.Create(_root);
        var scriptRel = "scripts/local/audit-probe.sh";
        await Tool(wsTools, "ops_doc_write").InvokeAsync(Args(("content", "echo hi"), ("path", scriptRel)), default);

        var scriptTools = ScriptTools.Create(_root, null);
        var before = Snapshot().Count;
        var result = (string)(await Tool(scriptTools, "ops_script_register").InvokeAsync(Args(
            ("path", scriptRel),
            ("summary", "审计探针"),
            ("compat", "bash4+")), default))!;

        Assert.Contains("已登记", result);
        var record = Snapshot().Skip(before).Single(r => r.Tool == "ops_script_register");
        Assert.Equal(AuditCategory.Local, record.Category);
        Assert.Equal(CommandStatus.Executed, record.Status);
        Assert.Equal("agent-77", record.SessionId);
        Assert.EndsWith("MANIFEST.md", record.FilePath!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(scriptRel, record.Command);
    }

    private static IAgentTool Tool(IReadOnlyList<IAgentTool> tools, string name) => tools.First(t => t.Name == name);

    private static Dictionary<string, object?> Args(params (string, object?)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => p.Item2);
}
