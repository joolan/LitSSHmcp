using System.ComponentModel;
using System.Runtime.CompilerServices;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public class SecuritySettingsViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService = new ConfigService();

    private bool _mcpEnabled = true;
    private string _blockedCommands = string.Empty;
    private string _sensitiveCommands = string.Empty;
    private string _sensitivePatterns = string.Empty;
    private string _sqlBlockedPatterns = string.Empty;
    private string _sqlSensitivePatterns = string.Empty;
    private bool _fileTransferEnabled = true;
    private bool _fileTransferRequireApproval = true;
    private string _maxFileSizeMb = "100";
    private string _allowedLocalPaths = string.Empty;
    private string _allowedRemotePaths = string.Empty;
    private string _sshHostKeyMode = "Tofu";
    private string _discoveryPaths = string.Empty;
    private bool _discoveryUseSudo;
    private string _logAllowedPaths = string.Empty;
    private string _maxConcurrent = "3";
    private string _maxPerMinute = "60";
    private bool _auditStoreSqlText = true;
    private bool _auditMaskLiterals;
    private string _auditRetentionDays = "90";
    private string _approvalTimeout = "45";
    private bool _approvalTopMost = true;
    private string _approvalStyle = "dialog";
    private int _approvalModeIndex;
    private int _approvalChannelIndex;
    private string[]? _loadedChannels;
    private string _maskingRules = string.Empty;
    private bool _snapshotUseSudo = true;
    private string _snapshotRetention = "30";
    private string _snapshotTimeout = "180";
    private string _statusMessage = string.Empty;

    public SecuritySettingsViewModel()
    {
        Load();
    }

    public bool McpEnabled { get => _mcpEnabled; set => Set(ref _mcpEnabled, value); }
    public string BlockedCommands { get => _blockedCommands; set => Set(ref _blockedCommands, value); }
    public string SensitiveCommands { get => _sensitiveCommands; set => Set(ref _sensitiveCommands, value); }
    public string SensitivePatterns { get => _sensitivePatterns; set => Set(ref _sensitivePatterns, value); }
    public string SqlBlockedPatterns { get => _sqlBlockedPatterns; set => Set(ref _sqlBlockedPatterns, value); }
    public string SqlSensitivePatterns { get => _sqlSensitivePatterns; set => Set(ref _sqlSensitivePatterns, value); }
    public bool FileTransferEnabled { get => _fileTransferEnabled; set => Set(ref _fileTransferEnabled, value); }
    public bool FileTransferRequireApproval { get => _fileTransferRequireApproval; set => Set(ref _fileTransferRequireApproval, value); }
    public string MaxFileSizeMb { get => _maxFileSizeMb; set => Set(ref _maxFileSizeMb, value); }
    public string AllowedLocalPaths { get => _allowedLocalPaths; set => Set(ref _allowedLocalPaths, value); }
    public string AllowedRemotePaths { get => _allowedRemotePaths; set => Set(ref _allowedRemotePaths, value); }
    public string SshHostKeyMode { get => _sshHostKeyMode; set => Set(ref _sshHostKeyMode, value); }
    public string DiscoveryPaths { get => _discoveryPaths; set => Set(ref _discoveryPaths, value); }
    public bool DiscoveryUseSudo { get => _discoveryUseSudo; set => Set(ref _discoveryUseSudo, value); }
    public string LogAllowedPaths { get => _logAllowedPaths; set => Set(ref _logAllowedPaths, value); }
    public string MaxConcurrent { get => _maxConcurrent; set => Set(ref _maxConcurrent, value); }
    public string MaxPerMinute { get => _maxPerMinute; set => Set(ref _maxPerMinute, value); }
    public bool AuditStoreSqlText { get => _auditStoreSqlText; set => Set(ref _auditStoreSqlText, value); }
    public bool AuditMaskLiterals { get => _auditMaskLiterals; set => Set(ref _auditMaskLiterals, value); }
    public string AuditRetentionDays { get => _auditRetentionDays; set => Set(ref _auditRetentionDays, value); }
    public string ApprovalTimeout { get => _approvalTimeout; set => Set(ref _approvalTimeout, value); }
    public bool ApprovalTopMost { get => _approvalTopMost; set => Set(ref _approvalTopMost, value); }
    public string ApprovalStyle { get => _approvalStyle; set => Set(ref _approvalStyle, value); }
    public int ApprovalModeIndex { get => _approvalModeIndex; set => Set(ref _approvalModeIndex, value); }
    public string[] ApprovalModeOptions { get; } = { "手动处理 (默认)", "自动允许授权 (危险)", "自动拒绝授权" };
    public int ApprovalChannelIndex { get => _approvalChannelIndex; set => Set(ref _approvalChannelIndex, value); }
    public string[] ApprovalChannelOptions { get; } = { "桌面弹窗 (desktop)", "命令行带外 (cli)", "桌面 + 命令行" };
    public string MaskingRules { get => _maskingRules; set => Set(ref _maskingRules, value); }
    public bool SnapshotUseSudo { get => _snapshotUseSudo; set => Set(ref _snapshotUseSudo, value); }
    public string SnapshotRetention { get => _snapshotRetention; set => Set(ref _snapshotRetention, value); }
    public string SnapshotTimeout { get => _snapshotTimeout; set => Set(ref _snapshotTimeout, value); }

    public string StatusMessage
    {
        get => _statusMessage;
        set => Set(ref _statusMessage, value);
    }

    public async void Load()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var s = config.Security;

            McpEnabled = s.Enabled;
            BlockedCommands = Join(s.CommandFilter.BlockedCommands);
            SensitiveCommands = Join(s.CommandFilter.SensitiveCommands);
            SensitivePatterns = Join(s.CommandFilter.SensitivePatterns);
            SqlBlockedPatterns = Join(s.SqlFilter.BlockedPatterns);
            SqlSensitivePatterns = Join(s.SqlFilter.SensitivePatterns);

            FileTransferEnabled = s.FileTransfer.Enabled;
            FileTransferRequireApproval = s.FileTransfer.RequireApproval;
            MaxFileSizeMb = (s.FileTransfer.MaxFileSizeBytes / 1024 / 1024).ToString();
            AllowedLocalPaths = Join(s.FileTransfer.AllowedLocalPaths);
            AllowedRemotePaths = Join(s.FileTransfer.AllowedRemotePaths);

            SshHostKeyMode = s.SshHostKey.Mode.ToString();
            DiscoveryPaths = Join(s.Discovery.AllowedSearchPaths);
            DiscoveryUseSudo = s.Discovery.UseSudo;
            LogAllowedPaths = Join(s.Logs.AllowedPaths);
            MaxConcurrent = s.Limits.MaxConcurrentPerTarget.ToString();
            MaxPerMinute = s.Limits.MaxCallsPerMinutePerTarget.ToString();

            AuditStoreSqlText = s.Audit.StoreSqlText;
            AuditMaskLiterals = s.Audit.MaskLiterals;
            AuditRetentionDays = s.Audit.RetentionDays.ToString();
            ApprovalTimeout = s.Approval.TimeoutSeconds.ToString();
            ApprovalTopMost = s.Approval.TopMost;
            ApprovalStyle = s.Approval.Style;
            ApprovalModeIndex = ResolveApprovalModeIndex(s.Approval.Mode);
            _loadedChannels = s.Approval.Channels;
            ApprovalChannelIndex = ResolveChannelIndex(s.Approval.Channels);
            MaskingRules = Join(s.Masking.Rules.Select(r => $"{r.Column}={r.Mode}").ToArray());

            SnapshotUseSudo = config.Snapshot.UseSudo;
            SnapshotRetention = config.Snapshot.RetentionPerServer.ToString();
            SnapshotTimeout = config.Snapshot.TimeoutSeconds.ToString();

            StatusMessage = "已加载当前安全配置";
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败: {ex.Message}";
        }
    }

    public async void Save()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var s = config.Security;

            s.Enabled = McpEnabled;
            s.CommandFilter.BlockedCommands = Split(BlockedCommands);
            s.CommandFilter.SensitiveCommands = Split(SensitiveCommands);
            s.CommandFilter.SensitivePatterns = Split(SensitivePatterns);
            s.SqlFilter.BlockedPatterns = Split(SqlBlockedPatterns);
            s.SqlFilter.SensitivePatterns = Split(SqlSensitivePatterns);

            s.FileTransfer.Enabled = FileTransferEnabled;
            s.FileTransfer.RequireApproval = FileTransferRequireApproval;
            s.FileTransfer.MaxFileSizeBytes = ParseLong(MaxFileSizeMb, 100) * 1024 * 1024;
            s.FileTransfer.AllowedLocalPaths = Split(AllowedLocalPaths);
            s.FileTransfer.AllowedRemotePaths = Split(AllowedRemotePaths);

            s.SshHostKey.Mode = Enum.TryParse<SshHostKeyMode>(SshHostKeyMode, ignoreCase: true, out var mode)
                ? mode
                : LitSSHmcp.Core.Models.SshHostKeyMode.Tofu;
            s.Discovery.AllowedSearchPaths = Split(DiscoveryPaths);
            s.Discovery.UseSudo = DiscoveryUseSudo;
            s.Logs.AllowedPaths = Split(LogAllowedPaths);
            s.Limits.MaxConcurrentPerTarget = (int)Math.Max(1, ParseLong(MaxConcurrent, 3));
            s.Limits.MaxCallsPerMinutePerTarget = (int)Math.Max(0, ParseLong(MaxPerMinute, 60));

            s.Audit.StoreSqlText = AuditStoreSqlText;
            s.Audit.MaskLiterals = AuditMaskLiterals;
            s.Audit.RetentionDays = (int)Math.Max(0, ParseLong(AuditRetentionDays, 90));
            s.Approval.TimeoutSeconds = (int)Math.Max(0, ParseLong(ApprovalTimeout, 45));
            s.Approval.TopMost = ApprovalTopMost;
            s.Approval.Style = ApprovalStyle?.Trim().ToLowerInvariant() switch
            {
                "process" => "process",
                "native" => "native",
                _ => "dialog"
            };
            s.Approval.Mode = ApprovalModeIndex switch
            {
                1 => "auto-approve",
                2 => "auto-reject",
                _ => "manual"
            };
            // 未改动通道下拉时保留原值(这样 config 里自定义/未知的通道不会被静默覆盖)；
            // 改动了才按下拉索引重写。
            var mappedChannels = ApprovalChannelIndex switch
            {
                1 => new[] { "cli" },
                2 => new[] { "desktop", "cli" },
                _ => new[] { "desktop" }
            };
            s.Approval.Channels = _loadedChannels is not null && ApprovalChannelIndex == ResolveChannelIndex(_loadedChannels)
                ? _loadedChannels
                : mappedChannels;
            s.Masking.Rules = ParseMaskRules(MaskingRules);

            config.Snapshot ??= new SnapshotConfig();
            config.Snapshot.UseSudo = SnapshotUseSudo;
            config.Snapshot.RetentionPerServer = (int)Math.Max(0, ParseLong(SnapshotRetention, 30));
            config.Snapshot.TimeoutSeconds = (int)Math.Max(1, ParseLong(SnapshotTimeout, 180));

            await _configService.SaveConfigAsync(config);
            StatusMessage = "已保存 (过滤器/限流规则无需重启即刻生效)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败: {ex.Message}";
        }
    }

    private static int ResolveChannelIndex(string[]? channels)
    {
        var set = (channels ?? Array.Empty<string>()).Select(c => c.ToLowerInvariant()).ToHashSet();
        var desktop = set.Contains("desktop");
        var cli = set.Contains("cli");
        return (desktop, cli) switch
        {
            (true, true) => 2,
            (false, true) => 1,
            _ => 0
        };
    }

    private static int ResolveApprovalModeIndex(string? mode) => (mode ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "auto-approve" or "auto_approve" or "autoapprove" or "allow" or "approve" => 1,
        "auto-reject" or "auto_reject" or "autoreject" or "deny" or "reject" => 2,
        _ => 0
    };

    private static string Join(string[]? values) => values == null ? string.Empty : string.Join(Environment.NewLine, values);

    // 每行 "列名正则=模式"(模式缺省 full)
    private static MaskRule[] ParseMaskRules(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Array.Empty<MaskRule>()
            : text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.TrimEnd('\r').Trim())
                .Where(line => line.Length > 0 && line.Contains('='))
                .Select(line =>
                {
                    var separator = line.IndexOf('=');
                    var mode = line[(separator + 1)..].Trim();
                    return new MaskRule
                    {
                        Column = line[..separator].Trim(),
                        Mode = string.IsNullOrEmpty(mode) ? "full" : mode
                    };
                })
                .Where(rule => rule.Column.Length > 0)
                .ToArray();

    private static string[] Split(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.TrimEnd('\r')).ToArray();

    private static long ParseLong(string? text, long fallback) =>
        long.TryParse(text?.Trim(), out var value) && value >= 0 ? value : fallback;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
