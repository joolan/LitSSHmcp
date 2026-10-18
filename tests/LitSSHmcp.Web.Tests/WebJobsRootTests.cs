using LitSSHmcp.Agent;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Web.Sessions;
using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>
/// Web 会话目录固定根（§8.3）：配置解析回退规则 + 旧布局（jobs/jobs）迁移。
/// </summary>
public class WebJobsRootTests
{
    [Fact]
    public void Resolve_empty_or_whitespace_falls_back_to_default()
    {
        var expected = Path.Combine(ConfigPaths.AppDataDir, "jobs");

        Assert.Equal(expected, WebSessionManager.ResolveJobsRoot(null));
        Assert.Equal(expected, WebSessionManager.ResolveJobsRoot(""));
        Assert.Equal(expected, WebSessionManager.ResolveJobsRoot("   "));
    }

    [Fact]
    public void Resolve_absolute_path_is_canonicalized()
    {
        var configured = Path.Combine(Path.GetTempPath(), "litssh-webroot-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(PathGuard.TryCanonicalize(configured), WebSessionManager.ResolveJobsRoot(configured));
    }

    [Fact]
    public void Resolve_relative_path_falls_back_to_default()
    {
        Assert.Equal(Path.Combine(ConfigPaths.AppDataDir, "jobs"),
            WebSessionManager.ResolveJobsRoot("relative\\jobs"));
    }

    [Fact]
    public void Default_root_is_data_dir_jobs()
    {
        Assert.Equal(Path.Combine(ConfigPaths.AppDataDir, WorkspaceLayout.JobsDirName),
            WebSessionManager.DefaultWebJobsRoot);
    }
}

/// <summary>旧 Web 会话目录布局（jobs/jobs/&lt;owner&gt;/s&lt;id&gt;）→ 修正布局的启动迁移。</summary>
public class LegacyJobsLayoutMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litssh-migrate-" + Guid.NewGuid().ToString("N"));
    private readonly ContextStore _store;

    public LegacyJobsLayoutMigrationTests()
    {
        Directory.CreateDirectory(_root);
        _store = new ContextStore(Path.Combine(_root, "agent.db"));
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Moves_legacy_dirs_and_updates_rows_idempotently()
    {
        var id = await _store.CreateSessionAsync("旧会话");
        await _store.SetSessionOwnerUserIdAsync(id, "1");
        var legacyDir = Path.Combine(_root, "jobs", "1", "s" + id);
        Directory.CreateDirectory(legacyDir);
        var file = Path.Combine(legacyDir, "out.txt");
        await File.WriteAllTextAsync(file, "产出");
        await _store.SetSessionWorkspaceDirAsync(id, legacyDir);

        var migrated = await LegacyJobsLayoutMigration.RunAsync(_store, _root);

        Assert.Equal(1, migrated);
        var expected = Path.Combine(Path.GetFullPath(_root), "1", "s" + id);
        Assert.Equal(expected, await _store.GetSessionWorkspaceDirAsync(id));
        Assert.True(File.Exists(Path.Combine(expected, "out.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, "jobs")));            // 空旧根被清理
        Assert.Equal(0, await LegacyJobsLayoutMigration.RunAsync(_store, _root)); // 幂等
    }

    [Fact]
    public async Task Leaves_current_layout_and_foreign_dirs_untouched()
    {
        var id = await _store.CreateSessionAsync("新会话");
        var currentDir = Path.Combine(_root, "2", "s" + id);
        Directory.CreateDirectory(currentDir);
        await _store.SetSessionWorkspaceDirAsync(id, currentDir);

        var desktopId = await _store.CreateSessionAsync("桌面会话");
        var desktopDir = Path.Combine(_root, "desktop-jobs", "local", "s" + desktopId);
        Directory.CreateDirectory(desktopDir);
        await _store.SetSessionWorkspaceDirAsync(desktopId, desktopDir);

        var migrated = await LegacyJobsLayoutMigration.RunAsync(_store, _root);

        Assert.Equal(0, migrated);
        Assert.Equal(currentDir, await _store.GetSessionWorkspaceDirAsync(id));
        Assert.Equal(desktopDir, await _store.GetSessionWorkspaceDirAsync(desktopId));
        Assert.True(Directory.Exists(currentDir));
        Assert.True(Directory.Exists(desktopDir));
    }

    [Fact]
    public async Task No_legacy_tree_is_a_noop()
    {
        Assert.Equal(0, await LegacyJobsLayoutMigration.RunAsync(_store, _root));
    }
}
