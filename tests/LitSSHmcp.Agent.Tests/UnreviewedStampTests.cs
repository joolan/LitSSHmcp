using System.IO;
using LitSSHmcp.Agent;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

/// <summary>
/// 提升审阅策略的旁路补标（评估报告 §10.D.1 待办 #9 / §5.1 行 185 的"或标记未审阅"方案，M1 实现时定）：
/// 脚本类产物经 ops_promote 提升走桌面确认；直接经 shared/ 前缀写入共享区则在文件头打"未审阅"标记。
/// </summary>
[Collection("local-audit")]   // AmbientSessionId 是进程级静态状态: 与 WorkspaceToolsAuditTests 等审计测试类串行
public class UnreviewedStampTests : IDisposable
{
    private readonly string _session;
    private readonly string _shared;
    private readonly string _sharedRoot;

    public UnreviewedStampTests()
    {
        _sharedRoot = Path.Combine(Path.GetTempPath(), "litssh-stamp-shared-" + Guid.NewGuid().ToString("N"));
        _session = Path.Combine(_sharedRoot, "jobs", "local", "s1");
        _shared = _sharedRoot;   // 与 AgentRuntime 一致: 双根时 shared 根=共享工作区, 主根=会话目录
        Directory.CreateDirectory(_session);
        LocalAudit.AmbientSessionId = "agent-stamp-test";
    }

    public void Dispose()
    {
        LocalAudit.AmbientSessionId = null;
        try { Directory.Delete(_sharedRoot, true); } catch { /* ignore */ }
    }

    private IReadOnlyList<IAgentTool> DualRoot() => WorkspaceTools.Create(_session, _shared);

    private static Dictionary<string, object?> Args(params (string, object?)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public async Task Shared_script_write_gains_unreviewed_stamp_with_session()
    {
        var tools = DualRoot();

        await tools.First(t => t.Name == "ops_doc_write")
            .InvokeAsync(Args(("content", "echo hi"), ("path", "shared/scripts/local/x.sh")), default);

        var written = File.ReadAllText(Path.Combine(_sharedRoot, "scripts", "local", "x.sh"));
        Assert.StartsWith("# 未审阅:", written, StringComparison.Ordinal);
        Assert.Contains("来源会话 agent-stamp-test", written);
        Assert.Contains("echo hi", written);
    }

    [Fact]
    public async Task Session_script_write_and_non_script_shared_write_are_not_stamped()
    {
        var tools = DualRoot();

        // 会话目录内的脚本(产出地)不打标
        await tools.First(t => t.Name == "ops_doc_write")
            .InvokeAsync(Args(("content", "echo session"), ("path", "local.sh")), default);
        Assert.Equal("echo session", File.ReadAllText(Path.Combine(_session, "local.sh")));

        // 共享区非脚本文件不打标
        await tools.First(t => t.Name == "ops_doc_write")
            .InvokeAsync(Args(("content", "ASSET"), ("path", "shared/OPS_ASSETS.md")), default);
        var asset = File.ReadAllText(Path.Combine(_sharedRoot, "OPS_ASSETS.md"));
        Assert.StartsWith("ASSET", asset, StringComparison.Ordinal);
        Assert.DoesNotContain("未审阅", asset);
    }

    [Fact]
    public async Task Single_root_mode_is_untouched()
    {
        var tools = WorkspaceTools.Create(_sharedRoot);   // 单根=旧会话现状

        await tools.First(t => t.Name == "ops_doc_write")
            .InvokeAsync(Args(("content", "echo hi"), ("path", "legacy.sh")), default);

        Assert.Equal("echo hi", File.ReadAllText(Path.Combine(_sharedRoot, "legacy.sh")));
    }

    [Fact]
    public async Task Append_creates_stamped_shared_script_but_existing_file_keeps_header()
    {
        var tools = DualRoot();
        var target = Path.Combine(_sharedRoot, "scripts", "local", "a.sh");

        // 新建(append 落到不存在的文件): 打标
        await tools.First(t => t.Name == "ops_doc_write")
            .InvokeAsync(Args(("mode", "append"), ("content", "first"), ("path", "shared/scripts/local/a.sh")), default);
        Assert.StartsWith("# 未审阅:", File.ReadAllText(target), StringComparison.Ordinal);

        // 追加既有文件: 保留原头部, 不重复打标
        await tools.First(t => t.Name == "ops_doc_write")
            .InvokeAsync(Args(("mode", "append"), ("content", "second"), ("path", "shared/scripts/local/a.sh")), default);
        var text = File.ReadAllText(target);
        Assert.Single(text.Split("# 未审阅:").Skip(1));           // 标记只出现一次
        Assert.Contains("first", text);
        Assert.Contains("second", text);

        // 覆盖重写(内容不带标记): 重新打标, 标记不因重写丢失
        await tools.First(t => t.Name == "ops_doc_write")
            .InvokeAsync(Args(("content", "rewritten"), ("path", "shared/scripts/local/a.sh")), default);
        var rewritten = File.ReadAllText(target);
        Assert.StartsWith("# 未审阅:", rewritten, StringComparison.Ordinal);
        Assert.Contains("rewritten", rewritten);

        // 覆盖重写(内容已含标记, 模型照抄读回内容): 不重复
        await tools.First(t => t.Name == "ops_doc_write")
            .InvokeAsync(Args(("content", rewritten), ("path", "shared/scripts/local/a.sh")), default);
        Assert.Equal(rewritten, File.ReadAllText(target));
    }
}
