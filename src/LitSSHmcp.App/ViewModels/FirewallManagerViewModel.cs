using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public enum FirewallBackend
{
    None,
    Ufw,
    Firewalld,
    Nftables,
    Iptables
}

public sealed class FirewallRuleItem
{
    public required string Id { get; init; }
    public required string DirectionText { get; init; }
    public required string ActionText { get; init; }
    public required string Target { get; init; }
    public required string Protocol { get; init; }
    public required string Source { get; init; }
    public required string Destination { get; init; }
    public required string Zone { get; init; }
    public required string Comment { get; init; }
    public required string Persistence { get; init; }
    public bool IsService { get; init; }
    public int Number { get; init; }

    public string SourceTargetText
    {
        get
        {
            var source = string.IsNullOrWhiteSpace(Source) ? "any" : Source;
            var destination = string.IsNullOrWhiteSpace(Destination) ? "any" : Destination;
            return source == "any" && destination == "any" ? "any" : $"{source} → {destination}";
        }
    }
}

public sealed class FirewallStructuredRule
{
    public required string Kind { get; init; }
    public required string Table { get; init; }
    public required string Chain { get; init; }
    public required string Target { get; init; }
    public required string Rule { get; init; }
    public required string Comment { get; init; }
}

public sealed class FirewallBackupItem
{
    public required string FilePath { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required FirewallBackend Backend { get; init; }
    public required string Body { get; init; }
    public string FileName => Path.GetFileName(FilePath);
    public string CreatedAtText => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
    public string BackendText => Backend switch
    {
        FirewallBackend.Ufw => "UFW",
        FirewallBackend.Firewalld => "firewalld",
        FirewallBackend.Nftables => "nftables",
        FirewallBackend.Iptables => "iptables",
        _ => "未知"
    };
    public string Summary => Backend switch
    {
        FirewallBackend.Ufw => $"{ParseUfwCount(Body)} 条规则",
        FirewallBackend.Firewalld => $"{ParseFirewalldCount(Body)} 条端口/服务",
        _ => "不可恢复"
    };

    private static int ParseUfwCount(string body)
    {
        var section = body.Contains("==RULES==", StringComparison.Ordinal)
            ? body.Split("==RULES==", StringSplitOptions.None)[^1]
            : body;
        return Regex.Matches(section, @"^\[\s*\d+\]", RegexOptions.Multiline).Count;
    }

    private static int ParseFirewalldCount(string body)
    {
        var runtime = body;
        if (body.Contains("==RUNTIME==", StringComparison.Ordinal))
            runtime = body.Split("==RUNTIME==", StringSplitOptions.None)[^1];
        if (runtime.Contains("==PERMANENT==", StringComparison.Ordinal))
            runtime = runtime.Split("==PERMANENT==", StringSplitOptions.None)[0];
        var count = 0;
        foreach (var line in runtime.Replace("\r\n", "\n").Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith("ports:", StringComparison.OrdinalIgnoreCase))
                count += text["ports:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            else if (text.StartsWith("services:", StringComparison.OrdinalIgnoreCase))
                count += text["services:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        }
        return count;
    }
}

public sealed class FirewallManagerViewModel : INotifyPropertyChanged
{
    private readonly ISshService _ssh;
    private readonly IUserDialogService _dialogs;
    private readonly IAuditLogService? _audit;
    private bool _busy;
    private bool _active;
    private bool _sudoAttempted;
    private bool _sudoWorked;
    private bool _suppressEditorFill;
    private bool _suppressAddressMove;
    private FirewallBackend _backend;
    private FirewallRuleItem? _selectedRule;
    private FirewallRuleItem? _editingRule;
    private FirewallBackupItem? _selectedBackup;
    private readonly HashSet<int> _sshdPorts = new();

    private const string CommandPrefix = "export PATH=/usr/sbin:/sbin:/usr/bin:/bin:$PATH; ";

    private const string DetectCommand =
        "echo ==FIREWALLD==; systemctl is-active firewalld 2>&1 || true; " +
        "command -v firewall-cmd 2>/dev/null || true; " +
        "command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state 2>&1 || true; " +
        "echo ==UFW==; command -v ufw 2>/dev/null || true; " +
        "command -v ufw >/dev/null 2>&1 && ufw status 2>&1 | head -1 || true; " +
        "echo ==NFT==; command -v nft 2>/dev/null || true; command -v nft >/dev/null 2>&1 && nft list ruleset 2>&1 | head -1 || true; " +
        "echo ==IPT==; command -v iptables 2>/dev/null || true; command -v iptables >/dev/null 2>&1 && iptables -S 2>&1 | head -1 || true; " +
        "echo ==SSHPORT==; (sshd -T 2>/dev/null || grep -hiE '^[[:space:]]*Port[[:space:]]' /etc/ssh/sshd_config /etc/ssh/sshd_config.d/*.conf 2>/dev/null) | grep -iE '^[[:space:]]*port[[:space:]]' | awk '{print $2}' | sort -u || true; true";

    private const string UfwStatusCommand = "ufw status verbose 2>&1 || true; echo ==NUMBERED==; ufw status numbered 2>&1 || true";
    private const string FirewalldRuntimeCommand = "firewall-cmd --get-default-zone 2>&1 || true; echo ==ALL==; firewall-cmd --list-all 2>&1 || true";
    private const string FirewalldPermanentCommand = "firewall-cmd --permanent --list-all 2>&1 || true";
    private const string NftCommand = "nft -j list ruleset 2>&1 || nft list ruleset 2>&1 | head -400 || true";
    private const string IptablesCommand =
        "for t in filter nat mangle raw security; do echo ==TABLE:$t==; iptables -t $t -S 2>&1 || true; done";

    public FirewallManagerViewModel(SshServerConfig server, ISshService ssh,
        IUserDialogService? dialogs = null, IAuditLogService? audit = null)
    {
        Server = server;
        _ssh = ssh;
        _dialogs = dialogs ?? new UserDialogService();
        _audit = audit;
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync());
        NewRuleCommand = new RelayCommand(_ => BeginNewRule(), _ => CanEdit);
        SaveRuleCommand = new RelayCommand(_ => _ = SaveRuleAsync(), _ => CanEdit);
        DeleteRuleCommand = new RelayCommand(_ => _ = DeleteRuleAsync(), _ => CanEdit && SelectedRule is not null);
        CancelEditCommand = new RelayCommand(_ => ClearEditor());
        RefreshBackupsCommand = new RelayCommand(_ => LoadBackups());
        RestoreBackupCommand = new RelayCommand(_ => _ = RestoreBackupAsync(), _ => CanEdit && SelectedBackup is not null);
        OpenBackupFolderCommand = new RelayCommand(_ => OpenBackupFolder());
        RulesView = CollectionViewSource.GetDefaultView(Rules);
        RulesView.Filter = FilterRule;
        _sshdPorts.Add(Server.Port);
        LoadBackups();
    }

    public SshServerConfig Server { get; }
    public string Title => $"系统防火墙 - {Server.Name}";
    public string Subtitle => $"{Server.Host}:{Server.Port}";
    public ObservableCollection<FirewallRuleItem> Rules { get; } = new();
    public ObservableCollection<FirewallBackupItem> Backups { get; } = new();
    public ObservableCollection<FirewallStructuredRule> StructuredRules { get; } = new();
    public bool HasStructuredRules => Backend is FirewallBackend.Nftables or FirewallBackend.Iptables;
    public ICollectionView RulesView { get; }

    public ICommand RefreshCommand { get; }
    public ICommand NewRuleCommand { get; }
    public ICommand SaveRuleCommand { get; }
    public ICommand DeleteRuleCommand { get; }
    public ICommand CancelEditCommand { get; }
    public ICommand RefreshBackupsCommand { get; }
    public ICommand RestoreBackupCommand { get; }
    public ICommand OpenBackupFolderCommand { get; }

    public FirewallBackupItem? SelectedBackup
    {
        get => _selectedBackup;
        set
        {
            if (ReferenceEquals(_selectedBackup, value)) return;
            _selectedBackup = value;
            OnPropertyChanged();
            (RestoreBackupCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string SshPortText => string.Join(", ", _sshdPorts.OrderBy(x => x));

    public FirewallBackend Backend
    {
        get => _backend;
        private set
        {
            if (_backend == value) return;
            _backend = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BackendText));
        }
    }
    public bool CanEdit => _active && Backend is FirewallBackend.Ufw or FirewallBackend.Firewalld;
    public bool CanEditOutbound => Backend == FirewallBackend.Ufw;
    public string BackendText => Backend switch
    {
        FirewallBackend.Ufw => "UFW",
        FirewallBackend.Firewalld => "firewalld",
        FirewallBackend.Nftables => "nftables",
        FirewallBackend.Iptables => "iptables",
        _ => "未检测到"
    };

    private string _statusText = "检测中…";
    public string StatusText { get => _statusText; private set { _statusText = value; OnPropertyChanged(); } }

    private string _defaultPolicyText = "—";
    public string DefaultPolicyText { get => _defaultPolicyText; private set { _defaultPolicyText = value; OnPropertyChanged(); } }

    private string _zoneText = "—";
    public string ZoneText { get => _zoneText; private set { _zoneText = value; OnPropertyChanged(); } }

    private string _rawRulesText = string.Empty;
    public string RawRulesText { get => _rawRulesText; private set { _rawRulesText = value; OnPropertyChanged(); } }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set { if (_searchText == value) return; _searchText = value; OnPropertyChanged(); RulesView.Refresh(); }
    }

    public FirewallRuleItem? SelectedRule
    {
        get => _selectedRule;
        set
        {
            if (ReferenceEquals(_selectedRule, value)) return;
            _selectedRule = value;
            OnPropertyChanged();
            if (!_suppressEditorFill && value is not null)
                FillEditor(value);
            (DeleteRuleCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private string _direction = "入站";
    public string Direction
    {
        get => _direction;
        set
        {
            if (_direction == value) return;
            var previous = _direction;
            var address = previous == "出站" ? Destination : Source;
            _direction = value;
            if (!_suppressAddressMove)
            {
                if (value == "出站")
                {
                    Source = string.Empty;
                    Destination = address;
                }
                else
                {
                    Destination = string.Empty;
                    Source = address;
                }
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(AddressLabel));
            OnPropertyChanged(nameof(RuleAddress));
            var currentAddress = value == "出站" ? Destination : Source;
            AddressMode = string.IsNullOrWhiteSpace(currentAddress) ? "所有 IP" : "指定 IP";
        }
    }

    public string[] DirectionOptions => CanEditOutbound ? new[] { "入站", "出站" } : new[] { "入站" };
    public string[] ActionOptions => Backend == FirewallBackend.Firewalld
        ? new[] { "允许" }
        : new[] { "允许", "拒绝" };
    public string[] ProtocolOptions { get; } = { "any", "tcp", "udp" };

    private string _action = "允许";
    public string Action { get => _action; set { if (_action == value) return; _action = value; OnPropertyChanged(); } }

    private string _port = string.Empty;
    public string Port { get => _port; set { if (_port == value) return; _port = value; OnPropertyChanged(); } }

    private string _service = string.Empty;
    public string Service { get => _service; set { if (_service == value) return; _service = value; OnPropertyChanged(); } }

    private string _protocol = "tcp";
    public string Protocol { get => _protocol; set { if (_protocol == value) return; _protocol = value; OnPropertyChanged(); } }

    private string _source = string.Empty;
    public string Source { get => _source; set { if (_source == value) return; _source = value; OnPropertyChanged(); OnPropertyChanged(nameof(RuleAddress)); } }

    private string _destination = string.Empty;
    public string Destination { get => _destination; set { if (_destination == value) return; _destination = value; OnPropertyChanged(); OnPropertyChanged(nameof(RuleAddress)); } }

    public string AddressLabel => Direction == "出站" ? "目标 IP / CIDR" : "来源 IP / CIDR";
    public string[] AddressModeOptions { get; } = { "所有 IP", "指定 IP" };
    public bool CanUseAddress => Backend == FirewallBackend.Ufw;
    public bool CanUseZone => Backend == FirewallBackend.Firewalld;
    public bool IsAddressSpecified => _addressMode == "指定 IP";

    private string _addressMode = "所有 IP";
    public string AddressMode
    {
        get => _addressMode;
        set
        {
            if (_addressMode == value) return;
            _addressMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAddressSpecified));
            if (value == "所有 IP")
                RuleAddress = string.Empty;
        }
    }

    public string RuleAddress
    {
        get => Direction == "出站" ? Destination : Source;
        set
        {
            var valueToSet = value ?? string.Empty;
            if (Direction == "出站")
                Destination = valueToSet;
            else
                Source = valueToSet;
            OnPropertyChanged();
        }
    }

    private string _zone = string.Empty;
    public string Zone { get => _zone; set { if (_zone == value) return; _zone = value; OnPropertyChanged(); } }

    private string _comment = string.Empty;
    public string Comment { get => _comment; set { if (_comment == value) return; _comment = value; OnPropertyChanged(); } }

    public string EditorTitle => _editingRule is null ? "新增规则" : "编辑规则";
    public string SaveButtonText => _editingRule is null ? "添加规则" : "保存修改";

    public async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        _sudoAttempted = false;
        _sudoWorked = false;
        StatusText = "检测防火墙…";
        try
        {
            var detect = await RunAsync(DetectCommand);
            _sshdPorts.Clear();
            _sshdPorts.Add(Server.Port);
            foreach (var line in GetSection(ParseSections(detect.Output), "SSHPORT"))
                if (int.TryParse(line.Trim(), out var sshPort) && sshPort is > 0 and <= 65535)
                    _sshdPorts.Add(sshPort);
            OnPropertyChanged(nameof(SshPortText));
            Backend = DetectBackend(detect.Output, out var active);
            _active = active;
            if (Backend == FirewallBackend.Firewalld)
            {
                Direction = "入站";
                if (Action != "允许") Action = "允许";
            }
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanEditOutbound));
            OnPropertyChanged(nameof(CanUseAddress));
            OnPropertyChanged(nameof(CanUseZone));
            OnPropertyChanged(nameof(HasStructuredRules));
            OnPropertyChanged(nameof(DirectionOptions));
            OnPropertyChanged(nameof(ActionOptions));
            (NewRuleCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SaveRuleCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeleteRuleCommand as RelayCommand)?.RaiseCanExecuteChanged();

            Rules.Clear();
            StructuredRules.Clear();
            RawRulesText = string.Empty;
            DefaultPolicyText = "—";
            ZoneText = "—";

            if (Backend == FirewallBackend.None)
            {
                StatusText = "未检测到 ufw / firewalld / nftables / iptables，或当前账号无权限。";
                RefreshView();
                return;
            }

            if (Backend == FirewallBackend.Ufw)
            {
                var result = await RunAsync(UfwStatusCommand);
                if (IsPermissionDenied(result))
                {
                    StatusText = PermissionMessage;
                    RefreshView();
                    return;
                }
                var parts = ParseSections(result.Output);
                var status = GetSectionText(parts, "NUMBERED") ?? string.Empty;
                var verbose = result.Output.Split("==NUMBERED==", StringSplitOptions.None)[0];
                var ufwActive = verbose.Contains("Status: active", StringComparison.OrdinalIgnoreCase);
                var ufwInactive = verbose.Contains("Status: inactive", StringComparison.OrdinalIgnoreCase);
                _active = ufwActive;
                StatusText = ufwActive ? "运行中"
                    : ufwInactive ? "未启用（修改已禁用）"
                    : "无法读取状态（可能需要 sudo）";
                DefaultPolicyText = ParseUfwDefaultPolicy(verbose);
                ZoneText = "默认";
                foreach (var rule in ParseUfwRules(status))
                    Rules.Add(rule);
                RawRulesText = status;
            }
            else if (Backend == FirewallBackend.Firewalld)
            {
                if (!_active)
                {
                    StatusText = "未启用（修改已禁用）";
                    RefreshView();
                    return;
                }
                var runtime = await RunAsync(FirewalldRuntimeCommand);
                var permanent = await RunAsync(FirewalldPermanentCommand);
                if (IsPermissionDenied(runtime) || IsPermissionDenied(permanent))
                {
                    StatusText = PermissionMessage;
                    RefreshView();
                    return;
                }
                var rtSections = ParseSections(runtime.Output);
                var zone = FirstLine(GetSectionText(rtSections, "ALL") ?? string.Empty);
                ZoneText = string.IsNullOrWhiteSpace(zone) ? "default" : zone;
                StatusText = "运行中";
                DefaultPolicyText = ParseFirewalldTarget(GetSectionText(rtSections, "ALL"));
                var rt = ParseFirewalldRules(GetSectionText(rtSections, "ALL"));
                var perm = ParseFirewalldRules(permanent.Output);
                foreach (var item in MergeFirewalld(rt, perm))
                    Rules.Add(item);
                RawRulesText = runtime.Output;
            }
            else
            {
                _active = false;
                var raw = Backend == FirewallBackend.Nftables
                    ? await RunAsync(NftCommand)
                    : await RunAsync(IptablesCommand);
                if (IsPermissionDenied(raw))
                {
                    RawRulesText = PermissionMessage;
                    StatusText = PermissionMessage;
                    RefreshView();
                    return;
                }
                RawRulesText = raw.Output;
                if (Backend == FirewallBackend.Nftables)
                    ParseNftRules(raw.Output);
                else
                    ParseIptablesRules(raw.Output);
                StatusText = "只读（结构化展示）";
                (RestoreBackupCommand as RelayCommand)?.RaiseCanExecuteChanged();
                RefreshView();
                return;
            }

            StatusText = StatusText + (_sudoAttempted && _sudoWorked ? "（sudo）" : string.Empty);
            RefreshView();
        }
        catch (Exception ex)
        {
            StatusText = "读取失败: " + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private void BeginNewRule()
    {
        _editingRule = null;
        _suppressEditorFill = true;
        SelectedRule = null;
        _suppressEditorFill = false;
        FillEditor(null);
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(SaveButtonText));
    }

    private void ClearEditor()
    {
        _editingRule = null;
        _suppressEditorFill = true;
        SelectedRule = null;
        _suppressEditorFill = false;
        FillEditor(null);
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(SaveButtonText));
    }

