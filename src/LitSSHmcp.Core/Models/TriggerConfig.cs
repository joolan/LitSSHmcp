using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace LitSSHmcp.Core.Models;

/// <summary>触发事件执行目标：本机或远程服务器（动作在其上执行）。</summary>
public enum TriggerTarget
{
    Local,
    Remote
}

/// <summary>触发事件动作类型：git 提交推送 / webhook 通知 / 目录备份 / 数据库备份。</summary>
public enum TriggerActionType
{
    GitCommitPush,
    Webhook,
    Backup,
    DatabaseBackup
}

/// <summary>git 动作：仅提交 / 提交并推送。</summary>
public enum TriggerGitOperation
{
    CommitOnly,
    CommitAndPush
}

/// <summary>webhook 载荷模板：通用 JSON / 钉钉 / 企业微信 / 飞书。</summary>
public enum TriggerWebhookTemplate
{
    Generic,
    DingTalk,
    WeCom,
    Feishu
}

/// <summary>备份压缩格式。</summary>
public enum TriggerBackupFormat
{
    Zip,
    TarGz
}

/// <summary>触发原因（进入 payload 与日志）。</summary>
public enum TriggerRunCause
{
    Manual,
    Schedule,
    Sync
}

/// <summary>流水线中的一步动作（扁平单类：按 <see cref="Type"/> 只使用对应字段组）。</summary>
public class TriggerAction : INotifyPropertyChanged
{
    public TriggerActionType Type { get; set; } = TriggerActionType.GitCommitPush;

    /// <summary>该步失败后是否继续执行后续动作（webhook 报失败通常设 true）。</summary>
    public bool ContinueOnError { get; set; }

    // ---- Git ----

    private string _gitDir = string.Empty;

    /// <summary>git 仓库目录（本机路径或远程绝对路径）。</summary>
    public string GitDir { get => _gitDir; set => Set(ref _gitDir, value); }

    /// <summary>提交信息模板（支持 {{trigger}}/{{timestamp}}/{{date}}/{{cause}}/{{result}} 等变量）。</summary>
    public string GitMessageTemplate { get; set; } = "[{{timestamp}}] 触发事件「{{trigger}}」自动提交";

    /// <summary>提交前 git add -A（false=仅暂存已有变更，由仓库自身暂存区决定）。</summary>
    public bool GitCommitAll { get; set; } = true;

    public TriggerGitOperation GitOperation { get; set; } = TriggerGitOperation.CommitAndPush;

    /// <summary>推送远端名（CommitAndPush 时使用）。</summary>
    public string GitPushRemote { get; set; } = "origin";

    /// <summary>推送分支（空 = 当前分支）。</summary>
    public string GitPushBranch { get; set; } = string.Empty;

    // ---- Webhook ----

    public string Url { get; set; } = string.Empty;

    public TriggerWebhookTemplate WebhookTemplate { get; set; } = TriggerWebhookTemplate.Generic;

    /// <summary>自定义请求头（Generic 常用于 Authorization 等）。</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Generic 模板的请求体（支持 {{变量}}；为空使用内置默认 JSON 载荷）。</summary>
    public string BodyTemplate { get; set; } = string.Empty;

    /// <summary>签名密钥：钉钉=加签 secret、飞书=签名校验 secret；空=不签名（企微无签名，用 URL key/IP 白名单）。</summary>
    public string? Secret { get; set; }

    public int TimeoutSeconds { get; set; } = 10;

    // ---- Backup ----

    private string _sourcePath = string.Empty;

    /// <summary>待备份目录（触发目标上的路径）。</summary>
    public string SourcePath { get => _sourcePath; set => Set(ref _sourcePath, value); }

    private string _destDir = string.Empty;

    /// <summary>备份输出目录（触发目标上的路径；文件名 自动生成 {trigger}-{yyyyMMdd-HHmmss}.{ext}）。</summary>
    public string DestDir { get => _destDir; set => Set(ref _destDir, value); }

    public TriggerBackupFormat BackupFormat { get; set; } = TriggerBackupFormat.Zip;

    /// <summary>仅保留最近 N 份（0 = 不清理）。</summary>
    public int RetentionCount { get; set; }

    // ---- DatabaseBackup ----

    private string _dsDatasourceId = string.Empty;

    /// <summary>目标数据源 Id（type 为 mysql / postgres）。</summary>
    public string DatasourceId { get => _dsDatasourceId; set => Set(ref _dsDatasourceId, value); }

    private string _dsDatabase = string.Empty;

    /// <summary>库名（MySQL=数据库名，PostgreSQL=数据库名）；留空 = 数据源默认库。</summary>
    public string Database { get => _dsDatabase; set => Set(ref _dsDatabase, value); }

    /// <summary>待备份的指定表（空 = 整库）。MySQL 表名，PostgreSQL schema.table。</summary>
    public string[] Tables { get; set; } = Array.Empty<string>();

    /// <summary>dump 工具路径（mysqldump / pg_dump；留空 = 从 PATH 探测）。</summary>
    public string DumpToolPath { get; set; } = string.Empty;

    /// <summary>备份产物是否 gzip 压缩为 .sql.gz（默认是）。</summary>
    public bool GzipEnabled { get; set; } = true;

