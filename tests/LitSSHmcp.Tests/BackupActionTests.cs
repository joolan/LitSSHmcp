using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Trigger;
using Xunit;

namespace LitSSHmcp.Tests;

public class BackupActionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litssh-backup-" + Guid.NewGuid().ToString("N"));

    public BackupActionTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "sub"));
        File.WriteAllText(Path.Combine(_root, "src", "a.txt"), "内容 A");
        File.WriteAllText(Path.Combine(_root, "src", "sub", "b.txt"), "内容 B");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    private static TriggerActionContext Ctx(TriggerConfig trigger, TriggerAction action) => new()
    {
        Trigger = trigger,
        Action = action,
        Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["trigger"] = trigger.Name }
    };

    [Fact]
    public async Task Local_zip_backup_creates_archive()
    {
        var action = new TriggerAction
        {
            Type = TriggerActionType.Backup,
            SourcePath = Path.Combine(_root, "src"),
            DestDir = Path.Combine(_root, "out"),
            BackupFormat = TriggerBackupFormat.Zip
        };

        var r = await BackupAction.RunAsync(Ctx(new TriggerConfig { Name = "my trigger" }, action));

        Assert.True(r.Success, r.Error);
        var files = Directory.GetFiles(Path.Combine(_root, "out"), "my-trigger-*.zip");
        var file = Assert.Single(files);
        Assert.True(new FileInfo(file).Length > 0);
        Assert.Contains(file, r.Output);

        // 内容完整（含子目录）
        using var zip = System.IO.Compression.ZipFile.OpenRead(file);
        Assert.Equal(2, zip.Entries.Count);
    }

    [Fact]
    public async Task Dest_inside_source_is_rejected()
    {
        var action = new TriggerAction
        {
            Type = TriggerActionType.Backup,
            SourcePath = Path.Combine(_root, "src"),
            DestDir = Path.Combine(_root, "src", "nested-out")
        };

        var r = await BackupAction.RunAsync(Ctx(new TriggerConfig { Name = "t" }, action));

        Assert.False(r.Success);
        Assert.Contains("源目录内", r.Error);
    }

    [Fact]
    public async Task Missing_source_fails()
    {
        var action = new TriggerAction
        {
            Type = TriggerActionType.Backup,
            SourcePath = Path.Combine(_root, "nope"),
            DestDir = Path.Combine(_root, "out")
        };

        var r = await BackupAction.RunAsync(Ctx(new TriggerConfig { Name = "t" }, action));

        Assert.False(r.Success);
        Assert.Contains("不存在", r.Error);
    }

    [Fact]
    public async Task Retention_removes_oldest_backups()
    {
        var outDir = Path.Combine(_root, "out");
        Directory.CreateDirectory(outDir);
        // 两个旧备份 + 本次新备份，RetentionCount=1 只留最新
        File.WriteAllText(Path.Combine(outDir, "ret-20200101-000000.zip"), "old1");
        File.WriteAllText(Path.Combine(outDir, "ret-20210101-000000.zip"), "old2");

        var action = new TriggerAction
        {
            Type = TriggerActionType.Backup,
            SourcePath = Path.Combine(_root, "src"),
            DestDir = outDir,
            BackupFormat = TriggerBackupFormat.Zip,
            RetentionCount = 1
        };

        var r = await BackupAction.RunAsync(Ctx(new TriggerConfig { Name = "ret" }, action));

        Assert.True(r.Success, r.Error);
        Assert.Single(Directory.GetFiles(outDir, "ret-*.zip"));
        Assert.Contains("已清理", r.Output);
    }
}

public class GitActionTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "litssh-git-" + Guid.NewGuid().ToString("N"));

    public GitActionTests()
    {
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch { /* 忽略 */ }
    }

    private static async Task<bool> GitAvailableAsync()
    {
        var r = await LocalProcessRunner.RunAsync("git", new[] { "--version" }, 15);
        return r.Success;
    }

    private async Task InitRepoAsync()
    {
        await LocalProcessRunner.RunAsync("git", new[] { "init" }, 30, default, _repo);
        await LocalProcessRunner.RunAsync("git", new[] { "config", "user.email", "trigger-test@local" }, 15, default, _repo);
        await LocalProcessRunner.RunAsync("git", new[] { "config", "user.name", "trigger-test" }, 15, default, _repo);
    }

    private static TriggerActionContext Ctx(TriggerAction action) => new()
    {
        Trigger = new TriggerConfig { Name = "git触发事件" },
        Action = action,
        Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["trigger"] = "git触发事件",
            ["timestamp"] = "2026-10-09 12:00:00"
        }
    };

    [Fact]
    public async Task Commit_then_skip_when_no_changes()
    {
        if (!await GitAvailableAsync())
            return; // 机器无 git 时跳过（不视为失败）

        await InitRepoAsync();
        File.WriteAllText(Path.Combine(_repo, "file.txt"), "v1");

        var action = new TriggerAction
        {
            Type = TriggerActionType.GitCommitPush,
            GitDir = _repo,
            GitOperation = TriggerGitOperation.CommitOnly
        };

        var first = await GitAction.RunAsync(Ctx(action));
        Assert.True(first.Success, first.Error);
        Assert.Contains("自动提交", first.Output);

        var second = await GitAction.RunAsync(Ctx(action));
        Assert.True(second.Success, second.Error);
        Assert.Contains("无变更", second.Output);
    }

    [Fact]
    public async Task Push_failure_is_reported()
    {
        if (!await GitAvailableAsync())
            return;

        await InitRepoAsync();
        File.WriteAllText(Path.Combine(_repo, "file.txt"), "v1");

        var action = new TriggerAction
        {
            Type = TriggerActionType.GitCommitPush,
            GitDir = _repo,
            GitOperation = TriggerGitOperation.CommitAndPush,
            GitPushRemote = "no-such-remote"
        };

        var r = await GitAction.RunAsync(Ctx(action));
        Assert.False(r.Success);
        Assert.Contains("git push", r.Error);
    }

    [Fact]
    public void Shell_quote_escapes_single_quotes()
    {
        Assert.Equal("'plain'", GitAction.ShellQuote("plain"));
        Assert.Equal("'a'\\''b'", GitAction.ShellQuote("a'b"));
        Assert.Equal("'with space'", GitAction.ShellQuote("with space"));
    }
}
