using System.Globalization;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.Core.Services.Snapshot.Collectors;

/// <summary>
/// 服务器账号安全快照：账号/密码状态/特权组/SSH 公钥/最近登录/失败登录与风险发现。
/// 命令与用户管理页的账号审计保持一致；root 直接采集，非 root 有 sudo 时提权，
/// 无 sudo 时按普通账号能力采集并标记 degraded。
/// </summary>
public sealed class AccountsSnapshotCollector : ISnapshotCollector
{
    public string Name => "accounts";
    public int Order => 44;

    private static string BuildAuditCommand(bool includeKeys, bool includeLogins)
    {
        var parts = new List<string>
        {
            "echo '##PASSWD'; getent passwd 2>/dev/null || cat /etc/passwd 2>/dev/null; " +
            "echo '##GROUP'; getent group 2>/dev/null | head -1200; " +
            "echo '##SHADOW'; cat /etc/shadow 2>/dev/null | head -2000;"
        };

        if (includeLogins)
        {
            parts.Add(
                "echo '##LASTLOG'; lastlog 2>/dev/null | head -1200; " +
                "echo '##LAST'; last -n 120 2>/dev/null; " +
                "echo '##WHO'; who 2>/dev/null | head -200; " +
                "echo '##LASTB'; lastb -n 120 2>/dev/null | head -150;");
        }

        if (includeKeys)
        {
            parts.Add(
                "echo '##KEYS'; for f in /root/.ssh/authorized_keys /home/*/.ssh/authorized_keys; do [ -f \"$f\" ] || continue; " +
                "echo \"@@KEYFILE $f $(stat -c '%Y' \"$f\" 2>/dev/null) $(stat -c '%a' \"$f\" 2>/dev/null)\"; " +
                "awk 'NF && $1 !~ /^#/ {print NR\"\\t\"$0}' \"$f\" | head -200 | while IFS=\"$(printf '\\t')\" read -r n line; do " +
                "fp=$(printf '%s\\n' \"$line\" | ssh-keygen -lf - 2>/dev/null | head -1); echo \"@@KEYLINE $n $fp\"; done; done;");
        }

        parts.Add(
            "echo '##SSHD'; (sshd -T 2>/dev/null || grep -hiE '^[[:space:]]*(PermitRootLogin|PasswordAuthentication|PubkeyAuthentication|PermitEmptyPasswords|AllowUsers|DenyUsers|KbdInteractiveAuthentication|ChallengeResponseAuthentication|UsePAM)[[:space:]]' /etc/ssh/sshd_config /etc/ssh/sshd_config.d/*.conf 2>/dev/null); " +
            "echo '##SUDO'; getent group wheel sudo 2>/dev/null; grep -rhE '^[^#]*(wheel|sudo|NOPASSWD)' /etc/sudoers /etc/sudoers.d/ 2>/dev/null | head -200; true");
        return string.Join(' ', parts);
    }

    public async Task<CollectorResult> CollectAsync(SnapshotContext context, CancellationToken ct)
    {
        if (context.Options is { CollectAccounts: false })
            return CollectorResult.Skip("账号采集已在 snapshot.collectAccounts 中关闭");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var command = BuildAuditCommand(
            includeKeys: context.Options?.CollectAuthorizedKeys ?? true,
            includeLogins: context.Options?.CollectLoginHistory ?? true);
        var result = await context.ExecuteAsync(command, context.Elevate, ct);
        if (!result.Success && result.ErrorKind is not null)
            return CollectorResult.FromCommandFailure(result) with { DurationMs = sw.Elapsed.TotalMilliseconds };

        var data = AccountAuditParser.Parse(result.Output, DateTime.UtcNow);
        if (data.Users.Count > 0)
        {
            context.Shared["accounts.emptyPassword"] = data.Users
                .Where(u => (string?)u["passwordStatus"] == "空密码")
                .Select(u => (string)u["name"]!)
                .ToList();
            context.Shared["accounts.nopasswd"] = data.NopasswdSudoers;
        }
        var degraded = !data.ShadowReadable;
        var note = degraded
            ? "shadow 不可读（当前账号无提权或权限不足），密码状态/部分账号检查不完整"
            : null;
        sw.Stop();
        return CollectorResult.Ok(data.ToDictionary(), degraded, note) with
        {
            DurationMs = sw.Elapsed.TotalMilliseconds
        };
    }
}

