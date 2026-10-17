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
}
