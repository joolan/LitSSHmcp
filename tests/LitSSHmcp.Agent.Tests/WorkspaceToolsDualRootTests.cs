using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

/// <summary>
/// 双根工作区（需求 D / D6 用户确认方案）：默认路径 → 会话目录、shared/ 前缀与省略 path → 共享区，
/// shared/jobs/ 禁触，逃逸照旧拦截。
/// </summary>
public class WorkspaceToolsDualRootTests : IDisposable
{
    private readonly string _session = Path.Combine(Path.GetTempPath(), "litssh-ws2-session-" + Guid.NewGuid().ToString("N"));
    private readonly string _shared = Path.Combine(Path.GetTempPath(), "litssh-ws2-shared-" + Guid.NewGuid().ToString("N"));

    private static IAgentTool Tool(IReadOnlyList<IAgentTool> tools, string name) => tools.First(t => t.Name == name);

    private static Dictionary<string, object?> Args(params (string, object?)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => p.Item2);

    private IReadOnlyList<IAgentTool> DualRootTools()
    {
        Directory.CreateDirectory(_session);
        Directory.CreateDirectory(_shared);
        return WorkspaceTools.Create(_session, _shared);
    }

    [Fact]
    public async Task Default_doc_goes_shared_explicit_path_goes_session_shared_prefix_goes_shared()
    {
        var tools = DualRootTools();

        // 省略 path = 共享区资产档案（跨会话 OPS_ASSETS 不被按会话割裂）
        await Tool(tools, "ops_doc_write").InvokeAsync(Args(("content", "ASSET")), default);
        Assert.True(File.Exists(Path.Combine(_shared, "OPS_ASSETS.md")));
        Assert.False(File.Exists(Path.Combine(_session, "OPS_ASSETS.md")));

        // 显式相对路径 = 任务产出落会话目录
        await Tool(tools, "ops_doc_write").InvokeAsync(Args(("content", "REPORT"), ("path", "report.md")), default);
        Assert.True(File.Exists(Path.Combine(_session, "report.md")));
        Assert.False(File.Exists(Path.Combine(_shared, "report.md")));

        // shared/ 前缀 = 共享区
        await Tool(tools, "ops_doc_write").InvokeAsync(Args(("content", "NOTE"), ("path", "shared/notes.md")), default);
        Assert.True(File.Exists(Path.Combine(_shared, "notes.md")));

        // 省略 path 读回共享资产
        var read = await Tool(tools, "ops_doc_read").InvokeAsync(Args(), default);
        Assert.Equal("ASSET", read);

        // 清单：会话条目裸名 + 共享条目 shared/ 前缀，且不暴露 jobs/ 会话树
        var listed = (string)(await Tool(tools, "ops_doc_list").InvokeAsync(Args(), default))!;
        Assert.Contains("report.md", listed);
        Assert.Contains("shared/notes.md", listed);
        Assert.DoesNotContain("shared/jobs/", listed);
    }

    [Theory]
    [InlineData("shared/jobs/local/s99/evil.md")]
    [InlineData("shared/./jobs/local/s99/evil.md")]
    [InlineData("shared//jobs/local/s99/evil.md")]
    [InlineData("shared\\jobs\\local\\s99\\evil.md")]
    [InlineData("SHARED/JOBS/local/s99/evil.md")]
    [InlineData("shared/jobs")]
    public async Task Shared_prefix_cannot_reach_session_tree(string path)
    {
        var tools = DualRootTools();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            Tool(tools, "ops_doc_write").InvokeAsync(Args(("content", "x"), ("path", path)), default));
        Assert.Contains("会话目录", ex.GetBaseException().Message);
        Assert.False(File.Exists(Path.Combine(_shared, "jobs", "local", "s99", "evil.md")));
    }

    [Fact]
    public async Task Shared_jobs_tree_is_not_listed()
    {
        var secret = Path.Combine(_shared, "jobs", "local", "s99", "secret.md");
        Directory.CreateDirectory(Path.GetDirectoryName(secret)!);
        await File.WriteAllTextAsync(secret, "secret");
        var tools = DualRootTools();
        await Tool(tools, "ops_doc_write").InvokeAsync(Args(("content", "NOTE"), ("path", "shared/notes.md")), default);

        var listed = (string)(await Tool(tools, "ops_doc_list").InvokeAsync(Args(), default))!;
        Assert.Contains("shared/notes.md", listed);
        Assert.DoesNotContain("secret.md", listed);
        Assert.DoesNotContain("shared/jobs/", listed);
    }

    [Fact]
    public async Task Shared_jobs_lookalike_is_allowed()
    {
        var tools = DualRootTools();

        await Tool(tools, "ops_doc_write").InvokeAsync(Args(("content", "x"), ("path", "shared/jobs2/notes.md")), default);
        Assert.True(File.Exists(Path.Combine(_shared, "jobs2", "notes.md")));
    }

    [Fact]
    public async Task Member_can_write_session_but_not_shared()
    {
        Directory.CreateDirectory(_session);
        Directory.CreateDirectory(_shared);
        await File.WriteAllTextAsync(Path.Combine(_shared, "OPS_ASSETS.md"), "ASSET");
        var tools = WorkspaceTools.Create(_session, _shared, sharedWritable: false);

        await Tool(tools, "ops_doc_write").InvokeAsync(Args(("content", "REPORT"), ("path", "report.md")), default);
        Assert.True(File.Exists(Path.Combine(_session, "report.md")));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            Tool(tools, "ops_doc_write").InvokeAsync(Args(("content", "X"), ("path", "shared/notes.md")), default));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            Tool(tools, "ops_doc_write").InvokeAsync(Args(("content", "X")), default));
        Assert.False(File.Exists(Path.Combine(_shared, "notes.md")));
        Assert.Equal("ASSET", await Tool(tools, "ops_doc_read").InvokeAsync(Args(), default));
    }

    [Fact]
    public async Task Admin_can_write_shared()
    {
        Directory.CreateDirectory(_session);
        Directory.CreateDirectory(_shared);
        var tools = WorkspaceTools.Create(_session, _shared, sharedWritable: true);

        await Tool(tools, "ops_doc_write").InvokeAsync(Args(("content", "ASSET")), default);
        Assert.True(File.Exists(Path.Combine(_shared, "OPS_ASSETS.md")));
    }

    [Fact]
    public async Task Session_dir_escape_still_rejected_in_dual_root()
    {
        var tools = DualRootTools();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            Tool(tools, "ops_doc_write").InvokeAsync(Args(("content", "x"), ("path", "../evil.md")), default));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            Tool(tools, "ops_doc_write").InvokeAsync(Args(("content", "x"), ("path", "C:\\Windows\\evil.md")), default));
    }

    [Fact]
    public async Task Single_root_mode_is_unchanged()
    {
        // 未分配会话目录 = 现状行为：一切相对路径落在唯一根，无 shared 语义
        Directory.CreateDirectory(_shared);
        var tools = WorkspaceTools.Create(_shared);

        await Tool(tools, "ops_doc_write").InvokeAsync(Args(("content", "X")), default);
        Assert.True(File.Exists(Path.Combine(_shared, "OPS_ASSETS.md")));

        // 单根下 shared/ 只是普通子目录名（无共享区概念）
        await Tool(tools, "ops_doc_write").InvokeAsync(Args(("content", "Y"), ("path", "shared/notes.md")), default);
        Assert.True(File.Exists(Path.Combine(_shared, "shared", "notes.md")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_session, true); } catch { /* ignore */ }
        try { Directory.Delete(_shared, true); } catch { /* ignore */ }
    }
}