/// <summary>账号审计结果（快照与后续用户管理可复用）。</summary>
public sealed class AccountAuditData
{
    public List<Dictionary<string, object?>> Users { get; } = new();
    public List<Dictionary<string, object?>> AuthorizedKeys { get; } = new();
    public List<Dictionary<string, object?>> RecentLogins { get; } = new();
    public List<Dictionary<string, object?>> FailedLogins { get; } = new();
    public List<Dictionary<string, object?>> Findings { get; } = new();
    public Dictionary<string, object?> Summary { get; } = new();
    public List<string> NopasswdSudoers { get; } = new();
    public bool ShadowReadable { get; set; }

    public Dictionary<string, object?> ToDictionary() => new()
    {
        ["users"] = Users,
        ["authorizedKeys"] = AuthorizedKeys,
        ["recentLogins"] = RecentLogins,
        ["failedLogins"] = FailedLogins,
        ["findings"] = Findings,
        ["summary"] = Summary,
        ["shadowReadable"] = ShadowReadable
    };
}

/// <summary>解析 passwd/group/shadow/lastlog/last/lastb/authorized_keys/sshd/sudoers。</summary>
public static class AccountAuditParser
{
    private static readonly HashSet<string> NoLoginShells = new(StringComparer.OrdinalIgnoreCase)
    {
        "/sbin/nologin", "/usr/sbin/nologin", "/bin/false", "/usr/bin/false", "/bin/sync"
    };

