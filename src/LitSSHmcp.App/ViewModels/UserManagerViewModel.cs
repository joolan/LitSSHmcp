using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public sealed class SshUserItem
{
    public string UserName { get; init; } = string.Empty;
    public int Uid { get; init; }
    public int Gid { get; init; }
    public string PrimaryGroup { get; init; } = string.Empty;
    public string Groups { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string Home { get; init; } = string.Empty;
    public string Shell { get; init; } = string.Empty;
    public string PasswordStatus { get; init; } = string.Empty;
    public string HashAlgorithm { get; init; } = string.Empty;
    public string AccountStatus { get; init; } = string.Empty;
    public string LastLogin { get; init; } = string.Empty;
    public string CanLogin { get; init; } = string.Empty;
    public string LoginMethod { get; init; } = string.Empty;
    public bool IsPrivileged { get; init; }
    public bool IsRisk { get; init; }
    public string RiskLevel { get; init; } = string.Empty;
    public string RiskNotes { get; init; } = string.Empty;
    public bool IsSystem { get; init; }
    public bool IsAnonymous { get; init; }
    public bool IsHidden { get; init; }
    public bool IsLocked { get; init; }
    public string PasswordChanged { get; init; } = string.Empty;
    public string PasswordAge { get; init; } = string.Empty;
    public string AccountExpires { get; init; } = string.Empty;
    public string SudoRules { get; init; } = string.Empty;
    public string ActiveSessions { get; init; } = string.Empty;
    public string FailedLogins { get; init; } = string.Empty;
    public List<SshAuthorizedKey> AuthorizedKeys { get; init; } = new();
}

public sealed class SshAuthorizedKey
{
    public string FilePath { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public int LineNumber { get; init; }
    public string Fingerprint { get; init; } = string.Empty;
    public string KeyType { get; init; } = string.Empty;
    public string Comment { get; init; } = string.Empty;
}

public sealed class UserManagerViewModel : INotifyPropertyChanged
{
    private readonly ISshService _ssh;
    private readonly IUserDialogService _dialogs;
    private readonly IAuditLogService? _audit;
    private bool _busy;
    private bool _elevated;

    private const string CommandPrefix = "export PATH=/usr/sbin:/sbin:/usr/bin:/bin:$PATH; ";

    private const string AuditCommand =
        "echo '##PASSWD'; getent passwd 2>/dev/null || cat /etc/passwd 2>/dev/null; " +
        "echo '##GROUP'; getent group 2>/dev/null | head -1200; " +
        "echo '##SHADOW'; cat /etc/shadow 2>/dev/null | head -2000; " +
        "echo '##LASTLOG'; lastlog 2>/dev/null | head -1200; " +
        "echo '##LAST'; last -n 120 2>/dev/null; " +
        "echo '##WHO'; who 2>/dev/null | head -200; " +
        "echo '##LASTB'; lastb -n 120 2>/dev/null | head -150; " +
        "echo '##KEYS'; for f in /root/.ssh/authorized_keys /home/*/.ssh/authorized_keys; do [ -f \"$f\" ] || continue; echo \"@@KEYFILE $f\"; " +
        "awk 'NF && $1 !~ /^#/ {print NR\"\\t\"$0}' \"$f\" | while IFS=\"$(printf '\\t')\" read -r n line; do " +
        "fp=$(printf '%s\\n' \"$line\" | ssh-keygen -lf - 2>/dev/null | head -1); echo \"@@KEYLINE $n $fp\"; done; done; " +
        "echo '##SSHD'; (sshd -T 2>/dev/null || grep -hiE '^[[:space:]]*(PermitRootLogin|PasswordAuthentication|PubkeyAuthentication|PermitEmptyPasswords|AllowUsers|DenyUsers|KbdInteractiveAuthentication|ChallengeResponseAuthentication|UsePAM)[[:space:]]' /etc/ssh/sshd_config /etc/ssh/sshd_config.d/*.conf 2>/dev/null); " +
        "echo '##SUDO'; getent group wheel sudo 2>/dev/null; grep -rhE '^[^#]*(wheel|sudo|NOPASSWD)' /etc/sudoers /etc/sudoers.d/ 2>/dev/null | head -200; true";

    private static readonly HashSet<string> NoLoginShells = new(StringComparer.OrdinalIgnoreCase)
    {
        "/sbin/nologin", "/usr/sbin/nologin", "/bin/false", "/usr/bin/false", "/bin/sync"
    };

    private static readonly HashSet<string> AnonymousNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "anonymous", "ftp", "guest", "nobody", "nfsnobody", "unknown", "test", "temp", "default"
    };

    public UserManagerViewModel(SshServerConfig server, ISshService ssh,
        IUserDialogService? dialogs = null, IAuditLogService? audit = null)
    {
        Server = server;
        _ssh = ssh;
        _dialogs = dialogs ?? new UserDialogService();
        _audit = audit;
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(false), _ => !IsBusy);
        ElevatedRefreshCommand = new RelayCommand(_ => _ = RefreshAsync(true),
            _ => !IsBusy && Server.SudoType != SudoType.None);
        ExportCommand = new RelayCommand(_ => ExportCsv(), _ => Users.Count > 0 && !IsBusy);
        LockCommand = new RelayCommand(_ => _ = LockUserAsync(), _ => CanLock);
        UnlockCommand = new RelayCommand(_ => _ = UnlockUserAsync(), _ => CanUnlock);
        DisableShellCommand = new RelayCommand(_ => _ = DisableShellAsync(), _ => CanDisableShell);
        RestoreShellCommand = new RelayCommand(_ => _ = RestoreShellAsync(), _ => CanRestoreShell);
        RemoveSudoCommand = new RelayCommand(_ => _ = RemoveSudoAsync(), _ => CanRemoveSudo);
        DeleteKeyCommand = new RelayCommand(_ => _ = DeleteKeyAsync(), _ => CanDeleteKey);
        UsersView = CollectionViewSource.GetDefaultView(Users);
        UsersView.Filter = FilterUser;
    }

    public SshServerConfig Server { get; }
    public string Title => $"用户管理 - {Server.Name}";
    public string Subtitle => $"{Server.Host}:{Server.Port}";
    public ObservableCollection<SshUserItem> Users { get; } = new();
    public ICollectionView UsersView { get; }

    private SshUserItem? _selectedUser;
    public SshUserItem? SelectedUser
    {
        get => _selectedUser;
        set
        {
            if (ReferenceEquals(_selectedUser, value)) return;
            _selectedUser = value;
            SelectedKey = null;
            OnPropertyChanged();
            RaiseActionCanExecute();
        }
    }

    public ICommand RefreshCommand { get; }
    public ICommand ElevatedRefreshCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand LockCommand { get; }
    public ICommand UnlockCommand { get; }
    public ICommand DisableShellCommand { get; }
    public ICommand RestoreShellCommand { get; }
    public ICommand RemoveSudoCommand { get; }
    public ICommand DeleteKeyCommand { get; }

    private SshAuthorizedKey? _selectedKey;
    public SshAuthorizedKey? SelectedKey
    {
        get => _selectedKey;
        set
        {
            if (ReferenceEquals(_selectedKey, value)) return;
            _selectedKey = value;
            OnPropertyChanged();
            (DeleteKeyCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            OnPropertyChanged();
            UsersView.Refresh();
        }
    }

    private bool _showOnlyRisk;
    public bool ShowOnlyRisk
    {
        get => _showOnlyRisk;
        set
        {
            if (_showOnlyRisk == value) return;
            _showOnlyRisk = value;
            OnPropertyChanged();
            UsersView.Refresh();
        }
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (_busy == value) return;
            _busy = value;
            OnPropertyChanged();
            (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ElevatedRefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ExportCommand as RelayCommand)?.RaiseCanExecuteChanged();
            RaiseActionCanExecute();
        }
    }

    public string ModeText => _elevated ? "提权查看" : "直接查看";

    private string _statusText = "点击刷新读取服务器用户信息。";
    public string StatusText
    {
        get => _statusText;
        private set { if (_statusText == value) return; _statusText = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusTextWithMode)); }
    }
    public string StatusTextWithMode => $"{ModeText} · {StatusText}";

    private string _sshSettingsText = "—";
    public string SshSettingsText
    {
        get => _sshSettingsText;
        private set { if (_sshSettingsText == value) return; _sshSettingsText = value; OnPropertyChanged(); }
    }

    public int TotalUsers => Users.Count;
    public int LoginableUsers => Users.Count(user => !user.CanLogin.StartsWith("禁止", StringComparison.Ordinal));
    public int PrivilegedUsers => Users.Count(user => user.IsPrivileged);
    public int RiskUsers => Users.Count(user => user.IsRisk);
    public string SummaryText => $"用户 {TotalUsers} · 可登录 {LoginableUsers} · 特权 {PrivilegedUsers} · 风险 {RiskUsers}";

    private async Task RefreshAsync(bool elevated)
    {
        if (IsBusy) return;
        if (elevated && Server.SudoType == SudoType.None)
        {
            _dialogs.Warn("该服务器未配置 sudo/Su 提权。", "用户管理");
            return;
        }

        _elevated = elevated;
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(StatusTextWithMode));
        IsBusy = true;
        StatusText = "正在读取用户、组、登录记录和 SSH 配置…";
        try
        {
            var command = CommandPrefix + AuditCommand;
            var result = _elevated && Server.SudoType != SudoType.None
                ? await _ssh.ExecuteWithSudoAsync(Server, command)
                : await _ssh.ExecuteCommandAsync(Server, command, timeoutSeconds: 45);
            if (IsPermissionDenied(result) && !_elevated)
            {
                StatusText = "权限不足，可尝试「提权查看」。";
                return;
            }
            Parse(result.Output);
            StatusText = $"已读取 {Users.Count} 个系统账号。";
            OnPropertyChanged(nameof(TotalUsers));
            OnPropertyChanged(nameof(LoginableUsers));
            OnPropertyChanged(nameof(PrivilegedUsers));
            OnPropertyChanged(nameof(RiskUsers));
            OnPropertyChanged(nameof(SummaryText));
            UsersView.Refresh();
            await AuditAsync();
        }
        catch (Exception ex)
        {
            StatusText = "读取失败: " + ex.Message;
            _dialogs.Error("读取用户信息失败：" + ex.Message, "用户管理");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Parse(string output)
    {
        var previousUser = SelectedUser?.UserName;
        Users.Clear();
        var sections = ParseSections(output);
        var passwd = GetSection(sections, "PASSWD");
        var groupLines = GetSection(sections, "GROUP");
        var shadowLines = GetSection(sections, "SHADOW");
        var lastlog = GetSectionText(sections, "LASTLOG");
        var last = GetSectionText(sections, "LAST");
        var sshd = ParseSshd(GetSectionText(sections, "SSHD"));

        var groups = ParseGroups(groupLines);
        var groupByGid = groups.GroupBy(g => g.Gid).ToDictionary(g => g.Key, g => g.First().Name);
        var shadow = ParseShadow(shadowLines);
        var lastLogins = ParseLastLog(lastlog);
        var lastFallback = ParseLastLog(last);
        var who = ParseWho(GetSectionText(sections, "WHO"));
        var failedLogins = ParseFailedLogins(GetSectionText(sections, "LASTB"));
        var authorizedKeys = ParseAuthorizedKeys(GetSectionText(sections, "KEYS"));
        var sudoText = GetSectionText(sections, "SUDO") ?? string.Empty;
        var uidZeroCount = passwd.Count(line => line.Split(':') is { Length: >= 3 } fields &&
                                                  fields[2] == "0");

        foreach (var line in passwd)
        {
            var fields = line.Split(':');
            if (fields.Length < 7 || !int.TryParse(fields[2], out var uid) ||
                !int.TryParse(fields[3], out var gid))
                continue;
            var name = fields[0];
            var shell = fields[6];
            shadow.TryGetValue(name, out var shadowEntry);
            var userGroups = groups.Where(g => g.Members.Contains(name, StringComparer.OrdinalIgnoreCase) || g.Gid == gid)
                .Select(g => g.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var privileged = uid == 0 || userGroups.Any(g =>
                g.Equals("wheel", StringComparison.OrdinalIgnoreCase) ||
                g.Equals("sudo", StringComparison.OrdinalIgnoreCase));
            var noShell = NoLoginShells.Contains(shell) || shell.EndsWith("nologin", StringComparison.OrdinalIgnoreCase);
            var status = shadowEntry?.Status ?? "未知（shadow 不可读）";
            var hash = shadowEntry?.Hash ?? string.Empty;
            var hashAlgorithm = HashName(hash);
            var isAnonymous = AnonymousNames.Contains(name) ||
                              name.Contains("anonymous", StringComparison.OrdinalIgnoreCase);
            var system = uid < 1000 && uid != 0;
            var notes = new List<string>();
            var risk = false;
            var riskHigh = false;

            if (uid == 0 && name != "root")
            {
                notes.Add("UID=0 但不是 root");
                risk = true;
                riskHigh = true;
            }
            if (uid == 0 && uidZeroCount > 1)
            {
                notes.Add("存在多个 UID=0 账号");
                risk = true;
                riskHigh = true;
            }
            if (isAnonymous)
            {
                notes.Add("匿名/默认账号");
                risk = true;
                riskHigh = true;
            }
            if (status == "空密码")
            {
                notes.Add("空密码");
                risk = true;
                riskHigh = true;
            }
            else if (status == "锁定/无密码")
            {
                notes.Add(system ? "系统账号默认锁定（非本次操作）" : "密码已锁定/无密码");
            }
            if (hashAlgorithm == "MD5")
            {
                notes.Add("使用弱密码哈希 MD5");
                risk = true;
            }
            if (system && !noShell)
            {
                notes.Add("系统账号使用登录 Shell");
                risk = true;
            }
            if (privileged)
                notes.Add("wheel/sudo 或 UID=0 特权账号");
            if (noShell)
                notes.Add("Shell 禁止交互登录");

            var canLogin = noShell
                ? "禁止（nologin）"
                : IsSshDenied(name, sshd)
                    ? "禁止（sshd 限制）"
                    : name == "root" && sshd.TryGetValue("permitrootlogin", out var rootLogin) &&
                      rootLogin.Equals("no", StringComparison.OrdinalIgnoreCase)
                        ? "禁止（root SSH 登录已关闭）"
                        : status == "锁定/无密码"
                            ? "受限（密码锁定，可能密钥）"
                            : "可能（Shell 登录）";
            var lastLogin = lastLogins.TryGetValue(name, out var value)
                ? value
                : lastFallback.TryGetValue(name, out var fallback) ? fallback : "从未/未知";

            Users.Add(new SshUserItem
            {
                UserName = name,
                Uid = uid,
                Gid = gid,
                PrimaryGroup = groupByGid.GetValueOrDefault(gid, gid.ToString()),
                Groups = string.Join(", ", userGroups),
                FullName = fields[4],
                Home = fields[5],
                Shell = shell,
                PasswordStatus = status,
                HashAlgorithm = hashAlgorithm,
                AccountStatus = shadowEntry?.AccountStatus ?? status,
                LastLogin = lastLogin,
                CanLogin = canLogin,
                LoginMethod = BuildLoginMethod(sshd, name),
                IsPrivileged = privileged,
                IsRisk = risk,
                RiskLevel = riskHigh ? "高风险" : risk ? "中风险" : "正常",
                RiskNotes = string.Join("；", notes),
                IsSystem = system,
                IsAnonymous = isAnonymous,
                IsHidden = uid == 0 && name != "root" || uidZeroCount > 1 && uid == 0,
                IsLocked = shadowEntry?.Locked == true,
                PasswordChanged = shadowEntry?.PasswordChanged ?? "未知",
                PasswordAge = shadowEntry?.PasswordAge ?? "未知",
                AccountExpires = shadowEntry?.AccountExpires ?? "无",
                SudoRules = BuildSudoRules(name, userGroups, sudoText),
                ActiveSessions = who.TryGetValue(name, out var sessions) ? sessions : "—",
                FailedLogins = failedLogins.TryGetValue(name, out var failures) ? failures.ToString() : "0",
                AuthorizedKeys = authorizedKeys.TryGetValue(name, out var keys) ? keys : new List<SshAuthorizedKey>()
            });
        }

        SshSettingsText = BuildSshSettings(sshd);
        SelectedUser = string.IsNullOrWhiteSpace(previousUser)
            ? null
            : Users.FirstOrDefault(user => string.Equals(user.UserName, previousUser, StringComparison.Ordinal));
    }

    private sealed record UnixGroup(string Name, int Gid, List<string> Members);
    private sealed record ShadowEntry(string Hash, string Status, string AccountStatus,
        string PasswordChanged, string PasswordAge, string AccountExpires, bool Locked);

    private static List<UnixGroup> ParseGroups(List<string> lines)
    {
        var result = new List<UnixGroup>();
        foreach (var line in lines)
        {
            var fields = line.Split(':');
            if (fields.Length < 4 || !int.TryParse(fields[2], out var gid)) continue;
            result.Add(new UnixGroup(fields[0],
                gid,
                fields[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()));
        }
        return result;
    }

    private static Dictionary<string, ShadowEntry> ParseShadow(List<string> lines)
    {
        var result = new Dictionary<string, ShadowEntry>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var fields = line.Split(':');
            if (fields.Length < 2) continue;
            var hash = fields[1];
            var status = hash.Length == 0
                ? "空密码"
                : hash.StartsWith('!') || hash.StartsWith('*')
                    ? "锁定/无密码"
                    : "已设置";
            var account = fields.Length > 7 && fields[7].Length > 0 && int.TryParse(fields[7], out var expire)
                ? expire > 0 ? "有过期时间" : "正常"
                : "正常";
            var changed = fields.Length > 2 && int.TryParse(fields[2], out var lastChange) && lastChange > 0
                ? DateTime.UnixEpoch.AddDays(lastChange).ToLocalTime().ToString("yyyy-MM-dd")
                : "未知";
            var age = fields.Length > 2 && int.TryParse(fields[2], out var lastChange2) && lastChange2 > 0
                ? $"{(DateTime.UtcNow.Date - DateTime.UnixEpoch.AddDays(lastChange2)).Days} 天"
                : "未知";
            var expires = fields.Length > 7 && int.TryParse(fields[7], out var expire2) && expire2 > 0
                ? DateTime.UnixEpoch.AddDays(expire2).ToLocalTime().ToString("yyyy-MM-dd")
                : "无";
            var locked = hash.StartsWith('!') || hash.StartsWith('*');
            result[fields[0]] = new ShadowEntry(hash, status, account, changed, age, expires, locked);
        }
        return result;
    }

    private static Dictionary<string, string> ParseWho(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || !IsValidUserName(line.Split(' ', 2)[0])) continue;
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;
            result[tokens[0]] = string.Join(" ", tokens.Skip(1));
        }
        return result;
    }

    private static Dictionary<string, int> ParseFailedLogins(string? text)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("btmp begins", StringComparison.OrdinalIgnoreCase)) continue;
            var user = line.Split(' ', 2)[0];
            if (!IsValidUserName(user)) continue;
            result[user] = result.GetValueOrDefault(user) + 1;
        }
        return result;
    }

    private static Dictionary<string, List<SshAuthorizedKey>> ParseAuthorizedKeys(string? text)
    {
        var result = new Dictionary<string, List<SshAuthorizedKey>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return result;
        string? file = null;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("@@KEYFILE ", StringComparison.Ordinal))
            {
                file = line["@@KEYFILE ".Length..].Trim();
                continue;
            }
            if (file is null || !line.StartsWith("@@KEYLINE ", StringComparison.Ordinal)) continue;
            var match = Regex.Match(line, @"^@@KEYLINE\s+(\d+)\s+(.*)$");
            if (!match.Success) continue;
            var lineNumber = int.TryParse(match.Groups[1].Value, out var number) ? number : 0;
            var fingerprintLine = match.Groups[2].Value.Trim();
            var fp = Regex.Match(fingerprintLine, @"^(\d+)\s+(\S+)\s+(.*?)(?:\s+\(([^)]+)\))?$");
            var user = UserFromAuthorizedKeysPath(file);
            if (user.Length == 0) continue;
            if (!result.TryGetValue(user, out var list))
                result[user] = list = new List<SshAuthorizedKey>();
            list.Add(new SshAuthorizedKey
            {
                FilePath = file,
                UserName = user,
                LineNumber = lineNumber,
                Fingerprint = fp.Success ? fp.Groups[2].Value : fingerprintLine,
                KeyType = fp.Success && fp.Groups[4].Success ? fp.Groups[4].Value : "未知",
                Comment = fp.Success ? fp.Groups[3].Value.Trim() : string.Empty
            });
        }
        return result;
    }

    private static string UserFromAuthorizedKeysPath(string path)
    {
        if (path.StartsWith("/root/", StringComparison.Ordinal)) return "root";
        var parts = path.Split('/');
        return parts.Length >= 4 && parts[1] == "home" ? parts[2] : string.Empty;
    }

    private static string BuildSudoRules(string user, List<string> groups, string sudoText)
    {
        if (string.IsNullOrWhiteSpace(sudoText)) return "未发现";
        var hits = sudoText.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Where(line => ContainsWord(line, user) || groups.Any(group => ContainsWord(line, group)))
            .Take(3)
            .ToList();
        return hits.Count == 0 ? "未发现直接规则" : string.Join(" | ", hits);
    }

    private static bool ContainsWord(string value, string word) =>
        Regex.IsMatch(value, $@"(^|[^A-Za-z0-9_.-]){Regex.Escape(word)}([^A-Za-z0-9_.-]|$)");
    private static Dictionary<string, string> ParseLastLog(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("Username", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("wtmp", StringComparison.OrdinalIgnoreCase) || line.StartsWith("reboot", StringComparison.OrdinalIgnoreCase))
                continue;
            var match = Regex.Match(line, @"^(\S+)\s+(.*)$");
            if (!match.Success || !IsValidUserName(match.Groups[1].Value)) continue;
            var rest = match.Groups[2].Value.Trim();
            if (rest.Contains("Never logged in", StringComparison.OrdinalIgnoreCase))
                result[match.Groups[1].Value] = "从未登录";
            else
                result[match.Groups[1].Value] = RestrictDate(rest);
        }
        return result;
    }

    private static string RestrictDate(string value)
    {
        var match = Regex.Match(value, @"((?:Mon|Tue|Wed|Thu|Fri|Sat|Sun)\s+\w+\s+\d+\s+\d+:\d+(?::\d+)?(?:\s+[+-]\d{4})?(?:\s+\d{4})?)");
        return match.Success ? match.Groups[1].Value.Trim() : value;
    }

    private static Dictionary<string, string> ParseSshd(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var space = line.IndexOfAny(new[] { ' ', '\t' });
            if (space <= 0) continue;
            var key = line[..space].Trim().ToLowerInvariant();
            if (key is "permitrootlogin" or "passwordauthentication" or "pubkeyauthentication" or
                "permitemptypasswords" or "allowusers" or "denyusers" or "kbdinteractiveauthentication" or
                "challengeresponseauthentication" or "usepam")
                result.TryAdd(key, line[(space + 1)..].Trim());
        }
        return result;
    }

    private static bool IsSshDenied(string user, Dictionary<string, string> settings)
    {
        if (settings.TryGetValue("denyusers", out var deny) &&
            deny.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(value => value.Split('@')[0] == user))
            return true;
        if (settings.TryGetValue("allowusers", out var allow))
        {
            var values = allow.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (values.Length > 0 && !values.Any(value => value.Split('@')[0] == user || value == "*"))
                return true;
        }
        return false;
    }

    private static string BuildLoginMethod(Dictionary<string, string> settings, string user)
    {
        var methods = new List<string>();
        if (!settings.TryGetValue("passwordauthentication", out var password) ||
            !password.Equals("no", StringComparison.OrdinalIgnoreCase))
            methods.Add("密码");
        if (!settings.TryGetValue("pubkeyauthentication", out var pubkey) ||
            !pubkey.Equals("no", StringComparison.OrdinalIgnoreCase))
            methods.Add("公钥");
        if (user == "root" && settings.TryGetValue("permitrootlogin", out var rootLogin) &&
            rootLogin.Equals("no", StringComparison.OrdinalIgnoreCase))
            return "禁止 root SSH";
        return methods.Count == 0 ? "未知" : string.Join(" + ", methods);
    }

    private static string BuildSshSettings(Dictionary<string, string> settings)
    {
        if (settings.Count == 0) return "SSH 配置不可读";
        var parts = new List<string>();
        foreach (var key in new[] { "permitrootlogin", "passwordauthentication", "pubkeyauthentication", "permitemptypasswords" })
            if (settings.TryGetValue(key, out var value)) parts.Add($"{key}={value}");
        if (settings.TryGetValue("allowusers", out var allow)) parts.Add("AllowUsers=" + allow);
        if (settings.TryGetValue("denyusers", out var deny)) parts.Add("DenyUsers=" + deny);
        return string.Join("；", parts);
    }

    private static string HashName(string hash)
    {
        if (hash.StartsWith("$1$", StringComparison.Ordinal)) return "MD5";
        if (hash.StartsWith("$2", StringComparison.Ordinal)) return "bcrypt";
        if (hash.StartsWith("$5$", StringComparison.Ordinal)) return "SHA-256";
        if (hash.StartsWith("$6$", StringComparison.Ordinal)) return "SHA-512";
        if (hash.StartsWith("$y$", StringComparison.Ordinal)) return "yescrypt";
        if (hash.StartsWith("$7$", StringComparison.Ordinal)) return "scrypt";
        if (hash.Length == 0) return "无密码";
        return "未知";
    }

    private static bool IsPermissionDenied(CommandResult result)
    {
        var text = (result.Error + "\n" + result.Output).ToLowerInvariant();
        return text.Contains("permission denied", StringComparison.Ordinal) ||
               text.Contains("operation not permitted", StringComparison.Ordinal) ||
               text.Contains("you need to be root", StringComparison.Ordinal) ||
               text.Contains("must be root", StringComparison.Ordinal);
    }

    private static Dictionary<string, List<string>> ParseSections(string output)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? current = null;
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("##") && line.Length > 2)
            {
                current = line[2..].Trim();
                sections[current] = new List<string>();
            }
            else if (current is not null)
                sections[current].Add(line);
        }
        return sections;
    }

    private static List<string> GetSection(Dictionary<string, List<string>> sections, string name) =>
        sections.TryGetValue(name, out var lines) ? lines : new List<string>();

    private static string? GetSectionText(Dictionary<string, List<string>> sections, string name) =>
        sections.TryGetValue(name, out var lines) ? string.Join('\n', lines) : null;

    private bool FilterUser(object item)
    {
        if (item is not SshUserItem user) return false;
        if (ShowOnlyRisk && !user.IsRisk) return false;
        var text = SearchText.Trim();
        if (text.Length == 0) return true;
        return user.UserName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
               user.Uid.ToString().Contains(text, StringComparison.Ordinal) ||
               user.Groups.Contains(text, StringComparison.OrdinalIgnoreCase) ||
               user.Shell.Contains(text, StringComparison.OrdinalIgnoreCase) ||
               user.LastLogin.Contains(text, StringComparison.OrdinalIgnoreCase) ||
               user.RiskNotes.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidUserName(string name) =>
        Regex.IsMatch(name, "^[a-zA-Z0-9_.][a-zA-Z0-9_.-]*$") && name.Length <= 64;

    private void ExportCsv()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV 文件|*.csv",
            FileName = $"ssh-users-{SafeFilePart(Server.Name)}.csv",
            AddExtension = true,
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("用户,UID,主组,附加组,Shell,密码状态,哈希算法,账号状态,可登录,登录方式,最后登录,特权,风险,风险说明,主目录");
            foreach (var user in UsersView.Cast<SshUserItem>())
                sb.AppendLine(string.Join(",", new[]
                {
                    Csv(user.UserName), Csv(user.Uid.ToString()), Csv(user.PrimaryGroup), Csv(user.Groups),
                    Csv(user.Shell), Csv(user.PasswordStatus), Csv(user.HashAlgorithm), Csv(user.AccountStatus),
                    Csv(user.CanLogin), Csv(user.LoginMethod), Csv(user.LastLogin),
                    Csv(user.IsPrivileged ? "是" : "否"), Csv(user.RiskLevel), Csv(user.RiskNotes), Csv(user.Home)
                }));
            File.WriteAllText(dialog.FileName, sb.ToString());
            StatusText = "用户清单已导出：" + dialog.FileName;
        }
        catch (Exception ex)
        {
            _dialogs.Error("导出失败：" + ex.Message, "用户管理");
        }
    }

    private static string Csv(string? value) =>
        "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

    private static string SafeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var text = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim('_', ' ');
        return text.Length == 0 ? "server" : text;
    }

    private bool CanManage => !IsBusy && SelectedUser is not null;
    private bool CanLock => CanManage && SelectedUser is { IsLocked: false, UserName: not "root" } && !IsCurrentUser;
    private bool CanUnlock => CanManage && SelectedUser is { IsLocked: true, UserName: not "root" } && !IsCurrentUser;
    private bool CanDisableShell => CanManage && SelectedUser is { UserName: not "root" } u1 && !IsCurrentUser &&
                                    !NoLoginShells.Contains(u1.Shell) && !u1.Shell.EndsWith("nologin", StringComparison.OrdinalIgnoreCase);
    private bool CanRestoreShell => CanManage && SelectedUser is { UserName: not "root" } u2 && !IsCurrentUser &&
                                    (NoLoginShells.Contains(u2.Shell) || u2.Shell.EndsWith("nologin", StringComparison.OrdinalIgnoreCase));
    private bool CanRemoveSudo => CanManage && SelectedUser is { IsPrivileged: true, UserName: not "root" } && !IsCurrentUser;
    private bool CanDeleteKey => CanManage && SelectedKey is not null && SelectedUser is { UserName: not "root" } && !IsCurrentUser;
    private bool IsCurrentUser => SelectedUser is { } u && string.Equals(u.UserName, Server.Username, StringComparison.OrdinalIgnoreCase);

    private void RaiseActionCanExecute()
    {
        (LockCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (UnlockCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DisableShellCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RestoreShellCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RemoveSudoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteKeyCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private Task LockUserAsync() => RunUserActionAsync("锁定账号", user => $"usermod -L {ShellQuote.Single(user.UserName)}");
    private Task UnlockUserAsync() => RunUserActionAsync("解锁账号", user => $"usermod -U {ShellQuote.Single(user.UserName)}");
    private Task DisableShellAsync() => RunUserActionAsync("禁止 Shell 登录",
        user => $"usermod -s /sbin/nologin {ShellQuote.Single(user.UserName)}");
    private Task RestoreShellAsync() => RunUserActionAsync("恢复 Shell 登录",
        user => $"usermod -s /bin/bash {ShellQuote.Single(user.UserName)}");
    private Task RemoveSudoAsync() => RunUserActionAsync("移除 sudo/wheel 权限",
        user => $"gpasswd -d {ShellQuote.Single(user.UserName)} wheel 2>/dev/null || true; " +
                $"gpasswd -d {ShellQuote.Single(user.UserName)} sudo 2>/dev/null || true");

    private async Task RunUserActionAsync(string action, Func<SshUserItem, string> commandFactory)
    {
        if (SelectedUser is not { } user || IsBusy) return;
        if (Server.SudoType == SudoType.None)
        {
            _dialogs.Warn("该服务器未配置 sudo/Su 提权，无法执行用户操作。", "用户管理");
            return;
        }
        if (string.Equals(user.UserName, Server.Username, StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.Warn("不能修改当前 SSH 登录账号，避免断开连接。", "用户管理");
            return;
        }
        if (!_dialogs.Confirm($"确认对账号「{user.UserName}」执行「{action}」？", "用户管理", warning: true))
            return;

        IsBusy = true;
        StatusText = $"正在{action}：{user.UserName}…";
        try
        {
            var command = CommandPrefix + commandFactory(user);
            var result = await _ssh.ExecuteWithSudoAsync(Server, command);
            if (!result.Success)
            {
                StatusText = $"{action}失败：{(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error)}";
                _dialogs.Error(StatusText, "用户管理");
                return;
            }
            await AuditActionAsync(action, command, result);
            var verify = await _ssh.ExecuteWithSudoAsync(Server,
                CommandPrefix + $"passwd -S {ShellQuote.Single(user.UserName)} 2>&1");
            var state = verify.Success && !string.IsNullOrWhiteSpace(verify.Output)
                ? " · " + verify.Output.Trim()
                : string.Empty;
            StatusText = $"{action}成功：{user.UserName}{state}";
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync(true);
    }

    private async Task DeleteKeyAsync()
    {
        if (SelectedUser is not { } user || SelectedKey is not { } key || IsBusy) return;
        if (Server.SudoType == SudoType.None)
        {
            _dialogs.Warn("该服务器未配置 sudo/Su 提权，无法删除授权密钥。", "用户管理");
            return;
        }
        if (string.Equals(user.UserName, Server.Username, StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.Warn("不能删除当前 SSH 登录账号的授权密钥，避免断开连接。", "用户管理");
            return;
        }
        if (!IsSafePath(key.FilePath) || key.LineNumber <= 0)
        {
            _dialogs.Warn("授权密钥路径或行号无效，已拒绝操作。", "用户管理");
            return;
        }
        if (!_dialogs.Confirm(
                $"确认删除账号「{user.UserName}」的授权密钥？\n\n{key.Fingerprint}\n{key.Comment}",
                "用户管理", warning: true))
            return;

        IsBusy = true;
        StatusText = "正在备份并删除授权密钥…";
        try
        {
            var cat = await _ssh.ExecuteWithSudoAsync(Server, CommandPrefix + $"cat {ShellQuote.Single(key.FilePath)}");
            if (cat.Success)
                BackupKeyFile(user.UserName, key.FilePath, cat.Output);
            var command = CommandPrefix +
                          $"sed -i {ShellQuote.Single(key.LineNumber + "d")} {ShellQuote.Single(key.FilePath)}";
            var result = await _ssh.ExecuteWithSudoAsync(Server, command);
            if (!result.Success)
            {
                StatusText = "删除授权密钥失败：" +
                             (string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
                return;
            }
            await AuditActionAsync("删除授权密钥", command, result);
            StatusText = "授权密钥已删除。";
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync(true);
    }

    private void BackupKeyFile(string user, string path, string content)
    {
        try
        {
            var dir = Path.Combine(ConfigPaths.AppDataDir, "user-backups");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir,
                $"{SafeFilePart(Server.Name)}-{SafeFilePart(user)}-{DateTime.Now:yyyyMMdd-HHmmss}.authorized_keys");
            File.WriteAllText(file, content);
        }
        catch
        {
            // 备份失败时仍继续删除；操作已确认
        }
    }

    private async Task AuditActionAsync(string action, string command, CommandResult result)
    {
        if (_audit is null) return;
        try
        {
            await _audit.LogCommandAsync(new CommandAuditLog
            {
                ServerId = Server.Id,
                ServerName = Server.Name,
                Tool = "user_manage",
                Command = command,
                Status = result.Success ? CommandStatus.Executed : CommandStatus.Failed,
                Result = $"{action}; exit={result.ExitCode}",
                ExitCode = result.ExitCode,
                Category = AuditCategory.Exec
            });
        }
        catch
        {
            // 审计失败不改变操作结果
        }
    }

    private static bool IsSafePath(string path) =>
        path.StartsWith('/') && !path.Contains("..", StringComparison.Ordinal) &&
        !path.Contains('\n') && !path.Contains('\r');
    private async Task AuditAsync()
    {
        if (_audit is null) return;
        try
        {
            await _audit.LogCommandAsync(new CommandAuditLog
            {
                ServerId = Server.Id,
                ServerName = Server.Name,
                Tool = "user_manager",
                Command = _elevated ? "提权读取用户清单" : "读取用户清单",
                Status = CommandStatus.Executed,
                Result = $"users={Users.Count}, privileged={PrivilegedUsers}, risk={RiskUsers}",
                Category = AuditCategory.Exec
            });
        }
        catch
        {
            // 审计失败不影响用户清单展示
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
