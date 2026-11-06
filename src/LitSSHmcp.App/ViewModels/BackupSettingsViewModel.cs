using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Backup;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public sealed class BackupPlanRow
{
    public required BackupPlanConfig Plan { get; init; }
    public BackupState? State { get; init; }
    public string Name => string.IsNullOrWhiteSpace(Plan.Name) ? "(未命名)" : Plan.Name;
    public string TargetDirectory => Plan.TargetDirectory;
    public string ScheduleText => Plan.ScheduleType switch
    {
        BackupScheduleType.Interval => $"每 {Math.Max(1, Plan.IntervalMinutes)} 分钟",
        BackupScheduleType.Daily => $"每天 {Plan.TimeOfDay}",
        BackupScheduleType.Weekly => $"每周 {string.Join(",", Plan.WeekDays)} {Plan.TimeOfDay}",
        BackupScheduleType.Monthly => $"每月 {string.Join(",", Plan.MonthDays)} 日 {Plan.TimeOfDay}",
        _ => string.Empty
    };
    public string ItemsText => Plan.Items.Length == 0 ? "未选择项目" : string.Join(" · ", Plan.Items);
    public string OssText => Plan.UploadToOss ? "上传 OSS" : "仅本地";
    public string LastRunText => State?.LastRunAt is { } at
        ? at.ToString("yyyy-MM-dd HH:mm:ss") + " · " + (State.LastResult ?? "未知")
        : "从未运行";
    public string NextRunText
    {
        get
        {
            var next = BackupSchedule.ComputeNextRun(Plan, DateTime.Now, State?.LastRunAt);
            return next is null ? "未调度" : next.Value.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }
    public bool IsEnabled => Plan.Enabled;
    public string EnabledText => Plan.Enabled ? "已启用" : "已停用";
    public string FallbackText => Plan.FallbackOnStartup ? "启动兜底" : string.Empty;
}

public sealed class BackupSettingsViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService;
    private readonly BackupService _backup;
    private AppConfig? _config;
    private bool _busy;
    private string _statusMessage = string.Empty;
    private CancellationTokenSource? _runCts;
    private string _progressText = string.Empty;

    public BackupSettingsViewModel(IConfigService? configService = null, BackupService? backup = null)
    {
        _configService = configService ?? AppServiceFactory.CreateConfigService();
        _backup = backup ?? new BackupService(new AliyunOssBackupUploader(), new DpapiSecretProtector());
    }

    public ObservableCollection<BackupPlanRow> Plans { get; } = new();

    private BackupPlanRow? _selectedPlan;
    public BackupPlanRow? SelectedPlan
    {
        get => _selectedPlan;
        set { if (ReferenceEquals(_selectedPlan, value)) return; _selectedPlan = value; OnPropertyChanged(); }
    }

    public string DataDir => ConfigPaths.AppDataDir;
    public bool IsRunning => _runCts is not null;
    public string ProgressText
    {
        get => _progressText;
        private set { if (_progressText == value) return; _progressText = value; OnPropertyChanged(); }
    }

    public void CancelRun() => _runCts?.Cancel();

    public bool Busy
    {
        get => _busy;
        private set { if (_busy == value) return; _busy = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set { if (_statusMessage == value) return; _statusMessage = value; OnPropertyChanged(); }
    }

    private string _ossEndpoint = string.Empty;
    public string OssEndpoint
    {
        get => _ossEndpoint;
        set { _ossEndpoint = value; OnPropertyChanged(); }
    }

    private string _ossBucket = string.Empty;
    public string OssBucket
    {
        get => _ossBucket;
        set { _ossBucket = value; OnPropertyChanged(); }
    }

    private string _ossPrefix = "litssh-backup";
    public string OssPrefix
    {
        get => _ossPrefix;
        set { _ossPrefix = value; OnPropertyChanged(); }
    }

    private string _ossAccessKeyId = string.Empty;
    public string OssAccessKeyId
    {
        get => _ossAccessKeyId;
        set { _ossAccessKeyId = value; OnPropertyChanged(); }
    }

    private bool _ossUseHttps = true;
    public bool OssUseHttps
    {
        get => _ossUseHttps;
        set { _ossUseHttps = value; OnPropertyChanged(); }
    }

    public string OssSecretHint => "已保存的 Secret 不会回显；留空保持不变。";

    public async Task LoadAsync()
    {
        Busy = true;
        try
        {
            _config = await _configService.LoadConfigAsync();
            var oss = _config.Backup.Oss;
            OssEndpoint = oss.Endpoint;
            OssBucket = oss.Bucket;
            OssPrefix = oss.Prefix;
            OssAccessKeyId = oss.AccessKeyId;
            OssUseHttps = oss.UseHttps;
            RefreshRows();
            StatusMessage = $"已加载 {Plans.Count} 个备份计划。";
        }
        catch (Exception ex)
        {
            StatusMessage = "加载备份计划失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task SaveOssAsync(string secret)
    {
        if (_config is null)
            await LoadAsync();
        if (_config is null)
            return;
        var oss = _config.Backup.Oss;
        oss.Endpoint = OssEndpoint.Trim();
        oss.Bucket = OssBucket.Trim();
        oss.Prefix = OssPrefix.Trim().Trim('/');
        oss.AccessKeyId = OssAccessKeyId.Trim();
        oss.UseHttps = OssUseHttps;
        if (!string.IsNullOrWhiteSpace(secret))
            oss.AccessKeySecretProtected = new DpapiSecretProtector().Protect(secret) ?? string.Empty;
        await _configService.SaveConfigAsync(_config);
        StatusMessage = "OSS 配置已保存。";
    }

    public async Task<string?> TestOssAsync(string secret)
    {
        var config = BuildOssConfig(secret);
        try
        {
            var value = string.IsNullOrWhiteSpace(secret) && _config is not null
                ? new DpapiSecretProtector().Unprotect(_config.Backup.Oss.AccessKeySecretProtected) ?? string.Empty
                : secret;
            await new AliyunOssBackupUploader().TestAsync(config, value);
            StatusMessage = "OSS 连接成功。";
            return null;
        }
        catch (Exception ex)
        {
            StatusMessage = "OSS 连接失败：" + ex.Message;
            return ex.Message;
        }
    }

    public async Task SavePlanAsync(BackupPlanConfig plan)
    {
        if (_config is null)
            await LoadAsync();
        if (_config is null)
            return;
        var existing = _config.Backup.Plans.FirstOrDefault(item => item.Id == plan.Id);
        if (existing is null)
        {
            var list = _config.Backup.Plans.ToList();
            list.Add(plan);
            _config.Backup.Plans = list.ToArray();
        }
        else
        {
            var index = Array.IndexOf(_config.Backup.Plans, existing);
            _config.Backup.Plans[index] = plan;
        }
        await _configService.SaveConfigAsync(_config);
        RefreshRows();
        StatusMessage = "备份计划已保存。";
    }

    public async Task DeletePlanAsync(BackupPlanRow row)
    {
        if (_config is null)
            return;
        _config.Backup.Plans = _config.Backup.Plans.Where(plan => plan.Id != row.Plan.Id).ToArray();
        await _configService.SaveConfigAsync(_config);
        RefreshRows();
        StatusMessage = "备份计划已删除。";
    }

    public async Task<BackupRunResult> RunNowAsync(BackupPlanRow row)
    {
        if (_config is null)
            await LoadAsync();
        if (_config is null)
            return new BackupRunResult(false, "配置未加载。");

        _runCts = new CancellationTokenSource();
        OnPropertyChanged(nameof(IsRunning));
        ProgressText = "准备备份…";
        StatusMessage = "正在执行备份：" + row.Name;
        try
        {
            var progress = new Progress<string>(text =>
            {
                ProgressText = text;
                StatusMessage = text;
            });
            var result = await _backup.RunAsync(row.Plan, _config, progress, _runCts.Token);
            var state = BackupStateStore.Get(row.Plan.Id) ?? new BackupState();
            state.LastRunAt = DateTime.Now;
            state.LastBackupFile = result.LocalPath;
            state.LastUploadResult = result.UploadMessage;
            state.LastResult = result.Success
                ? result.Uploaded ? "ok" : result.UploadMessage is null ? "ok" : "oss-fail"
                : "fail:" + result.Message;
            BackupStateStore.Save(row.Plan.Id, state);
            StatusMessage = result.Message;
            RefreshRows();
            return result;
        }
        catch (Exception ex)
        {
            StatusMessage = "备份失败：" + ex.Message;
            return new BackupRunResult(false, ex.Message);
        }
        finally
        {
            _runCts.Dispose();
            _runCts = null;
            OnPropertyChanged(nameof(IsRunning));
            ProgressText = string.Empty;
        }
    }

    public async Task<AppConfig?> GetConfigAsync()
    {
        if (_config is null)
            await LoadAsync();
        return _config;
    }

    private void RefreshRows()
    {
        Plans.Clear();
        if (_config is null)
            return;
        foreach (var plan in _config.Backup.Plans.OrderBy(plan => plan.Name))
        {
            Plans.Add(new BackupPlanRow
            {
                Plan = plan,
                State = BackupStateStore.Get(plan.Id)
            });
        }
        OnPropertyChanged(nameof(Plans));
    }

    private BackupOssConfig BuildOssConfig(string secret) => new()
    {
        Endpoint = OssEndpoint.Trim(),
        Bucket = OssBucket.Trim(),
        Prefix = OssPrefix.Trim().Trim('/'),
        AccessKeyId = OssAccessKeyId.Trim(),
        AccessKeySecretProtected = secret,
        UseHttps = OssUseHttps
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