    private void FillEditor(FirewallRuleItem? rule)
    {
        _editingRule = rule;
        _suppressAddressMove = true;
        try
        {
            Direction = rule?.DirectionText ?? "入站";
            Action = rule?.ActionText ?? "允许";
            Port = rule is null or { IsService: true } ? string.Empty : rule.Target;
            Service = rule is { IsService: true } ? rule.Target : string.Empty;
            if (!string.IsNullOrWhiteSpace(Port) && Port.Contains('/'))
                Port = Port[..Port.LastIndexOf('/')];
            Protocol = rule?.Protocol is "tcp" or "udp" ? rule.Protocol : "any";
            Source = rule?.Source == "any" ? string.Empty : rule?.Source ?? string.Empty;
            Destination = rule?.Destination == "any" ? string.Empty : rule?.Destination ?? string.Empty;
        }
        finally
        {
            _suppressAddressMove = false;
        }
        var address = Direction == "出站" ? Destination : Source;
        AddressMode = string.IsNullOrWhiteSpace(address) ? "所有 IP" : "指定 IP";
        if (AddressMode == "指定 IP")
            RuleAddress = address;
        Zone = rule?.Zone == "—" ? string.Empty : rule?.Zone ?? string.Empty;
        Comment = rule?.Comment ?? string.Empty;
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(SaveButtonText));
    }

    private async Task SaveRuleAsync()
    {
        if (!CanEdit || _busy) return;
        if (!ValidateEditor(out var error))
        {
            StatusText = error;
            return;
        }

        var target = BuildTarget();
        if (Action == "拒绝" && IsSshTarget(target, !string.IsNullOrWhiteSpace(Service)))
        {
            _dialogs.Warn("不能通过此面板添加拒绝当前 SSH 端口的规则，避免把当前连接锁死。", "系统防火墙");
            return;
        }
        if (_editingRule is not null && IsSshTarget(_editingRule.Target, _editingRule.IsService))
        {
            _dialogs.Warn("当前 SSH 端口规则禁止在此修改，请在服务器本地或终端中操作。", "系统防火墙");
            return;
        }

        var verb = _editingRule is null ? "新增" : "修改";
        var commands = new List<string>();
        if (_editingRule is not null)
            commands.AddRange(BuildDeleteCommands(_editingRule));
        commands.AddRange(BuildAddCommands());
        var preview = FormatCommandPreview(commands);
        if (!_dialogs.Confirm(
                $"{verb}规则：{Direction} {Action} {target}\n\n将执行：\n{preview}",
                "系统防火墙", warning: Action == "拒绝"))
            return;

        _busy = true;
        try
        {
            var backup = await BackupAsync();
            foreach (var command in commands)
            {
                var isDelete = command.Contains("--remove-", StringComparison.Ordinal) ||
                               command.StartsWith("ufw --force delete", StringComparison.Ordinal);
                var result = await RunAsync(command + (isDelete ? " 2>&1 || true" : " 2>&1"));
                if (IsPermissionDenied(result))
                {
                    StatusText = PermissionMessage;
                    return;
                }
                if (!result.Success && (!isDelete || Backend == FirewallBackend.Ufw))
                {
                    StatusText = $"执行失败: {(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error)}";
                    return;
                }
                await AuditAsync($"firewall_{(_editingRule is null ? "add" : "update")}", command, result);
            }

            StatusText = $"{verb}成功，正在刷新规则…" + (backup is null ? string.Empty : $"（已备份: {backup}）");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"{verb}失败: {ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task DeleteRuleAsync()
    {
        if (!CanEdit || SelectedRule is not { } rule || _busy) return;
        if (IsSshTarget(rule.Target, rule.IsService))
        {
            _dialogs.Warn("当前 SSH 端口规则禁止在此删除，请在服务器本地或终端中操作。", "系统防火墙");
            return;
        }
        var deleteCommands = BuildDeleteCommands(rule);
        var preview = FormatCommandPreview(deleteCommands);
        if (!_dialogs.Confirm(
                $"删除规则：{rule.DirectionText} {rule.ActionText} {rule.Target}\n\n将执行：\n{preview}",
                "系统防火墙", true))
            return;

        _busy = true;
        try
        {
            var backup = await BackupAsync();
            foreach (var command in deleteCommands)
            {
                var result = await RunAsync(command + (Backend == FirewallBackend.Ufw ? " 2>&1" : " 2>&1 || true"));
                if (IsPermissionDenied(result))
                {
                    StatusText = PermissionMessage;
                    return;
                }
                if (!result.Success && Backend == FirewallBackend.Ufw)
                {
                    StatusText = "删除失败: " +
                                 (string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
                    return;
                }
                await AuditAsync("firewall_delete", command, result);
            }
            StatusText = "删除成功，正在刷新规则…" + (backup is null ? string.Empty : $"（已备份: {backup}）");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = "删除失败: " + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private bool ValidateEditor(out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(Port) && string.IsNullOrWhiteSpace(Service))
        {
            error = "请填写端口/端口范围或服务名。";
            return false;
        }
        if (Backend == FirewallBackend.Firewalld && Direction == "出站")
        {
            error = "firewalld 第一版仅支持默认区域入站规则。";
            return false;
        }
        if (Backend == FirewallBackend.Firewalld && Action != "允许")
        {
            error = "firewalld 第一版仅支持允许规则；拒绝规则请使用 rich rule 或控制台。";
            return false;
        }
        if (Backend == FirewallBackend.Firewalld &&
            (!string.IsNullOrWhiteSpace(Source) || !string.IsNullOrWhiteSpace(Destination)))
        {
            error = "firewalld 第一版暂不支持来源/目标地址编辑。";
            return false;
        }
        if (Backend == FirewallBackend.Firewalld && !string.IsNullOrWhiteSpace(Port) &&
            Protocol is not "tcp" and not "udp")
        {
            error = "firewalld 端口规则的协议必须为 tcp 或 udp。";
            return false;
        }
        if (Backend == FirewallBackend.Ufw && !string.IsNullOrWhiteSpace(Service) &&
            (!string.IsNullOrWhiteSpace(Source) || !string.IsNullOrWhiteSpace(Destination)))
        {
            error = "UFW 服务规则暂不支持同时指定来源/目标地址，请改用端口规则。";
            return false;
        }
        if (CanUseAddress && IsAddressSpecified && string.IsNullOrWhiteSpace(RuleAddress))
        {
            error = $"请填写{AddressLabel}，或选择“所有 IP”。";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(Port) && Protocol is not "tcp" and not "udp")
        {
            error = "端口规则的协议请选择 tcp 或 udp。";
            return false;
        }
        if (Backend == FirewallBackend.Ufw && !string.IsNullOrWhiteSpace(Port) &&
            !Regex.IsMatch(Port.Trim(), @"^\d+(?:[:-]\d+)?(?:,\d+(?:[:-]\d+)?)*$"))
        {
            error = "UFW 端口格式应为 80、80,443 或 5000:5010。";
            return false;
        }
        if (Backend == FirewallBackend.Firewalld && !string.IsNullOrWhiteSpace(Port) &&
            !Regex.IsMatch(Port.Trim(), @"^\d+(?:-\d+)?(?:,\d+(?:-\d+)?)*$"))
        {
            error = "firewalld 端口格式应为 80、80,443 或 5000-5010。";
            return false;
        }
        return true;
    }

    private string BuildTarget() =>
        !string.IsNullOrWhiteSpace(Port) ? Port.Trim() :
        !string.IsNullOrWhiteSpace(Service) ? Service.Trim() : string.Empty;

    private List<string> BuildAddCommands() =>
        BuildAddCommands(Direction, Action, Port, Service, Protocol, Source, Destination, Zone, Comment);

    private List<string> BuildAddCommandsFor(FirewallRuleItem rule)
    {
        var port = string.Empty;
        var service = string.Empty;
        if (rule.IsService)
            service = rule.Target;
        else
            port = rule.Target.Contains('/') ? rule.Target[..rule.Target.LastIndexOf('/')] : rule.Target;
        return BuildAddCommands(rule.DirectionText, rule.ActionText, port, service, rule.Protocol,
            rule.Source, rule.Destination, rule.Zone, rule.Comment);
    }

    private List<string> BuildAddCommands(string directionText, string actionText, string port,
        string service, string protocolText, string source, string destination, string zoneText, string comment)
    {
        var result = new List<string>();
        if (Backend == FirewallBackend.Ufw)
        {
            var action = actionText == "拒绝" ? "deny" : "allow";
            var direction = directionText == "出站" ? "out" : "in";
            if (!string.IsNullOrWhiteSpace(port))
            {
                var proto = protocolText is "tcp" or "udp" ? protocolText : null;
                var ufwPort = port.Trim().Replace('-', ':');
                var from = string.IsNullOrWhiteSpace(source) || source == "any" ? "any" : source.Trim();
                var to = string.IsNullOrWhiteSpace(destination) || destination == "any" ? "any" : destination.Trim();
                var command = $"ufw {action} {direction}";
                if (from == "any" && to == "any")
                {
                    command += $" {ufwPort}";
                    if (proto is not null) command += $"/{proto}";
                }
                else
                {
                    command += $" from {from} to {to} port {ufwPort}";
                    if (proto is not null) command += $" proto {proto}";
                }
                if (!string.IsNullOrWhiteSpace(comment))
                    command += $" comment {ShellQuote.Single(comment.Trim())}";
                result.Add(command);
            }
            else
            {
                var command = $"ufw {action} {direction} {service.Trim()}";
                if (!string.IsNullOrWhiteSpace(comment))
                    command += $" comment {ShellQuote.Single(comment.Trim())}";
                result.Add(command);
            }
        }
        else if (Backend == FirewallBackend.Firewalld)
        {
            var zone = string.IsNullOrWhiteSpace(zoneText) || zoneText == "default"
                ? string.Empty
                : $"--zone={ShellQuote.Single(zoneText.Trim())} ";
            if (!string.IsNullOrWhiteSpace(port))
            {
                foreach (var part in port.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var target = $"{part}/{protocolText}";
                    result.Add($"firewall-cmd {zone}--add-port={ShellQuote.Single(target)}");
                    result.Add($"firewall-cmd --permanent {zone}--add-port={ShellQuote.Single(target)}");
                }
            }
            else
            {
                result.Add($"firewall-cmd {zone}--add-service={ShellQuote.Single(service.Trim())}");
                result.Add($"firewall-cmd --permanent {zone}--add-service={ShellQuote.Single(service.Trim())}");
            }
        }
        return result;
    }

    private List<string> BuildDeleteCommands(FirewallRuleItem rule)
    {
        var result = new List<string>();
        if (Backend == FirewallBackend.Ufw)
        {
            if (rule.Number > 0)
                result.Add($"ufw --force delete {rule.Number}");
        }
        else if (Backend == FirewallBackend.Firewalld)
        {
            var zone = string.IsNullOrWhiteSpace(rule.Zone) || rule.Zone == "default" ? string.Empty : $"--zone={ShellQuote.Single(rule.Zone)} ";
            var kind = rule.IsService ? "service" : "port";
            result.Add($"firewall-cmd {zone}--remove-{kind}={ShellQuote.Single(rule.Target)}");
            result.Add($"firewall-cmd --permanent {zone}--remove-{kind}={ShellQuote.Single(rule.Target)}");
        }
        return result;
    }

    private void ParseNftRules(string output)
    {
        var text = output.Trim();
        if (!text.StartsWith('{'))
        {
            if (text.Length > 0)
                StructuredRules.Add(new FirewallStructuredRule
                {
                    Kind = "原始",
                    Table = "nftables",
                    Chain = string.Empty,
                    Target = string.Empty,
                    Rule = text,
                    Comment = "JSON 解析不可用"
                });
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("nftables", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                return;

            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("table", out var table))
                {
                    var family = JsonString(table, "family");
                    var name = JsonString(table, "name");
                    StructuredRules.Add(new FirewallStructuredRule
                    {
                        Kind = "表",
                        Table = $"{family}/{name}",
                        Chain = string.Empty,
                        Target = string.Empty,
                        Rule = string.Empty,
                        Comment = string.Empty
                    });
                }
                else if (item.TryGetProperty("chain", out var chain))
                {
                    var family = JsonString(chain, "family");
                    var tableName = JsonString(chain, "table");
                    var name = JsonString(chain, "name");
                    var type = JsonString(chain, "type");
                    var hook = JsonString(chain, "hook");
                    var policy = JsonString(chain, "policy");
                    var target = string.Join(' ', new[] { type, hook, policy.Length > 0 ? $"policy={policy}" : string.Empty }
                        .Where(x => !string.IsNullOrWhiteSpace(x)));
                    StructuredRules.Add(new FirewallStructuredRule
                    {
                        Kind = "链",
                        Table = $"{family}/{tableName}",
                        Chain = name,
                        Target = target,
                        Rule = string.Empty,
                        Comment = string.Empty
                    });
                }
                else if (item.TryGetProperty("rule", out var rule))
                {
                    var family = JsonString(rule, "family");
                    var tableName = JsonString(rule, "table");
                    var chainName = JsonString(rule, "chain");
                    var comment = JsonString(rule, "comment");
                    var expr = rule.TryGetProperty("expr", out var expression)
                        ? expression.GetRawText()
                        : string.Empty;
                    var target = rule.TryGetProperty("expr", out var exprValue)
                        ? ExtractNftTarget(exprValue)
                        : string.Empty;
                    StructuredRules.Add(new FirewallStructuredRule
                    {
                        Kind = "规则",
                        Table = $"{family}/{tableName}",
                        Chain = chainName,
                        Target = target,
                        Rule = expr,
                        Comment = comment
                    });
                }
            }
        }
        catch (JsonException)
        {
            StructuredRules.Add(new FirewallStructuredRule
            {
                Kind = "原始",
                Table = "nftables",
                Chain = string.Empty,
                Target = string.Empty,
                Rule = text,
                Comment = "JSON 解析失败"
            });
        }
    }

    private static string JsonString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string ExtractNftTarget(JsonElement expression)
    {
        if (expression.ValueKind != JsonValueKind.Array) return string.Empty;
        foreach (var item in expression.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            foreach (var property in item.EnumerateObject())
            {
                if (property.Name is "accept" or "drop" or "reject" or "masquerade" or "return" or "continue")
                    return property.Name;
                if (property.Name is "jump" or "goto")
                {
                    if (property.Value.ValueKind == JsonValueKind.Object &&
                        property.Value.TryGetProperty("target", out var target))
                        return $"{property.Name} {target.GetString()}";
                    return property.Name;
                }
            }
        }
        return string.Empty;
    }

    private void ParseIptablesRules(string output)
    {
        var table = string.Empty;
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("==TABLE:", StringComparison.Ordinal) && line.EndsWith("==", StringComparison.Ordinal))
            {
                table = line["==TABLE:".Length..^2];
                continue;
            }
            if (line.StartsWith("iptables: ", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            {
                StructuredRules.Add(new FirewallStructuredRule
                {
                    Kind = "错误",
                    Table = table,
                    Chain = string.Empty,
                    Target = string.Empty,
                    Rule = line,
                    Comment = string.Empty
                });
                continue;
            }
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 3 && tokens[0] == "-P")
            {
                StructuredRules.Add(new FirewallStructuredRule
                {
                    Kind = "策略",
                    Table = table,
                    Chain = tokens[1],
                    Target = tokens[2],
                    Rule = line,
                    Comment = string.Empty
                });
                continue;
            }
            if (tokens.Length >= 2 && tokens[0] == "-N")
            {
                StructuredRules.Add(new FirewallStructuredRule
                {
                    Kind = "链",
                    Table = table,
                    Chain = tokens[1],
                    Target = string.Empty,
                    Rule = line,
                    Comment = string.Empty
                });
                continue;
            }
            if (tokens.Length >= 2 && tokens[0] == "-A")
            {
                var targetMatch = Regex.Match(line, @"(?:^|\s)(?:-j|-g)\s+(\S+)");
                var commentMatch = Regex.Match(line, @"--comment\s+(?:""([^""]*)""|(\S+))");
                var comment = commentMatch.Success
                    ? (commentMatch.Groups[1].Success ? commentMatch.Groups[1].Value : commentMatch.Groups[2].Value)
                    : string.Empty;
                StructuredRules.Add(new FirewallStructuredRule
                {
                    Kind = "规则",
                    Table = table,
                    Chain = tokens[1],
                    Target = targetMatch.Success ? targetMatch.Groups[1].Value : string.Empty,
                    Rule = line,
                    Comment = comment
                });
            }
        }
    }

    private async Task<string?> BackupAsync()
    {
        try
        {
            var command = Backend switch
            {
                FirewallBackend.Ufw => "ufw status verbose 2>&1 || true; echo ==RULES==; ufw status numbered 2>&1 || true",
                FirewallBackend.Firewalld => "echo ==RUNTIME==; firewall-cmd --list-all 2>&1 || true; echo ==PERMANENT==; firewall-cmd --permanent --list-all 2>&1 || true",
                _ => null
            };
            if (command is null) return null;
            var result = await RunAsync(command);
            var dir = BackupDirectory;
            Directory.CreateDirectory(dir);
            var created = DateTime.Now;
            var file = Path.Combine(dir, $"{Server.Id}-{created:yyyyMMdd-HHmmss-fff}.txt");
            var content =
                "# LitSSH Firewall Backup\n" +
                $"# ServerId={Server.Id}\n" +
                $"# Backend={Backend}\n" +
                $"# CreatedAt={created:O}\n" +
                "# ---\n" +
                result.Output;
            await File.WriteAllTextAsync(file, content);
            LoadBackups();
            return file;
        }
        catch
        {
            return null;
        }
    }

    private static string BackupDirectory =>
        Path.Combine(ConfigPaths.AppDataDir, "firewall-backups");

    private void LoadBackups()
    {
        Backups.Clear();
        try
        {
            var dir = BackupDirectory;
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, $"{Server.Id}-*.txt")
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                try
                {
                    var raw = File.ReadAllText(file);
                    var parsed = ParseBackupContent(raw, File.GetLastWriteTime(file));
                    if (parsed.Backend is not (FirewallBackend.Ufw or FirewallBackend.Firewalld))
                        continue;
                    Backups.Add(new FirewallBackupItem
                    {
                        FilePath = file,
                        CreatedAt = parsed.CreatedAt,
                        Backend = parsed.Backend,
                        Body = parsed.Body
                    });
                }
                catch
                {
                    // 单个损坏的备份不影响其他备份
                }
            }
        }
        catch
        {
            // 备份目录不可读时保持空列表
        }
        SelectedBackup = Backups.FirstOrDefault();
        OnPropertyChanged(nameof(Backups));
    }

    private static (FirewallBackend Backend, DateTime CreatedAt, string Body) ParseBackupContent(
        string content, DateTime fallbackCreatedAt)
    {
        var backend = FirewallBackend.None;
        var createdAt = fallbackCreatedAt;
        var body = content;
        if (content.StartsWith("# LitSSH Firewall Backup", StringComparison.Ordinal))
        {
            var separator = content.IndexOf("\n# ---", StringComparison.Ordinal);
            if (separator >= 0)
            {
                var header = content[..separator];
                body = content[(separator + "\n# ---".Length)..].TrimStart('\r', '\n');
                foreach (var line in header.Replace("\r\n", "\n").Split('\n'))
                {
                    if (line.StartsWith("# Backend=", StringComparison.Ordinal))
                        Enum.TryParse<FirewallBackend>(line["# Backend=".Length..].Trim(), out backend);
                    else if (line.StartsWith("# CreatedAt=", StringComparison.Ordinal) &&
                             DateTime.TryParse(line["# CreatedAt=".Length..].Trim(), out var parsed))
                        createdAt = parsed;
                }
            }
        }

        if (backend == FirewallBackend.None)
        {
            if (body.Contains("==RUNTIME==", StringComparison.Ordinal) ||
                (body.Contains("services:", StringComparison.OrdinalIgnoreCase) &&
                 body.Contains("ports:", StringComparison.OrdinalIgnoreCase)))
                backend = FirewallBackend.Firewalld;
            else if (body.Contains("==RULES==", StringComparison.Ordinal) ||
                     Regex.IsMatch(body, @"^\[\s*\d+\]", RegexOptions.Multiline))
                backend = FirewallBackend.Ufw;
        }
        return (backend, createdAt, body);
    }

    private void OpenBackupFolder()
    {
        var dir = BackupDirectory;
        Directory.CreateDirectory(dir);
        NativeShell.OpenFolder(dir);
    }

    private async Task RestoreBackupAsync()
    {
        if (!CanEdit || _busy || SelectedBackup is not { } backup) return;
        if (backup.Backend != Backend)
        {
            _dialogs.Warn($"该备份是 {backup.BackendText} 规则，与当前 {BackendText} 不匹配。", "系统防火墙");
            return;
        }

        var desired = ParseBackupRules(backup);
        if (desired.Count == 0 && Rules.Count == 0)
        {
            _dialogs.Info("备份中没有可恢复的端口/服务规则。", "系统防火墙");
            return;
        }

        var currentKeys = Rules.Select(RuleKey).ToHashSet(StringComparer.Ordinal);
        var desiredKeys = desired.Select(RuleKey).ToHashSet(StringComparer.Ordinal);
        var remove = Rules.Where(rule => !desiredKeys.Contains(RuleKey(rule))).ToList();
        var add = desired.Where(rule => !currentKeys.Contains(RuleKey(rule))).ToList();
        if (remove.Count == 0 && add.Count == 0)
        {
            _dialogs.Info("当前规则与备份一致，无需恢复。", "系统防火墙");
            return;
        }

        if (remove.Any(rule => IsSshTarget(rule.Target, rule.IsService)) ||
            add.Any(rule => rule.ActionText == "拒绝" && IsSshTarget(rule.Target, rule.IsService)))
        {
            _dialogs.Warn("恢复会删除当前 SSH 端口规则或新增拒绝 SSH 规则，已阻止以避免锁死连接。", "系统防火墙");
            return;
        }

        var commands = remove.SelectMany(BuildDeleteCommands)
            .Concat(add.SelectMany(BuildAddCommandsFor))
            .ToList();
        if (commands.Count == 0) return;
        var preview = FormatCommandPreview(commands);
        if (!_dialogs.Confirm(
                $"恢复备份 {backup.CreatedAtText}\n\n" +
                $"将删除 {remove.Count} 条、新增 {add.Count} 条规则。\n\n{preview}",
                "系统防火墙", warning: true))
            return;

        _busy = true;
        try
        {
            var preRestoreBackup = await BackupAsync();
            foreach (var command in commands)
            {
                var isDelete = command.Contains("--remove-", StringComparison.Ordinal) ||
                               command.StartsWith("ufw --force delete", StringComparison.Ordinal);
                var result = await RunAsync(command + (isDelete ? " 2>&1 || true" : " 2>&1"));
                if (IsPermissionDenied(result))
                {
                    StatusText = PermissionMessage;
                    return;
                }
                if (!result.Success && (!isDelete || Backend == FirewallBackend.Ufw))
                {
                    StatusText = "恢复失败: " +
                                 (string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
                    return;
                }
                await AuditAsync("firewall_restore", command, result);
            }
            StatusText = "恢复完成，正在刷新规则…" +
                         (preRestoreBackup is null ? string.Empty : $"（恢复前备份: {preRestoreBackup}）");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = "恢复失败: " + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private List<FirewallRuleItem> ParseBackupRules(FirewallBackupItem backup)
    {
        if (backup.Backend == FirewallBackend.Ufw)
        {
            var section = backup.Body;
            if (backup.Body.Contains("==RULES==", StringComparison.Ordinal))
                section = backup.Body.Split("==RULES==", StringSplitOptions.None)[^1];
            return ParseUfwRules(section).ToList();
        }

        if (backup.Backend == FirewallBackend.Firewalld)
        {
            var sections = ParseSections(backup.Body);
            var runtime = GetSectionText(sections, "RUNTIME") ?? backup.Body;
            var permanent = GetSectionText(sections, "PERMANENT") ?? string.Empty;
            return MergeFirewalld(ParseFirewalldRules(runtime), ParseFirewalldRules(permanent)).ToList();
        }
        return new List<FirewallRuleItem>();
    }

    private string RuleKey(FirewallRuleItem rule) => Backend == FirewallBackend.Ufw
        ? $"{rule.DirectionText}|{rule.ActionText}|{rule.Target}|{rule.Protocol}|{rule.Source}|{rule.Destination}"
        : $"{rule.Zone}|{rule.Target}|{rule.IsService}";

    private static string FormatCommandPreview(IEnumerable<string> commands)
    {
        var list = commands.ToList();
        var shown = list.Take(12).ToList();
        var text = string.Join(Environment.NewLine, shown.Select(command => "  " + command));
        return list.Count > shown.Count
            ? text + Environment.NewLine + $"  … 共 {list.Count} 条命令"
            : text;
    }

    private async Task AuditAsync(string tool, string command, CommandResult result)
    {
        if (_audit is null) return;
        try
        {
            await _audit.LogCommandAsync(new CommandAuditLog
            {
                ServerId = Server.Id,
                ServerName = Server.Name,
                Tool = tool,
                Command = command,
                Status = result.Success ? CommandStatus.Executed : CommandStatus.Failed,
                Result = result.Success ? result.Output : result.Error,
                ExitCode = result.ExitCode,
                Category = AuditCategory.Exec
            });
        }
        catch
        {
            // 审计失败不改变操作结果
        }
    }

    private bool FilterRule(object item)
    {
        if (item is not FirewallRuleItem rule) return false;
        var keyword = SearchText.Trim();
        return keyword.Length == 0 ||
               rule.DirectionText.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               rule.ActionText.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               rule.Target.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               rule.Source.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               rule.Comment.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshView()
    {
        RulesView.Refresh();
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanEditOutbound));
        OnPropertyChanged(nameof(CanUseAddress));
        OnPropertyChanged(nameof(CanUseZone));
        OnPropertyChanged(nameof(HasStructuredRules));
        OnPropertyChanged(nameof(AddressLabel));
        OnPropertyChanged(nameof(DirectionOptions));
        OnPropertyChanged(nameof(ActionOptions));
        (NewRuleCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveRuleCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteRuleCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private static FirewallBackend DetectBackend(string output, out bool active)
    {
        active = false;
        var sections = ParseSections(output);
        var firewalldLines = GetSection(sections, "FIREWALLD")
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();
        var firewalldActive = firewalldLines.Any(x =>
            x.Equals("active", StringComparison.OrdinalIgnoreCase) ||
            x.Equals("running", StringComparison.OrdinalIgnoreCase));
        var firewalldInstalled = firewalldLines.Any(x =>
            x.Equals("running", StringComparison.OrdinalIgnoreCase) ||
            x.Equals("not running", StringComparison.OrdinalIgnoreCase));
        if (firewalldActive)
        {
            active = true;
            return FirewallBackend.Firewalld;
        }

        var ufwLines = GetSection(sections, "UFW");
        var ufw = GetSectionText(sections, "UFW");
        if (ufw is not null && ufw.Contains("Status: active", StringComparison.OrdinalIgnoreCase))
        {
            active = true;
            return FirewallBackend.Ufw;
        }
        if (firewalldInstalled || HasExecutable(firewalldLines, "firewall-cmd"))
            return FirewallBackend.Firewalld;
        if (ufw is not null && ufw.Contains("Status:", StringComparison.OrdinalIgnoreCase) ||
            HasExecutable(ufwLines, "ufw"))
            return FirewallBackend.Ufw;
        if (HasExecutable(GetSection(sections, "NFT"), "nft"))
            return FirewallBackend.Nftables;
        if (HasExecutable(GetSection(sections, "IPT"), "iptables"))
            return FirewallBackend.Iptables;
        return FirewallBackend.None;
    }

    private static bool HasExecutable(IEnumerable<string> lines, string name) =>
        lines.Any(line =>
        {
            var value = line.Trim();
            return value.StartsWith('/') &&
                   Path.GetFileName(value).StartsWith(name, StringComparison.OrdinalIgnoreCase);
        });

    private static Dictionary<string, List<string>> ParseSections(string output)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? current = null;
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("==") && line.EndsWith("=="))
            {
                current = line.Trim('=');
                sections[current] = new List<string>();
            }
            else if (current is not null)
                sections[current].Add(line);
        }
        return sections;
    }

    private static List<string> GetSection(Dictionary<string, List<string>> sections, string key) =>
        sections.TryGetValue(key, out var lines) ? lines : new List<string>();

    private static string? GetSectionText(Dictionary<string, List<string>> sections, string key) =>
        sections.TryGetValue(key, out var lines) ? string.Join('\n', lines) : null;

    private static string FirstLine(string text) =>
        text.Replace("\r\n", "\n").Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? string.Empty;

    private static string ParseUfwDefaultPolicy(string text)
    {
        var line = text.Replace("\r\n", "\n").Split('\n')
            .Select(x => x.Trim())
            .FirstOrDefault(x => x.StartsWith("Default:", StringComparison.OrdinalIgnoreCase));
        return line ?? "—";
    }

    private static string ParseFirewalldTarget(string? text)
    {
        var line = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n')
            .Select(x => x.Trim())
            .FirstOrDefault(x => x.StartsWith("target:", StringComparison.OrdinalIgnoreCase));
        return line ?? "target: default";
    }

    private static IEnumerable<FirewallRuleItem> ParseUfwRules(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) yield break;
        var regex = new Regex(@"^\[\s*(\d+)\]\s+(.+)$", RegexOptions.Compiled);
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var match = regex.Match(raw.Trim());
            if (!match.Success) continue;
            var number = int.Parse(match.Groups[1].Value);
            var text = match.Groups[2].Value.Trim();
            var comment = string.Empty;
            var hash = text.IndexOf('#');
            if (hash >= 0)
            {
                comment = text[(hash + 1)..].Trim();
                text = text[..hash].Trim();
            }
            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            var actionIndex = tokens.FindIndex(t => t is "ALLOW" or "DENY" or "REJECT" or "LIMIT");
            if (actionIndex < 0) continue;
            var actionToken = tokens[actionIndex];
            var target = string.Join(' ', tokens.Take(actionIndex)).Trim();
            var direction = "入站";
            var next = actionIndex + 1;
            if (next < tokens.Count && tokens[next] is "IN" or "OUT" or "FWD")
            {
                direction = tokens[next] == "OUT" ? "出站" : "入站";
                next++;
            }
            var source = NormalizeUfwAddress(string.Join(' ', tokens.Skip(next)));
            var parsed = ParseUfwTarget(target);
            yield return new FirewallRuleItem
            {
                Id = $"ufw:{number}",
                DirectionText = direction,
                ActionText = actionToken == "ALLOW" ? "允许" : "拒绝",
                Target = parsed.Display,
                Protocol = parsed.Protocol,
                Source = source,
                Destination = parsed.Destination,
                Zone = "default",
                Comment = comment,
                Persistence = "永久",
                IsService = parsed.IsService,
                Number = number
            };
        }
    }

    private static string NormalizeUfwAddress(string value)
    {
        value = value.Replace("(v6)", string.Empty)
            .Replace("(out)", string.Empty)
            .Replace("(in)", string.Empty)
            .Trim();
        var onIndex = value.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        if (onIndex >= 0)
            value = value[..onIndex].Trim();
        if (value.Length == 0 || value.StartsWith("Anywhere", StringComparison.OrdinalIgnoreCase))
            return "any";
        return value;
    }

    private static (string Destination, string Display, string Protocol, bool IsService) ParseUfwTarget(string target)
    {
        var value = target.Trim();
        var slash = value.LastIndexOf('/');
        var protocol = slash >= 0 ? value[(slash + 1)..].Trim().ToLowerInvariant() : "any";
        if (protocol is not "tcp" and not "udp") protocol = "any";
        var main = slash >= 0 ? value[..slash].Trim() : value;
        if (main.Length == 0 || main.StartsWith("Anywhere", StringComparison.OrdinalIgnoreCase))
            return ("any", "any", protocol, false);

        var tokens = main.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 2 && IsPortSpec(tokens[^1]))
        {
            var destination = NormalizeUfwAddress(string.Join(' ', tokens[..^1]));
            var port = tokens[^1];
            return (destination, $"{port}/{protocol}", protocol, false);
        }
        if (IsPortSpec(main))
            return ("any", $"{main}/{protocol}", protocol, false);
        return ("any", main, protocol, true);
    }

    private static bool IsPortSpec(string value) =>
        Regex.IsMatch(value, @"^\d+(?:[-,:]\d+)*$");

    private sealed record FirewalldEntry(string Zone, string Value, bool IsPort);

    private static List<FirewalldEntry> ParseFirewalldRules(string? output)
    {
        var result = new List<FirewalldEntry>();
        if (string.IsNullOrWhiteSpace(output)) return result;
        var zone = FirstLine(output).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "public";
        var inPorts = false;
        var inServices = false;
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("ports:", StringComparison.OrdinalIgnoreCase))
            {
                inPorts = true; inServices = false;
                foreach (var item in line["ports:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    result.Add(new FirewalldEntry(zone, item, true));
                continue;
            }
            if (line.StartsWith("services:", StringComparison.OrdinalIgnoreCase))
            {
                inServices = true; inPorts = false;
                foreach (var item in line["services:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    result.Add(new FirewalldEntry(zone, item, false));
                continue;
            }
            if (line.Contains(':'))
            {
                inPorts = false;
                inServices = false;
            }
            else if (inPorts && line.Length > 0)
                foreach (var item in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    result.Add(new FirewalldEntry(zone, item, true));
            else if (inServices && line.Length > 0)
                foreach (var item in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    result.Add(new FirewalldEntry(zone, item, false));
        }
        return result;
    }

    private static IEnumerable<FirewallRuleItem> MergeFirewalld(
        List<FirewalldEntry> runtime, List<FirewalldEntry> permanent)
    {
        var keys = runtime.Select(e => $"{e.Zone}|{e.Value}|{e.IsPort}")
            .Concat(permanent.Select(e => $"{e.Zone}|{e.Value}|{e.IsPort}"))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            var parts = key.Split('|');
            var zone = parts[0];
            var value = parts[1];
            var isPort = parts[2] == "True";
            var rt = runtime.Any(e => e.Zone == zone && e.Value == value && e.IsPort == isPort);
            var perm = permanent.Any(e => e.Zone == zone && e.Value == value && e.IsPort == isPort);
            var persistence = rt && perm ? "运行时+永久" : rt ? "运行时" : "永久";
            var slash = value.LastIndexOf('/');
            var protocol = isPort && slash >= 0 ? value[(slash + 1)..] : "any";
            yield return new FirewallRuleItem
            {
                Id = $"firewalld:{zone}:{value}",
                DirectionText = "入站",
                ActionText = "允许",
                Target = value,
                Protocol = protocol,
                Source = "any",
                Destination = "any",
                Zone = zone,
                Comment = string.Empty,
                Persistence = persistence,
                IsService = !isPort
            };
        }
    }

    private async Task<CommandResult> RunAsync(string command, CancellationToken ct = default)
    {
        command = CommandPrefix + command;
        var direct = await _ssh.ExecuteCommandAsync(Server, command, ct, timeoutSeconds: 30);
        if (!IsPermissionDenied(direct) || Server.SudoType == SudoType.None)
            return direct;

        _sudoAttempted = true;
        var sudo = await _ssh.ExecuteWithSudoAsync(Server, command, ct);
        _sudoWorked = sudo.Success && !IsPermissionDenied(sudo);
        return sudo;
    }

    private static bool IsPermissionDenied(CommandResult result)
    {
        var text = (result.Error + "\n" + result.Output).ToLowerInvariant();
        return text.Contains("permission denied", StringComparison.Ordinal) ||
               text.Contains("operation not permitted", StringComparison.Ordinal) ||
               text.Contains("access denied", StringComparison.Ordinal) ||
               text.Contains("you need to be root", StringComparison.Ordinal) ||
               text.Contains("must be root", StringComparison.Ordinal) ||
               text.Contains("authorization failed", StringComparison.Ordinal) ||
               text.Contains("authentication is required", StringComparison.Ordinal) ||
               text.Contains("polkit", StringComparison.Ordinal);
    }

    private bool IsSshTarget(string target, bool isService)
    {
        if (isService &&
            (target.Equals("ssh", StringComparison.OrdinalIgnoreCase) ||
             target.Equals("openssh", StringComparison.OrdinalIgnoreCase)))
            return true;

        var value = target.Trim();
        if (value.Equals("ssh", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("openssh", StringComparison.OrdinalIgnoreCase))
            return true;
        var first = value.Split('/')[0].Split(':')[0];
        return int.TryParse(first, out var port) && _sshdPorts.Contains(port);
    }

    private const string PermissionMessage =
        "无权限读取/修改防火墙。请使用有权限的账号，或在服务器配置中启用 sudo/Su 提权。";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
