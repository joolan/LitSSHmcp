using LitSSHmcp.Core.Services.Security;
using Xunit;

namespace LitSSHmcp.Tests;

/// <summary>M1-5: 会话工作目录上下文（宿主 spawn 注入）与工具级默认落地解析（评估报告 §5.2）。</summary>
public class SessionWorkspaceTests : IDisposable
{
    public SessionWorkspaceTests() => SessionWorkspace.Current = null;

    public void Dispose() => SessionWorkspace.Current = null;

    [Fact]
    public void SetCurrent_accepts_absolute_and_rejects_blank()
    {
        SessionWorkspace.SetCurrent(null);
        Assert.Null(SessionWorkspace.Current);

        SessionWorkspace.SetCurrent("   ");
        Assert.Null(SessionWorkspace.Current);

        var dir = Path.Combine(Path.GetTempPath(), "litssh-sessionws-" + Guid.NewGuid().ToString("N"));
        SessionWorkspace.SetCurrent(dir);
        Assert.Equal(Path.GetFullPath(dir), SessionWorkspace.Current);
    }

    [Fact]
    public void ResolveLanding_without_session_dir_requires_explicit_path()
    {
        // 未注入会话目录（旧会话/直连）：省略 → 无法落地；相对/绝对原样（维持现状 CWD 语义）
        Assert.Null(SessionWorkspace.ResolveLanding(null, "/var/log/app.log", isDirectory: false));
        Assert.Null(SessionWorkspace.ResolveLanding("  ", "/var/log/", isDirectory: true));
        Assert.Equal("sub/a.log", SessionWorkspace.ResolveLanding("sub/a.log", "/var/log/app.log", isDirectory: false));
        Assert.Equal(@"C:\x\a.log", SessionWorkspace.ResolveLanding(@"C:\x\a.log", "/var/log/app.log", isDirectory: false));
    }

    [Fact]
    public void ResolveLanding_with_session_dir_defaults_and_resolves_relative()
    {
        var session = Path.Combine(Path.GetTempPath(), "litssh-landing-" + Guid.NewGuid().ToString("N"));

        // 省略 = 会话目录 + 远端文件名（POSIX 路径）
        Assert.Equal(Path.Combine(session, "app.log"),
            SessionWorkspace.ResolveLanding(null, "/var/log/app.log", isDirectory: false, sessionDir: session));
        // 批量下载省略 = 会话目录本身
        Assert.Equal(session,
            SessionWorkspace.ResolveLanding(null, "/var/log/a.log", isDirectory: true, sessionDir: session));
        // 相对路径 = 会话目录之下（工具级默认落地, 非提示词约定）
        Assert.Equal(Path.Combine(session, "sub", "y.log"),
            SessionWorkspace.ResolveLanding("sub/y.log", "/var/log/y.log", isDirectory: false, sessionDir: session));
        // 绝对路径原样（照旧过白名单）
        Assert.Equal(@"C:\Anywhere\a.log",
            SessionWorkspace.ResolveLanding(@"C:\Anywhere\a.log", "/var/log/a.log", isDirectory: false, sessionDir: session));
    }

    [Fact]
    public void WithAllowedRoots_delegates_to_conditional_injection()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = @"C:\Users\test\Desktop";
        SessionWorkspace.Current = null;
        Assert.Equal(new[] { root }, SessionWorkspace.WithAllowedRoots(new[] { root }));

        SessionWorkspace.Current = @"C:\Windows\jobs\local\s1";   // 越界 → 不注入
        Assert.DoesNotContain(SessionWorkspace.Current, SessionWorkspace.WithAllowedRoots(new[] { root }));
    }
}