    /// <summary>动作摘要（UI 列表与日志用）。</summary>
    public string Summarize() => Type switch
    {
        TriggerActionType.GitCommitPush => $"git: {GitDir}（{(GitOperation == TriggerGitOperation.CommitAndPush ? "提交并推送" : "仅提交")}）",
        TriggerActionType.Webhook => $"webhook: {Url}",
        TriggerActionType.Backup => $"备份: {SourcePath} → {DestDir}",
        TriggerActionType.DatabaseBackup => $"数据库备份: {DatasourceId} / {(string.IsNullOrWhiteSpace(Database) ? "默认库" : Database)} / {((Tables?.Length ?? 0) > 0 ? $"{Tables!.Length} 表" : "整库")} → {DestDir}",
        _ => Type.ToString()
    };

    /// <summary>列表绑定用（<see cref="Summarize"/> 的只读属性形式）。</summary>
    public string Summary => Summarize();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// 一个触发事件：手动执行，或按计划/某个同步任务完成后触发，按 <see cref="Actions"/> 动作流水线依次执行。
/// 仅在 App 运行期间调度（关闭 App 即停止，不补跑错过的计划）。
/// </summary>
public class TriggerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    public TriggerTarget TargetType { get; set; } = TriggerTarget.Local;

    /// <summary>TargetType=Remote 时的目标服务器 Id。</summary>
    public string? ServerId { get; set; }

    /// <summary>动作流水线（按顺序执行）。</summary>
    public TriggerAction[] Actions { get; set; } = Array.Empty<TriggerAction>();

    /// <summary>启用定时触发。</summary>
    public bool ScheduleEnabled { get; set; }

    /// <summary>是否允许被同步任务「事件触发」（同步任务里选择本触发事件时才生效）。</summary>
    public bool AllowEventTrigger { get; set; } = true;

    public SyncScheduleType ScheduleType { get; set; } = SyncScheduleType.Interval;

    /// <summary>固定间隔（分钟，ScheduleType=Interval）。</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>每周几（多选）：0=周日 .. 6=周六。</summary>
    public int[] WeekDays { get; set; } = new[] { 1 };

    /// <summary>每月几号（多选）：1..28。</summary>
    public int[] MonthDays { get; set; } = new[] { 1 };

    /// <summary>执行时刻 HH:mm（Weekly/Monthly）。</summary>
    public string TimeOfDay { get; set; } = "03:00";

    /// <summary>失败重试次数 0..5（指数退避）。</summary>
    public int MaxRetries { get; set; } = 3;

    public DateTime? LastRunAt { get; set; }

    /// <summary>上次结果摘要：ok / fail:...</summary>
    public string? LastResult { get; set; }

    /// <summary>校验配置，返回错误列表（空 = 通过）。</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
            errors.Add("名称不能为空");

        if (TargetType == TriggerTarget.Remote && string.IsNullOrWhiteSpace(ServerId))
            errors.Add("目标选择「远程服务器」时必须指定服务器");

        if (Actions is null || Actions.Length == 0)
            errors.Add("至少配置一个动作");
        else
        {
            for (var i = 0; i < Actions.Length; i++)
            {
                var a = Actions[i];
                if (a is null)
                {
                    errors.Add($"动作 {i + 1} 无效");
                    continue;
                }

                switch (a.Type)
                {
                    case TriggerActionType.GitCommitPush:
                        if (string.IsNullOrWhiteSpace(a.GitDir))
                            errors.Add($"动作 {i + 1}（git）：缺少仓库目录");
                        break;
                    case TriggerActionType.Webhook:
                        if (string.IsNullOrWhiteSpace(a.Url))
                            errors.Add($"动作 {i + 1}（webhook）：缺少 URL");
                        else if (!a.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                                 !a.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            errors.Add($"动作 {i + 1}（webhook）：URL 必须以 http(s):// 开头");
                        break;
                    case TriggerActionType.Backup:
                        if (string.IsNullOrWhiteSpace(a.SourcePath))
                            errors.Add($"动作 {i + 1}（备份）：缺少源目录");
                        if (string.IsNullOrWhiteSpace(a.DestDir))
                            errors.Add($"动作 {i + 1}（备份）：缺少备份输出目录");
                        break;
                    case TriggerActionType.DatabaseBackup:
                        if (string.IsNullOrWhiteSpace(a.DatasourceId))
                            errors.Add($"动作 {i + 1}（数据库备份）：未选择数据源");
                        if (string.IsNullOrWhiteSpace(a.DestDir))
                            errors.Add($"动作 {i + 1}（数据库备份）：缺少备份输出目录");
                        break;
                }
            }
        }

        if (ScheduleEnabled)
        {
            if (ScheduleType == SyncScheduleType.Interval && IntervalMinutes < 1)
                errors.Add("定时间隔至少 1 分钟");
            if (ScheduleType == SyncScheduleType.Weekly && (WeekDays is null || WeekDays.Length == 0))
                errors.Add("每周调度需至少选择一天");
            if (ScheduleType == SyncScheduleType.Monthly && (MonthDays is null || MonthDays.Length == 0))
                errors.Add("每月调度需至少选择一天");
        }

        return errors;
    }
}

/// <summary>
/// 模板变量渲染：替换 <c>{{key}}</c>（大小写不敏感）；未知变量原样保留。
/// 双花括号避免与 JSON 请求体的结构性花括号冲突。
/// </summary>
public static class TriggerTemplate
{
    private static readonly Regex Pattern = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    public static string Render(string? template, IReadOnlyDictionary<string, string> vars)
    {
        if (string.IsNullOrEmpty(template))
            return string.Empty;
        return Pattern.Replace(template, m =>
            vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }
}
