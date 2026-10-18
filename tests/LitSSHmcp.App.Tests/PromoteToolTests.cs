using System.IO;
using System.Threading;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.LocalTerminal;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>
/// 提升命令（评估报告 §10.D.1 投毒路径防护 / 待办 #9 / M1-7）：
/// 会话目录 → 共享工作区归档；**脚本类须桌面确认**（含来源会话标识）；非脚本直接提升但记审计；
/// 共享区模式/界外/覆盖 jobs 已有文件一律拒绝。
/// </summary>
[Collection("local-audit")]   // OverrideSink 是进程级静态替身: 与其他审计测试类串行, 防相互覆盖
public class PromoteToolTests : IDisposable
{
    private readonly string _shared;
    private readonly string _session;
    private readonly List<CommandAuditLog> _records = new();

    public PromoteToolTests()
    {
        _shared = Path.Combine(Path.GetTempPath(), "litssh-promote-shared-" + Guid.NewGuid().ToString("N"));
        _session = Path.Combine(_shared, "jobs", "local", "s1");
        Directory.CreateDirectory(_session);
        LocalAudit.AmbientSessionId = "agent-promote-test";
        LocalAudit.OverrideSink = log => { lock (_records) _records.Add(log); return Task.CompletedTask; };
    }

    public void Dispose()
    {
        LocalAudit.OverrideSink = _ => Task.CompletedTask;
        LocalAudit.AmbientSessionId = null;
        try { Directory.Delete(_shared, true); } catch { /* ignore */ }
    }

    private List<CommandAuditLog> Snapshot()
    {
        lock (_records) return _records.ToList();
    }

    private PromoteTool Make(LocalApprovalDecision decision) =>
        new(() => _session, () => _shared,
            (_, _, _) => Task.FromResult(decision),
            () => "sess-9");

    private PromoteTool MakeCapturing(LocalApprovalDecision decision,
        List<(string Command, string WorkDir)> approvals) =>
        new(() => _session, () => _shared,
            (cmd, workDir, _) => { lock (approvals) approvals.Add((cmd, workDir)); return Task.FromResult(decision); },
            () => "sess-9");

    private static Dictionary<string, object?> Args(string path, string? to = null)
    {
        var d = new Dictionary<string, object?> { ["path"] = path };
        if (to is not null) d["to"] = to;
        return d;
    }

    [Fact]
    public async Task Non_script_promote_moves_file_to_shared_and_audits()
    {
        var src = Path.Combine(_session, "report.md");
        File.WriteAllText(src, "# 产出");
        var tool = Make(LocalApprovalDecision.Allow);

        var result = await tool.InvokeAsync(Args("report.md"), CancellationToken.None);

        Assert.Contains("已提升", result?.ToString());
        Assert.False(File.Exists(src));
        var target = Path.Combine(_shared, "report.md");
        Assert.True(File.Exists(target));
        Assert.Equal("# 产出", File.ReadAllText(target));

        var record = Snapshot().Single(r => r.Tool == "ops_promote");
        Assert.Equal(AuditCategory.Local, record.Category);
        Assert.Equal(CommandStatus.Executed, record.Status);
        Assert.Equal("agent-promote-test", record.SessionId);        // 会话归因
        Assert.Equal("local", record.ServerId);
        Assert.Equal(target, record.FilePath);
        Assert.Equal("PROMOTE: report.md -> shared/report.md", record.Command);
        Assert.Null(record.Decision);                                 // 非脚本直接提升, 无审批决策
    }

