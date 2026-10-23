using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using LitSSHmcp.Agent;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

/// <summary>
/// 本机数据目录维护：统计各存储占用，清理 Memory 历史、过期 spill 与孤儿会话目录。
/// 只处理本机 AppData；Web/MCP 各自的运行目录仍按各自配置管理。
/// </summary>
public class StorageSettingsViewModel : INotifyPropertyChanged, IConfigReloadable
{
    private readonly IConfigService _configService;
    private readonly string _dataDir;

    private string _statusMessage = string.Empty;
    private string _migrationStatus = string.Empty;
    private bool _busy;

    public StorageSettingsViewModel(IConfigService? configService = null, string? dataDir = null)
    {
        _configService = configService ?? AppServiceFactory.CreateConfigService();
        _dataDir = dataDir ?? ConfigPaths.AppDataDir;
    }

    /// <summary>本机数据目录（%APPDATA%\LitSSH 或 LITSSH_DATA_DIR 覆盖）。</summary>
    public string DataDir => _dataDir;

    public ObservableCollection<StorageStat> Stats { get; } = new();

    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    /// <summary>配置/数据库格式版本与自动迁移状态（旧库在启动/首次使用时自动迁移）。</summary>
    public string MigrationStatus
    {
        get => _migrationStatus;
        private set { _migrationStatus = value; OnPropertyChanged(); }
    }

    public bool Busy
    {
        get => _busy;
        private set { _busy = value; OnPropertyChanged(); }
    }

    /// <summary>重新读取配置并统计占用（目录遍历放到后台线程，避免卡 UI）。</summary>
    public void ReloadConfig() => _ = RefreshAsync();

    public async Task RefreshAsync()
    {
        if (Busy)
            return;
        Busy = true;
        try
        {
            var config = await _configService.LoadConfigAsync();
            var agent = config.Agent ?? new AgentConfig();
            MigrationStatus = $"配置 schema v{config.SchemaVersion}/{AppConfig.CurrentSchemaVersion}；" +
                              "AI 会话/记忆与审计库在启动或首次使用时自动补齐列/迁移，无需手工操作。";
            var spillDir = string.IsNullOrWhiteSpace(agent.SpillDir)
                ? Path.Combine(_dataDir, "spills")
                : agent.SpillDir;
            var workspaceRoot = WorkspaceTools.ResolveDir(agent.WorkspaceDir);
            var jobsDir = Path.Combine(workspaceRoot, WorkspaceLayout.JobsDirName);

            var stats = await Task.Run(() => new List<StorageStat>
            {
                Stat("数据目录合计", _dataDir),
                Stat("配置 config.json", Path.Combine(_dataDir, "config.json")),
                Stat("AI 会话/记忆 agent.db", Path.Combine(_dataDir, "agent.db"), includeSidecars: true),
                Stat("审计 audit.db", Path.Combine(_dataDir, "audit.db"), includeSidecars: true),
                Stat("快照 snapshots.db", Path.Combine(_dataDir, "snapshots.db"), includeSidecars: true),
                Stat("大结果 spill", spillDir),
                Stat("工作区 jobs", jobsDir),
                Stat("日志", Path.Combine(_dataDir, "logs")),
                Stat("技能", Path.Combine(_dataDir, "skills"))
            });

            Stats.Clear();
            foreach (var stat in stats)
                Stats.Add(stat);
            StatusMessage = $"统计完成（{DateTime.Now:HH:mm:ss}）";
        }
        catch (Exception ex)
        {
            StatusMessage = "统计失败: " + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>清理全部 Memory 历史快照，每记忆保留最近 40 版并清掉 30 天前的孤儿快照。</summary>
    public async Task PruneMemoryHistoryAsync()
    {
        try
        {
            var removed = await new AgentMemoryStore(Path.Combine(_dataDir, "agent.db"))
                .PruneHistoryAsync(AgentMemoryStore.DefaultHistoryKeep);
            await RefreshAsync();
            StatusMessage = $"已清理 {removed} 个旧记忆快照（每记忆保留最近 {AgentMemoryStore.DefaultHistoryKeep} 版）";
        }
        catch (Exception ex)
        {
            StatusMessage = "清理记忆历史失败: " + ex.Message;
        }
    }

    /// <summary>按 agent.spillRetentionDays 清理过期的大结果文件（仅默认 spills 根目录，不递归子目录）。</summary>
    public async Task PruneSpillsAsync()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var agent = config.Agent ?? new AgentConfig();
            if (agent.SpillRetentionDays <= 0)
            {
                StatusMessage = "未启用 spill 保留期（agent.spillRetentionDays ≤ 0），未清理。";
                return;
            }
            var dir = string.IsNullOrWhiteSpace(agent.SpillDir)
                ? Path.Combine(_dataDir, "spills")
                : agent.SpillDir;
            _ = new SpillStore(dir, agent.SpillRetentionDays);   // 构造函数内按保留期清理
            await RefreshAsync();
            StatusMessage = $"已按 {agent.SpillRetentionDays} 天保留期清理过期 spill。";
        }
        catch (Exception ex)
        {
            StatusMessage = "清理 spill 失败: " + ex.Message;
        }
    }

