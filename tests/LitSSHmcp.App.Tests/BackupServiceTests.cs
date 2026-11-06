using System.IO;
using System.IO.Compression;
using LitSSHmcp.App.Services.Backup;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class BackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litssh-backup-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDir;
    private readonly string _target;

    public BackupServiceTests()
    {
        _dataDir = Path.Combine(_root, "data");
        _target = Path.Combine(_root, "backups");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(Path.Combine(_dataDir, "skills"));
        Directory.CreateDirectory(_target);
        File.WriteAllText(Path.Combine(_dataDir, "config.json"), "{\"schemaVersion\":6}");
        File.WriteAllText(Path.Combine(_dataDir, "skills", "a.md"), "skill");
    }

    [Fact]
    public async Task Creates_zip_with_manifest_and_selected_items()
    {
        var plan = new BackupPlanConfig
        {
            Name = "每日",
            TargetDirectory = _target,
            Items = new[] { "config", "skills" },
            KeepLocalCopies = 1
        };
        var service = new BackupService(new FakeUploader(), new DpapiSecretProtector(), _dataDir);

        var result = await service.RunAsync(plan, new AppConfig());

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(result.LocalPath));
        using var archive = ZipFile.OpenRead(result.LocalPath!);
        Assert.Contains(archive.Entries, entry => entry.FullName == "manifest.json");
        Assert.Contains(archive.Entries, entry => entry.FullName == "items/config/config.json");
        Assert.Contains(archive.Entries, entry => entry.FullName == "items/skills/a.md");
    }

    [Fact]
    public async Task Rejects_target_inside_selected_source()
    {
        var plan = new BackupPlanConfig
        {
            Name = "bad",
            TargetDirectory = Path.Combine(_dataDir, "skills", "backups"),
            Items = new[] { "skills" }
        };
        var service = new BackupService(new FakeUploader(), new DpapiSecretProtector(), _dataDir);

        var result = await service.RunAsync(plan, new AppConfig());

        Assert.False(result.Success);
        Assert.Contains("不能位于", result.Message);
    }

    [Fact]
    public async Task Sqlite_items_use_consistent_backup_api()
    {
        var dbPath = Path.Combine(_dataDir, "agent.db");
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE t(v TEXT); INSERT INTO t VALUES ('ok');";
            command.ExecuteNonQuery();
        }

        var plan = new BackupPlanConfig
        {
            Name = "db",
            TargetDirectory = _target,
            Items = new[] { "agent-db" }
        };
        var service = new BackupService(new FakeUploader(), new DpapiSecretProtector(), _dataDir);

        var result = await service.RunAsync(plan, new AppConfig());

        Assert.True(result.Success, result.Message);
        using var archive = ZipFile.OpenRead(result.LocalPath!);
        var entry = Assert.Single(archive.Entries, e => e.FullName == "items/agent-db/agent.db");
        var temp = Path.Combine(_root, "restored.db");
        using (var stream = entry.Open())
        using (var output = File.Create(temp))
            await stream.CopyToAsync(output);

        using var check = new SqliteConnection($"Data Source={temp}");
        check.Open();
        using var read = check.CreateCommand();
        read.CommandText = "SELECT v FROM t";
        Assert.Equal("ok", read.ExecuteScalar());
    }

    [Fact]
    public async Task Encrypted_backup_requires_password_and_restores_to_directory()
    {
        var protector = new DpapiSecretProtector();
        var plan = new BackupPlanConfig
        {
            Name = "encrypted",
            TargetDirectory = _target,
            Items = new[] { "config" },
            EncryptBackup = true,
            BackupPasswordProtected = protector.Protect("backup-pass") ?? string.Empty
        };
        var service = new BackupService(new FakeUploader(), protector, _dataDir);

        var result = await service.RunAsync(plan, new AppConfig());

        Assert.True(result.Success, result.Message);
        Assert.EndsWith(".lsbak", result.LocalPath);
        Assert.True(BackupCrypto.IsEncrypted(result.LocalPath!));
        Assert.Single(BackupHistoryService.List(plan));

        var restore = new BackupRestoreService();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restore.OpenAsync(result.LocalPath!, "wrong-password"));
        using var archive = await restore.OpenAsync(result.LocalPath!, "backup-pass");
        Assert.True(archive.Manifest.Encrypted);
        var restoreDirectory = Path.Combine(_root, "restored");
        var count = await restore.RestoreToDirectoryAsync(archive, restoreDirectory, new[] { "config" });
        Assert.True(count > 0);
        Assert.True(File.Exists(Path.Combine(restoreDirectory, "config", "config.json")));

        var verify = await restore.VerifyAsync(result.LocalPath!, "backup-pass");
        Assert.True(verify.Success, verify.Message);
        Assert.NotNull(verify.Sha256);
    }

    [Fact]
    public async Task Direct_restore_writes_data_directory_and_keeps_safety_copy()
    {
        var plan = new BackupPlanConfig
        {
            Name = "direct",
            TargetDirectory = _target,
            Items = new[] { "config" }
        };
        var service = new BackupService(new FakeUploader(), new DpapiSecretProtector(), _dataDir);
        var backup = await service.RunAsync(plan, new AppConfig());
        Assert.True(backup.Success, backup.Message);
        File.WriteAllText(Path.Combine(_dataDir, "config.json"), "changed");

        var restore = new BackupRestoreService();
        using var archive = await restore.OpenAsync(backup.LocalPath!, null);
        var result = await restore.RestoreToDataDirectoryAsync(archive, new AppConfig(),
            _dataDir, new[] { "config" });

        Assert.True(result.Success, result.Message);
        Assert.Contains("schemaVersion", File.ReadAllText(Path.Combine(_dataDir, "config.json")));
        Assert.NotNull(result.SafetyDirectory);
        Assert.True(Directory.Exists(result.SafetyDirectory));
    }

    [Fact]
    public void Computes_daily_weekly_and_interval_runs()
    {
        var now = new DateTime(2026, 11, 6, 4, 0, 0);
        var daily = new BackupPlanConfig { ScheduleType = BackupScheduleType.Daily, TimeOfDay = "03:00" };
        var weekly = new BackupPlanConfig
        {
            ScheduleType = BackupScheduleType.Weekly,
            TimeOfDay = "03:00",
            WeekDays = new[] { 1 }
        };
        var interval = new BackupPlanConfig { ScheduleType = BackupScheduleType.Interval, IntervalMinutes = 60 };

        Assert.Equal(new DateTime(2026, 11, 7, 3, 0, 0), BackupSchedule.ComputeNextRun(daily, now, null));
        Assert.Equal(new DateTime(2026, 11, 9, 3, 0, 0), BackupSchedule.ComputeNextRun(weekly, now, null));
        Assert.Equal(now.AddMinutes(60), BackupSchedule.ComputeNextRun(interval, now, null));
    }

    [Fact]
    public async Task Uploads_local_zip_to_oss_after_local_backup()
    {
        var protector = new DpapiSecretProtector();
        var uploader = new FakeUploader();
        var plan = new BackupPlanConfig
        {
            Name = "oss",
            TargetDirectory = _target,
            Items = new[] { "config" },
            UploadToOss = true
        };
        var config = new AppConfig();
        config.Backup.Oss = new BackupOssConfig
        {
            Endpoint = "oss.example.com",
            Bucket = "bucket",
            AccessKeyId = "ak",
            AccessKeySecretProtected = protector.Protect("sk") ?? string.Empty
        };
        var service = new BackupService(uploader, protector, _dataDir);

        var result = await service.RunAsync(plan, config);

        Assert.True(result.Success, result.Message);
        Assert.True(result.Uploaded);
        Assert.NotNull(uploader.UploadedPath);
        Assert.StartsWith("litssh-backup/" + plan.Id + "/", uploader.ObjectKey);
    }

    [Fact]
    public void Startup_fallback_requires_a_successful_backup_today()
    {
        Assert.True(BackupScheduler.IsSuccessToday(new BackupState
        {
            LastRunAt = DateTime.Today.AddHours(3),
            LastResult = "ok"
        }));
        Assert.True(BackupScheduler.IsSuccessToday(new BackupState
        {
            LastRunAt = DateTime.Today.AddHours(3),
            LastResult = "oss-fail"
        }));
        Assert.False(BackupScheduler.IsSuccessToday(new BackupState
        {
            LastRunAt = DateTime.Today.AddDays(-1),
            LastResult = "ok"
        }));
        Assert.False(BackupScheduler.IsSuccessToday(new BackupState
        {
            LastRunAt = DateTime.Today.AddHours(3),
            LastResult = "fail:disk"
        }));
    }

    [Fact]
    public void Startup_fallback_respects_scheduled_day_and_time()
    {
        var plan = new BackupPlanConfig
        {
            Name = "weekly",
            Enabled = true,
            FallbackOnStartup = true,
            ScheduleType = BackupScheduleType.Weekly,
            WeekDays = new[] { 2 },
            TimeOfDay = "03:00"
        };

        var monday = new DateTime(2026, 11, 9, 4, 0, 0);
        Assert.False(BackupScheduler.ShouldRunStartupFallback(plan, null, monday));

        var tuesdayBefore = new DateTime(2026, 11, 10, 2, 0, 0);
        Assert.False(BackupScheduler.ShouldRunStartupFallback(plan, null, tuesdayBefore));

        var tuesdayAfter = new DateTime(2026, 11, 10, 4, 0, 0);
        Assert.True(BackupScheduler.ShouldRunStartupFallback(plan, null, tuesdayAfter));
        Assert.False(BackupScheduler.ShouldRunStartupFallback(plan, new BackupState
        {
            LastRunAt = new DateTime(2026, 11, 10, 3, 30, 0),
            LastResult = "ok"
        }, tuesdayAfter));
    }

    [Fact]
    public void Builds_oss_object_key()
    {
        var plan = new BackupPlanConfig { Id = "plan-1" };
        var key = BackupService.BuildObjectKey(new BackupOssConfig { Prefix = "backups/" }, plan, "a.zip");
        Assert.StartsWith("backups/plan-1/", key);
        Assert.EndsWith("/a.zip", key);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private sealed class FakeUploader : IBackupUploader
    {
        public string? UploadedPath { get; private set; }
        public string ObjectKey { get; private set; } = string.Empty;

        public Task UploadAsync(BackupOssConfig config, string accessKeySecret, string localPath,
            string objectKey, CancellationToken ct = default)
        {
            UploadedPath = localPath;
            ObjectKey = objectKey;
            return Task.CompletedTask;
        }

        public Task TestAsync(BackupOssConfig config, string accessKeySecret, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
