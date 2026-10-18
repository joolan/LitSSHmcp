using LitSSHmcp.Agent;
using LitSSHmcp.Core.Services.Security;

namespace LitSSHmcp.Web.Sessions;

/// <summary>
/// 旧版 Web 会话目录布局迁移：早期版本把固定根（<c>%APPDATA%\LitSSH\jobs</c>）当 sharedRoot 再套一层
/// <c>jobs/</c>，实际落盘为 <c>jobs\jobs\&lt;用户&gt;\s&lt;id&gt;</c>；已修正为
/// <c>jobs\&lt;用户&gt;\s&lt;id&gt;</c>（评估报告 §8.3）。启动时把库内旧路径的目录平移并更新库行，幂等。
/// </summary>
public static class LegacyJobsLayoutMigration
{
    /// <summary>执行迁移，返回更新的会话行数（无旧布局时为 0）。</summary>
    public static async Task<int> RunAsync(IContextStore store, string jobsRoot, CancellationToken ct = default)
    {
        var root = PathGuard.TryCanonicalize(jobsRoot);
        if (root is null)
            return 0;
        var legacyRoot = Path.Combine(root, WorkspaceLayout.JobsDirName);
        if (!Directory.Exists(legacyRoot))
            return 0;

        var migrated = 0;
        foreach (var row in await store.ListSessionsAsync(ct))
        {
            var dir = await store.GetSessionWorkspaceDirAsync(row.Id, ct);
            if (string.IsNullOrWhiteSpace(dir) || !PathGuard.IsUnder(legacyRoot, dir))
                continue;

            var relative = Path.GetRelativePath(legacyRoot, dir);
            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!PathGuard.IsUnder(root, target) || string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
                continue;

            if (Directory.Exists(dir))
            {
                try
                {
                    if (!Directory.Exists(target))
                    {
                        var parent = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(parent))
                            Directory.CreateDirectory(parent);
                        Directory.Move(dir, target);
                    }
                }
                catch
                {
                    continue;   // 移动失败保留旧行，下次启动重试
                }
            }

            await store.SetSessionWorkspaceDirAsync(row.Id, target, ct);
            migrated++;
        }

        TryDeleteEmptyTree(legacyRoot);
        return migrated;
    }

    /// <summary>自底向上删除空目录（含 <c>jobs/jobs/&lt;owner&gt;</c>）；非空即停止，不碰任何文件。</summary>
    private static void TryDeleteEmptyTree(string dir)
    {
        try
        {
            foreach (var child in Directory.EnumerateDirectories(dir))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    return;
                TryDeleteEmptyTree(child);
            }
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir, recursive: false);
        }
        catch
        {
            // 清理失败不影响启动
        }
    }
}
