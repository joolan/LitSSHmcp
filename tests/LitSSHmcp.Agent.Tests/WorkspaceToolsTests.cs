using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class WorkspaceToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "litssh-ws-" + Guid.NewGuid().ToString("N"));

    private static IAgentTool Tool(IReadOnlyList<IAgentTool> tools, string name) => tools.First(t => t.Name == name);

    private static Dictionary<string, object?> Args(params (string, object?)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public void ResolvePath_rejects_escape()
    {
        var root = Path.GetFullPath(_dir);
        Directory.CreateDirectory(root);

        Assert.Throws<InvalidOperationException>(() => WorkspaceTools.ResolvePath(root, "../evil.md"));
        Assert.Throws<InvalidOperationException>(() => WorkspaceTools.ResolvePath(root, "C:\\Windows\\evil.md"));
    }

    [Fact]
    public async Task Write_read_list_roundtrip()
    {
        var root = Path.GetFullPath(_dir);
        Directory.CreateDirectory(root);
        var tools = WorkspaceTools.Create(root);

        var write = await Tool(tools, "ops_doc_write").InvokeAsync(Args(("content", "hello-ops"), ("path", "OPS_ASSETS.md")), default);
        Assert.Contains("OPS_ASSETS.md", (string)write!);

        var read = await Tool(tools, "ops_doc_read").InvokeAsync(Args(("path", "OPS_ASSETS.md")), default);
        Assert.Equal("hello-ops", read);

        var listed = await Tool(tools, "ops_doc_list").InvokeAsync(Args(), default);
        Assert.Contains("OPS_ASSETS.md", (string)listed!);
        Assert.Contains("hello-ops", File.ReadAllText(Path.Combine(root, "OPS_ASSETS.md")));
    }

    [Fact]
    public async Task Read_defaults_to_ops_assets()
    {
        var root = Path.GetFullPath(_dir);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "OPS_ASSETS.md"), "DEFAULT_DOC");

        var tools = WorkspaceTools.Create(root);
        var read = await Tool(tools, "ops_doc_read").InvokeAsync(Args(), default);

        Assert.Equal("DEFAULT_DOC", read);
    }

    [Fact]
    public void ResolveDir_defaults_to_documents_when_empty()
    {
        var dir = WorkspaceTools.ResolveDir(null);
        Assert.Contains("LitSSH", dir);
        Assert.Equal(@"C:\custom", WorkspaceTools.ResolveDir(@"C:\custom"));
    }

    [Fact]
    public async Task Append_and_patch_update_incrementally()
    {
        var root = Path.GetFullPath(_dir);
        Directory.CreateDirectory(root);
        var tools = WorkspaceTools.Create(root);

        var append1 = await Tool(tools, "ops_doc_append").InvokeAsync(Args(("content", "line1")), default);
        Assert.Contains("已追加", (string)append1!);
        var append2 = await Tool(tools, "ops_doc_append").InvokeAsync(Args(("content", "line2")), default);
        Assert.Contains("已追加", (string)append2!);
        Assert.Equal("line1" + Environment.NewLine + "line2", File.ReadAllText(Path.Combine(root, "OPS_ASSETS.md")));

        var patch = await Tool(tools, "ops_doc_patch").InvokeAsync(Args(("find", "line1"), ("replace", "LINE-1")), default);
        Assert.Contains("已更新", (string)patch!);
        Assert.StartsWith("LINE-1", File.ReadAllText(Path.Combine(root, "OPS_ASSETS.md")));

        var miss = await Tool(tools, "ops_doc_patch").InvokeAsync(Args(("find", "nope"), ("replace", "x")), default);
        Assert.Contains("未找到", (string)miss!);
    }

    [Fact]
    public void ResolvePath_rejects_junction_escape_but_allows_internal_links()
    {
        var root = Path.GetFullPath(_dir);
        Directory.CreateDirectory(root);
        var outside = Path.Combine(Path.GetTempPath(), "litssh-ws-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var evilLink = Path.Combine(root, "evil");
        var innerReal = Directory.CreateDirectory(Path.Combine(root, "real")).FullName;
        var innerAlias = Path.Combine(root, "alias");

        try
        {
            if (!TryCreateDirLink(evilLink, outside) || !TryCreateDirLink(innerAlias, innerReal))
                return;    // 环境不允许创建链接：跳过

            // 字面在工作区内、物理指向区外 → 拒绝（评估报告 §10.C.3）
            Assert.Throws<InvalidOperationException>(() => WorkspaceTools.ResolvePath(root, "evil/secret.md"));
            // 指向工作区内的链接 → 放行（防护不误伤）
            var ok = WorkspaceTools.ResolvePath(root, "alias/x.md");
            Assert.StartsWith(root, ok, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteLink(evilLink);
            DeleteLink(innerAlias);
            try { Directory.Delete(outside, true); } catch { /* ignore */ }
        }
    }

    private static bool TryCreateDirLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p is null) return false;
                p.WaitForExit(5000);
                return p.ExitCode == 0 && Directory.Exists(link);
            }
            catch
            {
                return false;
            }
        }

        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteLink(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c rmdir \"{path}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(5000);
            }
            else
            {
                Directory.Delete(path, false);
            }
        }
        catch
        {
            /* ignore */
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }
}