    [Fact]
    public async Task Script_promote_denied_keeps_file_and_audits_rejection()
    {
        var src = Path.Combine(_session, "deploy.sh");
        File.WriteAllText(src, "#!/bin/bash\necho hi");
        var approvals = new List<(string Command, string WorkDir)>();
        var tool = MakeCapturing(LocalApprovalDecision.Deny, approvals);

        var result = await tool.InvokeAsync(Args("deploy.sh"), CancellationToken.None);

        Assert.Contains("拒绝", result?.ToString());
        Assert.True(File.Exists(src));                               // 未移动
        Assert.False(File.Exists(Path.Combine(_shared, "deploy.sh")));

        var approval = Assert.Single(approvals);
        Assert.Equal(_session, approval.WorkDir);                    // 审批弹窗锚定会话目录
        Assert.Contains("来源会话", approval.Command);                // §10.D.1: 执行时显示来源会话
        Assert.Contains("sess-9", approval.Command);

        var record = Snapshot().Single(r => r.Tool == "ops_promote");
        Assert.Equal(CommandStatus.Rejected, record.Status);
        Assert.Equal("manual-rejected", record.Decision);
        Assert.Equal("agent-promote-test", record.SessionId);
    }

    [Fact]
    public async Task Script_promote_approved_moves_file_and_audits_approval()
    {
        var src = Path.Combine(_session, "cleanup.py");
        File.WriteAllText(src, "print('x')");
        var tool = Make(LocalApprovalDecision.Allow);

        var result = await tool.InvokeAsync(Args("cleanup.py", "scripts/local/cleanup.py"), CancellationToken.None);

        Assert.Contains("桌面确认", result?.ToString());
        Assert.False(File.Exists(src));
        var target = Path.Combine(_shared, "scripts", "local", "cleanup.py");
        Assert.True(File.Exists(target));

        var record = Snapshot().Single(r => r.Tool == "ops_promote");
        Assert.Equal(CommandStatus.Executed, record.Status);
        Assert.Equal("manual-approved", record.Decision);
        Assert.Equal(target, record.FilePath);
        Assert.Equal("PROMOTE: cleanup.py -> shared/scripts/local/cleanup.py", record.Command);
    }

    [Fact]
    public async Task Shared_workspace_mode_without_session_dir_rejects()
    {
        var tool = new PromoteTool(() => (string?)null, () => _shared,
            (_, _, _) => Task.FromResult(LocalApprovalDecision.Allow), () => "sess-9");

        var result = await tool.InvokeAsync(Args("x.md"), CancellationToken.None);

        Assert.Contains("未分配", result?.ToString());
        Assert.Empty(Snapshot());                                    // 拒绝发生在审计之前
    }

    [Fact]
    public async Task Refuses_existing_target_jobs_target_and_path_escape()
    {
        // ① 目标已存在 → 拒绝覆盖（防静默改写共享脚本）
        File.WriteAllText(Path.Combine(_session, "hello.txt"), "new");
        File.WriteAllText(Path.Combine(_shared, "hello.txt"), "old");
        var tool = Make(LocalApprovalDecision.Allow);
        var r1 = await tool.InvokeAsync(Args("hello.txt"), CancellationToken.None);
        Assert.Contains("已存在", r1?.ToString());
        Assert.Equal("old", File.ReadAllText(Path.Combine(_shared, "hello.txt")));

        // ② 目标禁止落入 jobs/ 会话树
        File.WriteAllText(Path.Combine(_session, "a.txt"), "a");
        var r2 = await tool.InvokeAsync(Args("a.txt", "jobs/local/s2/b.txt"), CancellationToken.None);
        Assert.Contains("jobs", r2?.ToString());
        Assert.True(File.Exists(Path.Combine(_session, "a.txt")));

        // ③ 路径穿越拒绝
        var r3 = await tool.InvokeAsync(Args("../evil.txt"), CancellationToken.None);
        Assert.Contains("非法路径", r3?.ToString());

        // ④ 提升方向为 会话→共享, 源不能带 shared/
        var r4 = await tool.InvokeAsync(Args("shared/anything.txt"), CancellationToken.None);
        Assert.Contains("shared/", r4?.ToString());

        Assert.Empty(Snapshot());                                    // 全部拒绝, 无审计写入
    }
}
