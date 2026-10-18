using LitSSHmcp.Agent;
using LitSSHmcp.Core.Services.Security;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

/// <summary>
/// Web 固定根口径（&lt;jobsRoot&gt;/&lt;owner&gt;/s&lt;id&gt;）的目录解析/删除安全边界，
/// 以及孤儿会话目录清扫（评估报告 §8.2/§8.3）。
/// </summary>
public class SessionDirLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litssh-jobs-" + Guid.NewGuid().ToString("N"));

    public SessionDirLayoutTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void Resolve_in_jobs_root_builds_owner_session_and_sanitizes_owner()
    {
        var dir = WorkspaceLayout.ResolveSessionDirInJobsRoot(_root, "web-user-2", 42);

        Assert.Equal(Path.Combine(Path.GetFullPath(_root), "web-user-2", "s42"), dir);

        var evil = WorkspaceLayout.ResolveSessionDirInJobsRoot(_root, "../..", 7);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(_root), WorkspaceLayout.SanitizeSegment("../..", "local"), "s7"),
            evil);
        Assert.True(PathGuard.IsUnder(Path.GetFullPath(_root), evil));   // 消毒后仍在根内
    }

    [Fact]
    public void TryParseSessionId_accepts_only_s_positive_number()
    {
        Assert.True(WorkspaceLayout.TryParseSessionId("s12", out var id));
        Assert.Equal(12, id);
        Assert.False(WorkspaceLayout.TryParseSessionId("s0", out _));
        Assert.False(WorkspaceLayout.TryParseSessionId("s1x", out _));
        Assert.False(WorkspaceLayout.TryParseSessionId("s", out _));
        Assert.False(WorkspaceLayout.TryParseSessionId("12", out _));
        Assert.False(WorkspaceLayout.TryParseSessionId(null, out _));
    }

    [Fact]
    public void TryRemove_in_jobs_root_removes_session_dir_only()
    {
        var sessionDir = WorkspaceLayout.ResolveSessionDirInJobsRoot(_root, "1", 5);
        Directory.CreateDirectory(Path.Combine(sessionDir, "attachments"));
        File.WriteAllText(Path.Combine(sessionDir, "attachments", "a.txt"), "x");

        var (removed, files, error) = WorkspaceLayout.TryRemoveSessionDirInJobsRoot(_root, sessionDir);

        Assert.True(removed);
        Assert.Equal(1, files);
        Assert.Null(error);
        Assert.False(Directory.Exists(sessionDir));
    }

    [Fact]
    public void TryRemove_in_jobs_root_rejects_root_owner_and_outside()
    {
        var ownerDir = Path.Combine(Path.GetFullPath(_root), "1");
        var sessionDir = Path.Combine(ownerDir, "s5");
        Directory.CreateDirectory(sessionDir);
        var outside = Path.Combine(Path.GetTempPath(), "litssh-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);

        try
        {
            Assert.False(WorkspaceLayout.TryRemoveSessionDirInJobsRoot(_root, _root).Removed);          // jobs 根本身
            Assert.False(WorkspaceLayout.TryRemoveSessionDirInJobsRoot(_root, ownerDir).Removed);       // 整个 owner 目录
            Assert.False(WorkspaceLayout.TryRemoveSessionDirInJobsRoot(_root, outside).Removed);        // 根外目录

            Assert.True(Directory.Exists(ownerDir));
            Assert.True(Directory.Exists(outside));
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Sweep_removes_only_old_orphan_session_dirs()
    {
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var oldAlive = CreateSessionDir("1", 11);      // 存活（无论多旧都保留）
        var oldOrphan = CreateSessionDir("1", 12);     // 孤儿且旧 → 删
        var freshOrphan = CreateSessionDir("2", 13);   // 孤儿但新 → 保留
        Directory.SetLastWriteTimeUtc(oldOrphan, now.AddDays(-40));
        Directory.SetLastWriteTimeUtc(oldAlive, now.AddDays(-40));

        var (removedDirs, _, errors) = WorkspaceLayout.SweepOrphanSessionDirs(_root, 30, new long[] { 11 }, now);

        Assert.Equal(1, removedDirs);
        Assert.Empty(errors);
        Assert.True(Directory.Exists(oldAlive));
        Assert.False(Directory.Exists(oldOrphan));
        Assert.True(Directory.Exists(freshOrphan));
    }

    [Fact]
    public void Sweep_is_noop_when_retention_disabled_or_root_missing()
    {
        var dir = CreateSessionDir("1", 21);
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddDays(-100));

        Assert.Equal(0, WorkspaceLayout.SweepOrphanSessionDirs(_root, 0, Array.Empty<long>()).RemovedDirs);
        Assert.True(Directory.Exists(dir));

        var missing = Path.Combine(_root, "nope");
        Assert.Equal(0, WorkspaceLayout.SweepOrphanSessionDirs(missing, 30, Array.Empty<long>()).RemovedDirs);
    }

    [Fact]
    public void Sweep_ignores_non_session_entries_and_removes_empty_owner_dirs()
    {
        var orphan = CreateSessionDir("1", 31);
        var notSession = Path.Combine(Path.GetFullPath(_root), "1", "keepme");
        Directory.CreateDirectory(notSession);
        File.WriteAllText(Path.Combine(notSession, "f.txt"), "data");
        Directory.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-100));

        var (removedDirs, _, _) = WorkspaceLayout.SweepOrphanSessionDirs(
            _root, 30, Array.Empty<long>(), DateTime.UtcNow);

        Assert.Equal(1, removedDirs);
        Assert.False(Directory.Exists(orphan));
        Assert.True(Directory.Exists(notSession));   // 非 s<id> 目录不动
        Assert.True(Directory.Exists(Path.Combine(Path.GetFullPath(_root), "1"))); // 非空 owner 目录保留
    }

    private string CreateSessionDir(string owner, long sessionId)
    {
        var dir = WorkspaceLayout.ResolveSessionDirInJobsRoot(_root, owner, sessionId);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
