using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.Tests;

/// <summary>M1-6（评估报告 §10.A.1）：本地工具审计统一出口的记录形态与失败静默口径。</summary>
public class LocalAuditTests : IDisposable
{
    public void Dispose()
    {
        LocalAudit.OverrideSink = null;
        LocalAudit.AmbientSessionId = null;
    }

    [Fact]
    public async Task LogAsync_routes_to_sink_with_local_defaults_and_attribution()
    {
        CommandAuditLog? got = null;
        LocalAudit.OverrideSink = log => { got = log; return Task.CompletedTask; };
        LocalAudit.AmbientSessionId = "agent-9";

        await LocalAudit.LogAsync("ops_doc_write", "DOC_WRITE: a.md", CommandStatus.Executed,
            result: "12 字节", filePath: @"C:\ws\a.md", decision: "manual-approved", exitCode: 3);

        Assert.NotNull(got);
        Assert.Equal("local", got!.ServerId);
        Assert.Equal("本机", got.ServerName);
        Assert.Equal(AuditCategory.Local, got.Category);   // 新类别: 本地工具可按类过滤
        Assert.Equal("agent-9", got.SessionId);            // 会话归因随记录落链
        Assert.Equal("ops_doc_write", got.Tool);
        Assert.Equal("DOC_WRITE: a.md", got.Command);
        Assert.Equal(CommandStatus.Executed, got.Status);
        Assert.Equal("12 字节", got.Result);
        Assert.Equal(@"C:\ws\a.md", got.FilePath);
        Assert.Equal("manual-approved", got.Decision);
        Assert.Equal(3, got.ExitCode);
    }

    [Fact]
    public async Task LogAsync_without_ambient_session_leaves_session_null()
    {
        CommandAuditLog? got = null;
        LocalAudit.OverrideSink = log => { got = log; return Task.CompletedTask; };
        LocalAudit.AmbientSessionId = null;

        await LocalAudit.LogAsync("local_shell", "LOCAL_SHELL: git status", CommandStatus.Executed);

        Assert.NotNull(got);
        Assert.Null(got!.SessionId);
        Assert.Equal(AuditCategory.Local, got.Category);
    }

    [Fact]
    public async Task LogAsync_swallows_sink_failures()
    {
        // 口径与 MCP SafeLog 一致: 审计失败不阻断业务（调用方是工具执行路径）
        LocalAudit.OverrideSink = _ => throw new InvalidOperationException("审计通道故障");

        await LocalAudit.LogAsync("ops_doc_write", "DOC_WRITE: x.md", CommandStatus.Executed);
    }

    [Fact]
    public async Task BeginRunScope_overrides_fallback_inside_flow_and_restores_after()
    {
        // §4.1 并行会话归因：运行作用域只作用于本轮执行流；结束回退进程级缺省；显式 sessionId 最优先
        CommandAuditLog? got = null;
        LocalAudit.OverrideSink = log => { got = log; return Task.CompletedTask; };
        LocalAudit.AmbientSessionId = "agent-ui";

        using (LocalAudit.BeginRunScope("agent-77"))
        {
            await LocalAudit.LogAsync("ops_doc_write", "DOC_WRITE: run.md", CommandStatus.Executed);
            Assert.Equal("agent-77", got?.SessionId);
        }

        got = null;
        await LocalAudit.LogAsync("ops_doc_write", "DOC_WRITE: ui.md", CommandStatus.Executed);
        Assert.Equal("agent-ui", got?.SessionId);

        got = null;
        using (LocalAudit.BeginRunScope("agent-77"))
            await LocalAudit.LogAsync("session_delete", "SESSION_DELETE: x", CommandStatus.Executed, sessionId: "agent-5");
        Assert.Equal("agent-5", got?.SessionId);   // 显式指定压过运行作用域（UI 动作防串号）
    }

    [Fact]
    public async Task Run_scope_does_not_leak_into_sibling_flow_created_before_scope()
    {
        // 作用域建立前就派生的并发流（如窗口关闭前发起的其他工作）不应看到运行会话归因
        CommandAuditLog? got = null;
        LocalAudit.OverrideSink = log => { got = log; return Task.CompletedTask; };
        LocalAudit.AmbientSessionId = "agent-ui";

        var ready = new TaskCompletionSource();
        var sibling = Task.Run(async () =>
        {
            await ready.Task;   // 等作用域已在外层建立；本流的执行上下文在建立前就已捕获
            await LocalAudit.LogAsync("local_shell", "LOCAL_SHELL: git status", CommandStatus.Executed);
        });

        using (LocalAudit.BeginRunScope("agent-run"))
            ready.SetResult();
        await sibling;

        Assert.Equal("agent-ui", got?.SessionId);
        LocalAudit.OverrideSink = null;
        LocalAudit.AmbientSessionId = null;
    }

    [Fact]
    public async Task Sibling_run_scopes_keep_separate_attribution()
    {
        // 两条并行运行流各自归因，互不串号（多会话并行的审计链前提）
        var records = new System.Collections.Concurrent.ConcurrentQueue<(string Cmd, string? Session)>();
        LocalAudit.OverrideSink = log => { records.Enqueue((log.Command, log.SessionId)); return Task.CompletedTask; };
        LocalAudit.AmbientSessionId = "agent-ui";

        static async Task RunScopedAsync(string sessionId, System.Collections.Concurrent.ConcurrentQueue<(string Cmd, string? Session)> sink)
        {
            using (LocalAudit.BeginRunScope(sessionId))
                await LocalAudit.LogAsync("ops_doc_write", $"DOC_WRITE: {sessionId}", CommandStatus.Executed);
        }

        await Task.WhenAll(RunScopedAsync("agent-a", records), RunScopedAsync("agent-b", records));

        Assert.Equal(2, records.Count);
        Assert.Contains(records, r => r.Cmd == "DOC_WRITE: agent-a" && r.Session == "agent-a");
        Assert.Contains(records, r => r.Cmd == "DOC_WRITE: agent-b" && r.Session == "agent-b");
        LocalAudit.OverrideSink = null;
        LocalAudit.AmbientSessionId = null;
    }
}