    private static readonly HashSet<string> AnonymousNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "anonymous", "ftp", "guest", "nobody", "nfsnobody", "unknown", "test", "temp", "default"
    };

    private sealed record UnixGroup(string Name, int Gid, List<string> Members);
    private sealed record ShadowEntry(string Hash, string Status, string HashAlgorithm, bool Locked,
        string PasswordChanged, string PasswordAge, string AccountExpires, bool Expired, bool NeverChanged);

    private sealed class KeyFile
    {
        public string User = string.Empty;
        public string Path = string.Empty;
        public string ModifiedAt = string.Empty;
        public string Mode = string.Empty;
        public List<Dictionary<string, object?>> Keys { get; } = new();
    }

    public static AccountAuditData Parse(string output, DateTime now)
    {
        var sections = SplitSections(output);
        var passwd = sections.GetValueOrDefault("PASSWD") ?? new List<string>();
        var groupLines = sections.GetValueOrDefault("GROUP") ?? new List<string>();
        var shadowLines = sections.GetValueOrDefault("SHADOW") ?? new List<string>();
        var groups = ParseGroups(groupLines);
        var groupByGid = groups.GroupBy(g => g.Gid).ToDictionary(g => g.Key, g => g.First().Name);
        var shadow = ParseShadow(shadowLines, now);
        var lastLogins = ParseLastLog(GetSectionText(sections, "LASTLOG"));
        var lastFallback = ParseLast(GetSectionText(sections, "LAST"));
        var who = ParseWho(GetSectionText(sections, "WHO"));
        var failed = ParseLastb(GetSectionText(sections, "LASTB"));
        var keyFiles = ParseKeys(GetSectionText(sections, "KEYS"));
        var sshd = ParseSshd(GetSectionText(sections, "SSHD"));
        var sudo = ParseSudo(GetSectionText(sections, "SUDO"));
        var keysByUser = keyFiles
            .GroupBy(k => k.User, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var data = new AccountAuditData { ShadowReadable = shadow.Count > 0 };
        data.NopasswdSudoers.AddRange(sudo.Nopasswd);
        var uidZero = passwd.Count(line => line.Split(':') is { Length: >= 3 } f && f[2] == "0");

        foreach (var line in passwd)
        {
            var fields = line.Split(':');
            if (fields.Length < 7 || !int.TryParse(fields[2], out var uid) ||
                !int.TryParse(fields[3], out var gid))
                continue;

            var name = fields[0];
            var shell = fields[6];
            shadow.TryGetValue(name, out var shadowEntry);
            var userGroups = groups
                .Where(g => g.Members.Contains(name, StringComparer.OrdinalIgnoreCase) || g.Gid == gid)
                .Select(g => g.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var privileged = uid == 0 || userGroups.Any(g =>
                g.Equals("wheel", StringComparison.OrdinalIgnoreCase) ||
                g.Equals("sudo", StringComparison.OrdinalIgnoreCase));
            var noShell = NoLoginShells.Contains(shell) ||
                          shell.EndsWith("nologin", StringComparison.OrdinalIgnoreCase);
            var system = uid < 1000 && uid != 0;
            var anonymous = AnonymousNames.Contains(name) ||
                            name.Contains("anonymous", StringComparison.OrdinalIgnoreCase);
            var keys = keysByUser.TryGetValue(name, out var keyFileList)
                ? keyFileList.Sum(k => k.Keys.Count)
                : 0;
            var failedCount = failed.TryGetValue(name, out var failedList) ? failedList.Count : 0;
            var lastLogin = lastLogins.TryGetValue(name, out var lastLoginValue)
                ? lastLoginValue
                : lastFallback.TryGetValue(name, out var lastFallbackValue) ? lastFallbackValue : "从未/未知";
            var active = who.TryGetValue(name, out var activeValue) ? activeValue : "—";

            var riskNotes = new List<string>();
            var riskLevel = "正常";
            void Risk(string severity, string note)
            {
                riskNotes.Add(note);
                if (severity == "high" || (severity == "medium" && riskLevel != "高风险"))
                    riskLevel = severity == "high" ? "高风险" : "中风险";
            }

            if (uid == 0 && name != "root")
                Risk("high", "UID=0 但不是 root");
            if (uidZero > 1 && uid == 0)
                Risk("high", "存在多个 UID=0 账号");
            if (anonymous)
                Risk("high", "匿名/默认账号");
            if (shadowEntry?.Status == "空密码")
                Risk("high", "空密码");
            if (shadowEntry?.HashAlgorithm == "MD5")
                Risk("medium", "使用弱密码哈希 MD5");
            if (system && !noShell)
                Risk("medium", "系统账号使用登录 Shell");
            if (shadowEntry?.NeverChanged == true)
                Risk("medium", "密码从未修改/需立即修改");
            if (shadowEntry?.Expired == true)
                Risk("high", "密码已过期");
            if (privileged)
                riskNotes.Add("wheel/sudo 或 UID=0 特权账号");
            if (noShell)
                riskNotes.Add("Shell 禁止交互登录");
            if (failedCount > 20)
                Risk("medium", $"失败登录 {failedCount} 次（>20）");

            data.Users.Add(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["uid"] = uid,
                ["gid"] = gid,
                ["primaryGroup"] = groupByGid.GetValueOrDefault(gid, gid.ToString(CultureInfo.InvariantCulture)),
                ["groups"] = userGroups,
                ["home"] = fields[5],
                ["shell"] = shell,
                ["passwordStatus"] = shadowEntry?.Status ?? "未知（shadow 不可读）",
                ["hashAlgorithm"] = shadowEntry?.HashAlgorithm ?? string.Empty,
                ["locked"] = shadowEntry?.Locked ?? false,
                ["passwordChanged"] = shadowEntry?.PasswordChanged ?? "未知",
                ["passwordAge"] = shadowEntry?.PasswordAge ?? "未知",
                ["accountExpires"] = shadowEntry?.AccountExpires ?? "无",
                ["canLogin"] = noShell ? "禁止（nologin）" : "可能（Shell 登录）",
                ["isPrivileged"] = privileged,
                ["isSystem"] = system,
                ["isAnonymous"] = anonymous,
                ["lastLogin"] = lastLogin,
                ["activeSessions"] = active,
                ["failedLogins"] = failedCount,
                ["authorizedKeyCount"] = keys,
                ["riskLevel"] = riskLevel,
                ["riskNotes"] = riskNotes
            });

            if (riskLevel != "正常")
                AddFinding(data, riskLevel == "高风险" ? "high" : "medium",
                    "user_risk", $"{name} 账号风险", string.Join("；", riskNotes));
        }

        foreach (var keyFile in keyFiles)
        {
            data.AuthorizedKeys.Add(new Dictionary<string, object?>
            {
                ["user"] = keyFile.User,
                ["path"] = keyFile.Path,
                ["keyCount"] = keyFile.Keys.Count,
                ["modifiedAt"] = keyFile.ModifiedAt,
                ["mode"] = keyFile.Mode,
                ["keys"] = keyFile.Keys
            });

            if (keyFile.Keys.Count > 5)
                AddFinding(data, "medium", "many_authorized_keys",
                    $"{keyFile.User} authorized_keys 数量较多（{keyFile.Keys.Count}）", keyFile.Path);
            if (DateTime.TryParse(keyFile.ModifiedAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var modified) &&
                now - modified.ToUniversalTime() <= TimeSpan.FromDays(7))
            {
                AddFinding(data, "info", "recent_authorized_key",
                    $"{keyFile.User} 的 authorized_keys 最近 7 天内有修改", keyFile.Path);
            }
            if (keyFile.Mode is { Length: > 0 } mode &&
                (mode.EndsWith('2') || mode.EndsWith('3') || mode.EndsWith('6') || mode.EndsWith('7')))
            {
                AddFinding(data, "medium", "authorized_keys_writable",
                    $"{keyFile.User} 的 authorized_keys 权限为 {mode}（组/其他用户可写）", keyFile.Path);
            }
        }

        foreach (var login in lastLogins
                     .Where(kv => !string.IsNullOrWhiteSpace(kv.Value) &&
                                  !kv.Value.Contains("Never logged in", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(kv => kv.Key)
                     .Take(100))
        {
            data.RecentLogins.Add(new Dictionary<string, object?>
            {
                ["user"] = login.Key,
                ["detail"] = login.Value
            });
        }
        foreach (var (user, list) in failed)
        {
            foreach (var item in list.Take(50))
                data.FailedLogins.Add(new Dictionary<string, object?>
                {
                    ["user"] = user,
                    ["detail"] = item
                });
        }

        AddFindingIf(data, sshd.GetValueOrDefault("permitrootlogin") is "yes",
            "high", "permit_root_login", "SSH 允许 root 直接登录", "PermitRootLogin yes");
        AddFindingIf(data, sshd.GetValueOrDefault("passwordauthentication") is "yes",
            "medium", "password_auth_enabled", "SSH 启用了密码认证", "PasswordAuthentication yes");
        AddFindingIf(data, sshd.GetValueOrDefault("permitemptypasswords") is "yes",
            "high", "empty_password_login", "SSH 允许空密码登录", "PermitEmptyPasswords yes");
        if (sudo.Nopasswd.Count > 0)
            AddFinding(data, "medium", "sudo_nopasswd",
                "sudoers 存在 NOPASSWD 配置", string.Join("；", sudo.Nopasswd.Take(10)));

        data.Summary["users"] = data.Users.Count;
        data.Summary["riskyUsers"] = data.Users.Count(u => (string?)u["riskLevel"] != "正常");
        data.Summary["authorizedKeys"] = data.AuthorizedKeys.Sum(k => (int)k["keyCount"]!);
        data.Summary["failedUsers"] = failed.Count;
        data.Summary["findings"] = data.Findings.Count;
        data.Summary["high"] = data.Findings.Count(f => (string?)f["severity"] == "high");
        data.Summary["medium"] = data.Findings.Count(f => (string?)f["severity"] == "medium");
        data.Summary["info"] = data.Findings.Count(f => (string?)f["severity"] == "info");
        return data;
    }

    private static Dictionary<string, List<string>> SplitSections(string output)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        List<string>? current = null;
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("##", StringComparison.Ordinal) && line.Length > 2)
            {
                current = new List<string>();
                result[line[2..].Trim()] = current;
                continue;
            }
            current?.Add(line);
        }
        return result;
    }

    private static string GetSectionText(Dictionary<string, List<string>> sections, string key) =>
        sections.TryGetValue(key, out var lines) ? string.Join('\n', lines) : string.Empty;

    private static List<UnixGroup> ParseGroups(List<string> lines)
    {
        var result = new List<UnixGroup>();
        foreach (var line in lines)
        {
            var fields = line.Split(':');
            if (fields.Length < 4 || !int.TryParse(fields[2], out var gid))
                continue;
            result.Add(new UnixGroup(fields[0], gid,
                fields[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()));
        }
        return result;
    }

    private static Dictionary<string, ShadowEntry> ParseShadow(List<string> lines, DateTime now)
    {
        var result = new Dictionary<string, ShadowEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var fields = line.Split(':');
            if (fields.Length < 2)
                continue;
            var name = fields[0];
            var hash = fields[1];
            var status = hash.Length == 0 ? "空密码" : hash[0] is '!' or '*' ? "锁定/无密码" : "已设置";
            var hashAlgorithm = HashName(hash);
            var lastChange = ParseInt(fields.ElementAtOrDefault(2), -1);
            var maxDays = ParseInt(fields.ElementAtOrDefault(4), -1);
            var expireDays = ParseInt(fields.ElementAtOrDefault(7), -1);
            var changed = lastChange > 0
                ? DateTime.UnixEpoch.AddDays(lastChange).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "未知";
            var age = lastChange > 0 ? Math.Max(0, (now - DateTime.UnixEpoch.AddDays(lastChange)).Days) : -1;
            var expiredByMax = maxDays > 0 && age > maxDays;
            var expires = expireDays > 0
                ? DateTime.UnixEpoch.AddDays(expireDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "无";
            var expiredByDate = expireDays > 0 && now > DateTime.UnixEpoch.AddDays(expireDays);
            result[name] = new ShadowEntry(
                hash, status, hashAlgorithm, status != "已设置", changed,
                age >= 0 ? $"{age} 天" : "未知", expires,
                expiredByMax || expiredByDate, lastChange == 0);
        }
        return result;
    }

    private static Dictionary<string, string> ParseLastLog(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("Username", StringComparison.OrdinalIgnoreCase))
                continue;
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 1)
                continue;
            result[tokens[0]] = string.Join(' ', tokens.Skip(1));
        }
        return result;
    }

    private static Dictionary<string, string> ParseLast(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("wtmp", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("reboot", StringComparison.OrdinalIgnoreCase))
                continue;
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
                continue;
            result.TryAdd(tokens[0], string.Join(' ', tokens.Skip(1)));
        }
        return result;
    }

    private static Dictionary<string, string> ParseWho(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
                continue;
            result[tokens[0]] = string.Join(' ', tokens.Skip(1));
        }
        return result;
    }

    private static Dictionary<string, List<string>> ParseLastb(string text)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("btmp", StringComparison.OrdinalIgnoreCase))
                continue;
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
                continue;
            if (!result.TryGetValue(tokens[0], out var list))
                result[tokens[0]] = list = new List<string>();
            list.Add(line);
        }
        return result;
    }

    private static List<KeyFile> ParseKeys(string text)
    {
        var result = new List<KeyFile>();
        KeyFile? current = null;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("@@KEYFILE ", StringComparison.Ordinal))
            {
                var rest = line["@@KEYFILE ".Length..].Trim();
                var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    continue;
                var path = parts[0];
                var modified = parts.Length > 1 && long.TryParse(parts[1], out var epoch) && epoch > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
                    : string.Empty;
                current = new KeyFile
                {
                    Path = path,
                    User = UserFromKeyPath(path),
                    ModifiedAt = modified,
                    Mode = parts.Length > 2 ? parts[2] : string.Empty
                };
                result.Add(current);
            }
            else if (line.StartsWith("@@KEYLINE ", StringComparison.Ordinal) && current is not null)
            {
                var fp = line["@@KEYLINE ".Length..].Trim();
                current.Keys.Add(ParseFingerprint(fp) ?? new Dictionary<string, object?>
                {
                    ["fingerprint"] = string.Empty,
                    ["type"] = string.Empty,
                    ["comment"] = string.Empty
                });
            }
        }
        return result;
    }

    private static string UserFromKeyPath(string path)
    {
        if (path.StartsWith("/root/", StringComparison.Ordinal))
            return "root";
        if (path.StartsWith("/home/", StringComparison.Ordinal))
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
                return parts[1];
        }
        return "unknown";
    }

    private static Dictionary<string, object?>? ParseFingerprint(string fp)
    {
        if (string.IsNullOrWhiteSpace(fp))
            return null;
        var tokens = fp.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return null;
        var fingerprint = tokens.FirstOrDefault(t =>
            t.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("MD5:", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        var type = tokens.LastOrDefault(t => t.StartsWith('(') && t.EndsWith(')'))?.Trim('(', ')') ?? string.Empty;
        var fpIndex = Array.FindIndex(tokens, t => string.Equals(t, fingerprint, StringComparison.OrdinalIgnoreCase));
        var typeIndex = Array.FindIndex(tokens, t => string.Equals(t, $"({type})", StringComparison.OrdinalIgnoreCase));
        var comment = fpIndex >= 0 && typeIndex > fpIndex + 1
            ? string.Join(' ', tokens[(fpIndex + 1)..typeIndex])
            : string.Empty;
        return new Dictionary<string, object?>
        {
            ["fingerprint"] = fingerprint,
            ["type"] = type,
            ["comment"] = comment
        };
    }

    private static Dictionary<string, string> ParseSshd(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var index = line.IndexOfAny(new[] { ' ', '\t' });
            if (index <= 0)
                continue;
            result[line[..index].Trim().ToLowerInvariant()] = line[(index + 1)..].Trim().ToLowerInvariant();
        }
        return result;
    }

    private static (List<(string Name, int Gid, List<string> Members)> Groups, List<string> Nopasswd) ParseSudo(string text)
    {
        var groups = new List<(string Name, int Gid, List<string> Members)>();
        var nopasswd = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line.Contains("NOPASSWD", StringComparison.OrdinalIgnoreCase))
                nopasswd.Add(line);
            var fields = line.Split(':');
            if (fields.Length >= 4 && int.TryParse(fields[2], out var gid))
                groups.Add((fields[0], gid,
                    fields[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()));
        }
        return (groups, nopasswd);
    }

    private static void AddFindingIf(AccountAuditData data, bool condition,
        string severity, string id, string title, string evidence)
    {
        if (condition)
            AddFinding(data, severity, id, title, evidence);
    }

    private static void AddFinding(AccountAuditData data, string severity, string id,
        string title, string evidence)
    {
        data.Findings.Add(new Dictionary<string, object?>
        {
            ["severity"] = severity,
            ["id"] = id,
            ["title"] = title,
            ["detail"] = evidence,
            ["evidence"] = evidence
        });
    }

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;

    private static string HashName(string hash) => hash switch
    {
        { Length: 0 } => string.Empty,
        _ when hash.StartsWith("$1$", StringComparison.Ordinal) => "MD5",
        _ when hash.StartsWith("$2", StringComparison.Ordinal) => "bcrypt",
        _ when hash.StartsWith("$5$", StringComparison.Ordinal) => "SHA256",
        _ when hash.StartsWith("$6$", StringComparison.Ordinal) => "SHA512",
        _ when hash.StartsWith("$y$", StringComparison.Ordinal) => "yescrypt",
        _ when hash.StartsWith("$7$", StringComparison.Ordinal) => "scrypt",
        _ => "未知"
    };
}