    /// <summary>按 audit.retentionDays 清理审计历史归档，并重建剩余记录的哈希链。</summary>
    public async Task PruneAuditArchiveAsync()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var days = config.Security?.Audit?.RetentionDays ?? 0;
            if (days <= 0)
            {
                StatusMessage = "审计保留期为 0（永久保留），未清理归档。";
                return;
            }

            var cutoff = DateTime.UtcNow.AddDays(-days);
            var removed = await new AuditLogService(null, Path.Combine(_dataDir, "audit.db"))
                .PruneHistoryAsync(cutoff);
            await RefreshAsync();
            StatusMessage = removed == 0
                ? $"没有超过 {days} 天的审计归档需要清理。"
                : $"已清理 {removed} 条超过 {days} 天的审计归档；剩余记录的哈希链已重建。";
        }
        catch (Exception ex)
        {
            StatusMessage = "清理审计归档失败: " + ex.Message;
        }
    }

    /// <summary>按 agent.orphanSessionDirRetentionDays 清理工作区中已无会话引用的 jobs 目录。</summary>
    public async Task PruneOrphanSessionsAsync()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var agent = config.Agent ?? new AgentConfig();
            if (agent.OrphanSessionDirRetentionDays <= 0)
            {
                StatusMessage = "未启用孤儿会话目录清理（agent.orphanSessionDirRetentionDays ≤ 0）。";
                return;
            }

            var sharedRoot = WorkspaceTools.ResolveDir(agent.WorkspaceDir);
            var store = new ContextStore();
            await store.InitializeAsync();
            var alive = (await store.ListSessionsAsync()).Select(s => s.Id).ToArray();
            var result = WorkspaceLayout.SweepOrphanSessionDirs(
                sharedRoot, agent.OrphanSessionDirRetentionDays, alive);

            await RefreshAsync();
            var errors = result.Errors.Length == 0 ? string.Empty : $"；{result.Errors.Length} 个错误";
            StatusMessage = $"已清理 {result.RemovedDirs} 个孤儿会话目录、{result.Files} 个文件{errors}。";
        }
        catch (Exception ex)
        {
            StatusMessage = "清理孤儿会话目录失败: " + ex.Message;
        }
    }

    private static StorageStat Stat(string name, string path, bool includeSidecars = false)
    {
        long bytes = 0;
        try
        {
            if (File.Exists(path))
            {
                bytes += new FileInfo(path).Length;
            }
            else if (Directory.Exists(path))
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                foreach (var file in Directory.EnumerateFiles(path, "*", options))
                {
                    try { bytes += new FileInfo(file).Length; } catch { /* 单个文件不可读忽略 */ }
                }
            }

            if (includeSidecars)
            {
                foreach (var suffix in new[] { "-wal", "-shm" })
                {
                    var sidecar = path + suffix;
                    if (File.Exists(sidecar))
                        bytes += new FileInfo(sidecar).Length;
                }
            }
        }
        catch
        {
            // 占用统计失败不影响其它条目
        }
        return new StorageStat(name, path, bytes);
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.##} MB",
        >= 1024L => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes} B"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>一项本机存储占用（名称、路径与格式化大小）。</summary>
public sealed record StorageStat(string Name, string Path, long Bytes)
{
    public string SizeText => StorageSettingsViewModel.FormatBytes(Bytes);
}
