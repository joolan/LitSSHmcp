using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.Core.Services.Snapshot.Collectors;

/// <summary>
/// 安全巡检快照（只读）：SSH 有效配置（PermitRootLogin/PasswordAuthentication/端口…）、防火墙
/// （ufw/firewalld/iptables）暴露面、fail2ban、MySQL 匿名账户/远程 root、系统空口令账户、sudoers NOPASSWD，
/// 以及结合监听端口识别"公网暴露的高危端口"（3306/5432/6379/2375/9200/21/23…）。
/// 输出统一的 findings[]（severity/id/title/detail/evidence）便于审计与后续扩展新指标。
/// </summary>
public sealed class SecurityAuditSnapshotCollector : ISnapshotCollector
{
    public string Name => "security";
    public int Order => 45;

    // SSH 有效配置：优先 sshd -T（含 Include 展开），不可用回退 grep sshd_config(.d)
    private const string SshCommand =
        "(sshd -T 2>/dev/null || grep -hiE '^[[:space:]]*(PermitRootLogin|PasswordAuthentication|PermitEmptyPasswords|" +
        "PubkeyAuthentication|MaxAuthTries|X11Forwarding|ChallengeResponseAuthentication|KbdInteractiveAuthentication|Port)[[:space:]]' " +
        "/etc/ssh/sshd_config /etc/ssh/sshd_config.d/*.conf 2>/dev/null)";

    // 防火墙 + fail2ban 合并：输出按行特征解析（无标记，PTY 回显安全）
    private const string FirewallCommand =
        "(command -v ufw >/dev/null 2>&1 && ufw status verbose 2>/dev/null | head -40); " +
        "(command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state 2>/dev/null; firewall-cmd --list-all 2>/dev/null | head -50); " +
        "(command -v iptables >/dev/null 2>&1 && iptables -S 2>/dev/null | wc -l); " +
        "(command -v fail2ban-client 2>/dev/null; fail2ban-client status 2>/dev/null)";

    // MySQL 账户（免密 best-effort）+ 系统空口令账户 + sudoers NOPASSWD。
    // MySQL 授权独立于 OS 权限: 提权到 root 不代表能连 MySQL。这里依次尝试——① socket + 客户端默认配置
    // (auth_socket / ~/.my.cnf / 免密) ② Debian/Ubuntu 维护账户 /etc/mysql/debian.cnf；都失败则标注未检查。
    private const string AccountsCommand =
        "command -v mysql 2>/dev/null; " +
        "mysql -N -e \"SELECT user,host FROM mysql.user;\" 2>/dev/null; " +
        "mysql --defaults-file=/etc/mysql/debian.cnf -N -e \"SELECT user,host FROM mysql.user;\" 2>/dev/null; " +
        "awk -F: '($2==\"\" && $1!=\"\"){print \"EMPTY_PW:\"$1}' /etc/shadow 2>/dev/null; " +
        "grep -rhE '^[^#]*NOPASSWD' /etc/sudoers /etc/sudoers.d/ 2>/dev/null";

    private const string PortsCommand = "ss -H -tlnp 2>/dev/null";

    /// <summary>高危端口 → (服务名, 风险级别)。仅在公网绑定(0.0.0.0/::/* 或非回环具体地址)时告警。</summary>
    public static readonly IReadOnlyDictionary<int, (string Name, string Severity)> HighRiskPorts =
        new Dictionary<int, (string, string)>
        {
            [21] = ("FTP", "high"),
            [23] = ("Telnet", "high"),
            [25] = ("SMTP", "low"),
            [135] = ("MSRPC", "medium"),
            [139] = ("NetBIOS", "high"),
            [445] = ("SMB", "high"),
            [1433] = ("MSSQL", "high"),
            [1521] = ("Oracle", "medium"),
            [2181] = ("ZooKeeper", "medium"),
            [2375] = ("Docker API(明文)", "high"),
            [2376] = ("Docker API", "medium"),
            [3306] = ("MySQL", "high"),
            [3389] = ("RDP", "high"),
            [5432] = ("PostgreSQL", "high"),
            [5601] = ("Kibana", "medium"),
            [5900] = ("VNC", "high"),
            [6379] = ("Redis", "high"),
            [9200] = ("Elasticsearch", "high"),
            [9300] = ("Elasticsearch", "high"),
            [11211] = ("Memcached", "high"),
            [27017] = ("MongoDB", "high"),
            [27018] = ("MongoDB", "high")
        };

    public sealed record FirewallInfo(string? Ufw, string? Firewalld, int IptablesRules);
    public sealed record Fail2banInfo(bool Installed, bool ServiceRunning, List<string> Jails);
    public sealed record MysqlInfo(bool Installed, bool Checked, List<(string User, string Host)> Accounts, string? Reason);

    public async Task<CollectorResult> CollectAsync(SnapshotContext context, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var sshResult = await context.ExecuteAsync(SshCommand, context.Elevate, ct);
        if (!sshResult.Success && sshResult.ErrorKind is not null)
            return CollectorResult.FromCommandFailure(sshResult) with { DurationMs = sw.Elapsed.TotalMilliseconds };

        var fwResult = await context.ExecuteAsync(FirewallCommand, context.Elevate, ct);
        if (!fwResult.Success && fwResult.ErrorKind is not null)
            return CollectorResult.FromCommandFailure(fwResult) with { DurationMs = sw.Elapsed.TotalMilliseconds };

        var accResult = await context.ExecuteAsync(AccountsCommand, context.Elevate, ct);
        if (!accResult.Success && accResult.ErrorKind is not null)
            return CollectorResult.FromCommandFailure(accResult) with { DurationMs = sw.Elapsed.TotalMilliseconds };

        var portsResult = await context.ExecuteAsync(PortsCommand, context.Elevate, ct);
        if (!portsResult.Success && portsResult.ErrorKind is not null)
            return CollectorResult.FromCommandFailure(portsResult) with { DurationMs = sw.Elapsed.TotalMilliseconds };

        var ssh = ParseSshConfig(sshResult.Output);
        var firewall = ParseFirewall(fwResult.Output);
        var fail2ban = ParseFail2ban(fwResult.Output);
        var (emptyPw, nopasswd, onHostMysql) = ParseAccounts(accResult.Output);

        // MySQL 账户：优先用"匹配该服务器的已配置 MySQL 数据源凭据"精确查询；否则用免密 best-effort 结果
        var mysql = onHostMysql;
        var remoteGrants = new List<MysqlRemoteGrant>();
        if (context.QueryMysqlAccountsAsync is not null)
        {
            var probe = await context.QueryMysqlAccountsAsync(ct);
            if (probe is not null)
            {
                mysql = new MysqlInfo(true, probe.Checked, probe.Accounts, probe.Reason);
                remoteGrants = probe.RemoteGrants ?? new List<MysqlRemoteGrant>();
            }
        }

        var listeners = PortMapSnapshotCollector.ParseSsListeners(portsResult.Output, proto: null);
        var exposedPorts = FindExposedPorts(listeners);

        var findings = BuildFindings(ssh, firewall, fail2ban, mysql, exposedPorts, emptyPw, nopasswd, remoteGrants);
        var summary = new Dictionary<string, object?>
        {
            ["high"] = findings.Count(f => (string?)f["severity"] == "high"),
            ["medium"] = findings.Count(f => (string?)f["severity"] == "medium"),
            ["low"] = findings.Count(f => (string?)f["severity"] == "low"),
            ["info"] = findings.Count(f => (string?)f["severity"] == "info")
        };

        var data = new Dictionary<string, object?>
        {
            ["ssh"] = ssh,
            ["firewall"] = new Dictionary<string, object?>
            {
                ["ufw"] = firewall.Ufw,
                ["firewalld"] = firewall.Firewalld,
                ["iptablesRules"] = firewall.IptablesRules
            },
            ["fail2ban"] = new Dictionary<string, object?>
            {
                ["installed"] = fail2ban.Installed,
                ["serviceRunning"] = fail2ban.ServiceRunning,
                ["jails"] = fail2ban.Jails
            },
            ["mysql"] = new Dictionary<string, object?>
            {
                ["installed"] = mysql.Installed,
                ["checked"] = mysql.Checked,
                ["reason"] = mysql.Reason,
                ["accounts"] = mysql.Accounts.Select(a => new Dictionary<string, object?> { ["user"] = a.User, ["host"] = a.Host }).ToList(),
                ["remoteGrants"] = remoteGrants.Select(g => new Dictionary<string, object?>
                {
                    ["user"] = g.User,
                    ["host"] = g.Host,
                    ["privileged"] = !IsLocalHost(g.Host) && g.Grants.Any(IsPrivilegedGrant),
                    ["grants"] = g.Grants
                }).ToList()
            },
            ["exposedHighRiskPorts"] = exposedPorts,
            ["emptyPasswordAccounts"] = emptyPw,
            ["nopasswdSudo"] = nopasswd,
            ["findings"] = findings,
            ["summary"] = summary
        };

        var degraded = !context.Elevate || (mysql.Installed && !mysql.Checked);
        var note = degraded
            ? "部分项未完整检查(未提权时 /etc/shadow 与 MySQL 账户可能不可读)"
            : null;

        sw.Stop();
        return CollectorResult.Ok(data, degraded, note) with { DurationMs = sw.Elapsed.TotalMilliseconds };
    }

    /// <summary>解析 sshd -T / sshd_config 的 "Key value" 行 → 小写键 → 值。</summary>
    public static Dictionary<string, string> ParseSshConfig(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var space = line.IndexOfAny(new[] { ' ', '\t' });
            if (space <= 0)
                continue;

            var key = line[..space].Trim().ToLowerInvariant();
            var value = line[(space + 1)..].Trim();
            // 只收已知键, 避免把包含 "Port"/"Password" 的杂项行误收
            if (KnownSshKeys.Contains(key) && !result.ContainsKey(key))
                result[key] = value;
        }
        return result;
    }

    private static readonly HashSet<string> KnownSshKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "permitrootlogin", "passwordauthentication", "permitemptypasswords", "pubkeyauthentication",
        "maxauthtries", "x11forwarding", "challengeresponseauthentication", "kbdinteractiveauthentication", "port"
    };

    /// <summary>解析 ufw/firewalld/iptables 合并输出。ufw: "Status: active/inactive"; firewalld: "running"/"not running"; iptables 行数=纯数字。</summary>
    public static FirewallInfo ParseFirewall(string output)
    {
        string? ufw = null, firewalld = null;
        var iptables = 0;
        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Status:", StringComparison.OrdinalIgnoreCase))
                ufw = line["Status:".Length..].Trim();
            else if (line is "running" or "not running")
                firewalld = line;
            else if (line.Length > 0 && line.All(char.IsDigit) && int.TryParse(line, out var n))
                iptables = n;
        }
        return new FirewallInfo(ufw, firewalld, iptables);
    }

    /// <summary>解析 fail2ban 输出：安装与否看可执行路径行；jails 看 "Jail list:"；运行看是否存在 "Status" 概览行。</summary>
    public static Fail2banInfo ParseFail2ban(string output)
    {
        var installed = false;
        var running = false;
        var jails = new List<string>();
        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('/') && line.EndsWith("fail2ban-client", StringComparison.Ordinal))
                installed = true;
            else if (line.IndexOf("Jail list:", StringComparison.OrdinalIgnoreCase) is var jailIdx && jailIdx >= 0)
                jails = line[(jailIdx + "Jail list:".Length)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            else if (line == "Status")
                running = true;
            else if (line.Contains("Number of jail", StringComparison.OrdinalIgnoreCase))
                running = true;
        }
        return new Fail2banInfo(installed, running || jails.Count > 0, jails);
    }

    /// <summary>
    /// 解析 AccountsCommand 合并输出：mysql 可执行路径 / mysql.user 行(user\thost) / "EMPTY_PW:用户名" / sudoers NOPASSWD 行。
    /// </summary>
    public static (List<string> EmptyPw, List<string> Nopasswd, MysqlInfo Mysql) ParseAccounts(string output)
    {
        var installed = false;
        var accounts = new List<(string, string)>();
        var emptyPw = new List<string>();
        var nopasswd = new List<string>();

        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            if (trimmed.StartsWith('/') && trimmed.EndsWith("/mysql", StringComparison.Ordinal))
            {
                installed = true;
                continue;
            }
            if (trimmed.StartsWith("EMPTY_PW:", StringComparison.Ordinal))
            {
                var user = trimmed["EMPTY_PW:".Length..].Trim();
                if (IsValidUserName(user))   // 排除 su PTY 回显碎片 "EMPTY_PW:\"$1}..."
                    emptyPw.Add(user);
                continue;
            }
            if (trimmed.Contains("NOPASSWD:", StringComparison.Ordinal))
            {
                // 必须带冒号(sudoers 规则形如 ... NOPASSWD: ALL); 排除回显命令里的 grep '^[^#]*NOPASSWD' 碎片
                nopasswd.Add(trimmed);
                continue;
            }
            if (line.Contains('\t'))
            {
                var parts = line.Split('\t');
                if (parts.Length >= 2)
                {
                    var user = parts[0].Trim('\'', ' ', '"');
                    var host = parts[1].Trim();
                    if (!accounts.Any(a => a.Item1 == user && a.Item2 == host))   // 多来源去重
                        accounts.Add((user, host));
                }
            }
        }

        var mysql = new MysqlInfo(
            installed,
            accounts.Count > 0,
            accounts,
            installed && accounts.Count == 0
                ? "mysql 存在但无免密访问(socket/auth_socket/~/.my.cnf/debian.cnf 均不可用), 无法读取 mysql.user(需 MySQL 凭据)"
                : null);
        return (emptyPw, nopasswd, mysql);
    }

    /// <summary>预定义高危端口中"公网暴露"的监听项（anyBind 或非回环具体地址）。</summary>
    public static List<Dictionary<string, object?>> FindExposedPorts(List<Dictionary<string, object?>> listeners)
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (var listener in listeners)
        {
            if (listener.GetValueOrDefault("port") is not int port || !HighRiskPorts.TryGetValue(port, out var risk))
                continue;

            var bind = listener.GetValueOrDefault("bind") as string ?? string.Empty;
            var anyBind = listener.GetValueOrDefault("anyBind") is true;
            if (!anyBind && !IsPublicBind(bind))
                continue;    // 仅回环 → 不告警

            result.Add(new Dictionary<string, object?>
            {
                ["port"] = port,
                ["proto"] = listener.GetValueOrDefault("proto"),
                ["bind"] = bind,
                ["process"] = listener.GetValueOrDefault("process"),
                ["service"] = risk.Name,
                ["risk"] = risk.Severity
            });
        }
        return result;
    }

    private static bool IsValidUserName(string user) =>
        user.Length > 0 && user.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '$');

    private static readonly string[] AdminGrantKeywords =
    {
        "ALL PRIVILEGES", "WITH GRANT OPTION", "SUPER", "CREATE USER", "FILE",
        "PROCESS", "RELOAD", "SHUTDOWN", "REPLICATION", "CREATE ROLE", "DROP ROLE"
    };

    /// <summary>授权行是否为"全局高权"：作用于 *.* 且含 ALL PRIVILEGES / GRANT OPTION / 管理类权限。</summary>
    public static bool IsPrivilegedGrant(string grant)
    {
        var upper = grant.ToUpperInvariant();
        return upper.Contains("ON *.*") && AdminGrantKeywords.Any(upper.Contains);
    }

    public static bool IsPublicBind(string bind) =>
        bind.Length > 0 &&
        bind is not "127.0.0.1" and not "::1" and not "localhost" &&
        !bind.StartsWith("127.", StringComparison.Ordinal);

    public static bool IsLocalHost(string host) =>
        host is "localhost" or "127.0.0.1" or "::1" or "ip6-localhost" ||
        host.StartsWith("127.", StringComparison.Ordinal) ||
        host.EndsWith("localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>汇总所有巡检项为 findings[]（severity/id/title/detail/evidence）。</summary>
    public static List<Dictionary<string, object?>> BuildFindings(
        Dictionary<string, string> ssh,
        FirewallInfo firewall,
        Fail2banInfo fail2ban,
        MysqlInfo mysql,
        List<Dictionary<string, object?>> exposedPorts,
        List<string> emptyPw,
        List<string> nopasswd,
        List<MysqlRemoteGrant>? remoteGrants = null)
    {
        var findings = new List<Dictionary<string, object?>>();
        void Add(string severity, string id, string title, string? detail, string? evidence = null) =>
            findings.Add(new Dictionary<string, object?>
            {
                ["severity"] = severity,
                ["id"] = id,
                ["title"] = title,
                ["detail"] = detail,
                ["evidence"] = evidence
            });

        // SSH
        if (ssh.Count == 0)
        {
            Add("info", "SSH_CONFIG_UNREAD", "未能读取 SSH 配置", "sshd -T 与 sshd_config 均不可用(可能无权限)");
        }
        else
        {
            var rootLogin = ssh.GetValueOrDefault("permitrootlogin");
            if (rootLogin == "yes")
                Add("high", "SSH_PERMIT_ROOT_LOGIN", "SSH 允许 root 直接登录", "建议改为 prohibit-password 或 no", "PermitRootLogin " + rootLogin);
            else if (rootLogin is "prohibit-password" or "without-password" or "forced-commands-only")
                Add("info", "SSH_ROOT_LOGIN_KEY_ONLY", "root 仅允许凭据登录", $"PermitRootLogin={rootLogin}");

            if (ssh.GetValueOrDefault("passwordauthentication") == "yes")
                Add("medium", "SSH_PASSWORD_AUTH", "SSH 启用密码登录", "易遭暴力破解; 建议仅用密钥登录", "PasswordAuthentication yes");
            if (ssh.GetValueOrDefault("permitemptypasswords") == "yes")
                Add("high", "SSH_EMPTY_PASSWORD", "SSH 允许空密码", "PermitEmptyPasswords yes");
            if (ssh.GetValueOrDefault("pubkeyauthentication") == "no")
                Add("medium", "SSH_NO_PUBKEY", "SSH 未启用公钥认证", "PubkeyAuthentication no");
            if (ssh.TryGetValue("port", out var sshPort) && sshPort != "22")
                Add("info", "SSH_CUSTOM_PORT", "SSH 使用非默认端口", "Port " + sshPort);
        }

        // 防火墙
        if (firewall.Ufw is not null && !firewall.Ufw.Equals("active", StringComparison.OrdinalIgnoreCase))
            Add("medium", "FW_UFW_INACTIVE", "ufw 防火墙未启用", "建议 ufw enable", "Status: " + firewall.Ufw);
        if (firewall.Firewalld is not null && !firewall.Firewalld.Equals("running", StringComparison.OrdinalIgnoreCase))
            Add("medium", "FW_FIREWALLD_INACTIVE", "firewalld 未运行", "建议 systemctl enable --now firewalld", "firewall-cmd --state: " + firewall.Firewalld);
        if (firewall.Ufw is null && firewall.Firewalld is null)
        {
            if (firewall.IptablesRules == 0)
                Add("high", "FW_NONE", "未检测到生效的防火墙规则", "无 ufw/firewalld, 且 iptables 规则为 0");
            else
                Add("low", "FW_IPTABLES_ONLY", "仅 iptables, 无 ufw/firewalld", null, $"iptables 规则 {firewall.IptablesRules} 条");
        }

        // fail2ban
        if (!fail2ban.Installed)
            Add("low", "F2B_ABSENT", "未安装 fail2ban", "建议安装以缓解 SSH/服务暴力破解");
        else if (!fail2ban.ServiceRunning)
            Add("low", "F2B_NOT_RUNNING", "fail2ban 已安装但未运行", "检查 systemctl status fail2ban");

        // MySQL
        if (mysql.Installed)
        {
            if (!mysql.Checked)
                Add("info", "MYSQL_UNCHECKED", "MySQL 账户未检查", mysql.Reason);
            foreach (var (user, host) in mysql.Accounts)
            {
                if (user.Length == 0)
                    Add("high", "MYSQL_ANONYMOUS_ACCOUNT", "MySQL 存在匿名账户", "建议删除匿名账户", $"user='' host={host}");
                else if (user == "root" && !IsLocalHost(host))
                    Add("high", "MYSQL_REMOTE_ROOT", "MySQL root 允许远程登录", "建议限制 root 仅本机或改授权主机", $"root@{host}");
            }

            foreach (var grant in remoteGrants ?? new List<MysqlRemoteGrant>())
            {
                if (IsLocalHost(grant.Host))
                    continue;
                var privileged = grant.Grants.FirstOrDefault(IsPrivilegedGrant);
                if (privileged is not null)
                    Add("high", "MYSQL_REMOTE_PRIVILEGED", "MySQL 存在可远程登录的高权账户",
                        $"账户 {grant.User}@{grant.Host} 可从远程登录且拥有全局高权限; 建议限制登录主机或降权", privileged);
            }
        }

        // 系统账户/提权
        foreach (var user in emptyPw)
            Add("high", "EMPTY_PASSWORD_ACCOUNT", "系统账户存在空口令", null, user);
        foreach (var line in nopasswd)
            Add("medium", "SUDO_NOPASSWD", "sudoers 含 NOPASSWD(免密提权)", null, line);

        // 公网暴露高危端口
        foreach (var exposed in exposedPorts)
        {
            var severity = (string?)exposed["risk"] == "high" ? "high" : "medium";
            Add(severity, $"PORT_{exposed["port"]}_EXPOSED", "高危端口公网暴露",
                $"{exposed["service"]} 监听 {exposed["bind"]}:{exposed["port"]}/{exposed["proto"]} (进程 {exposed["process"]})");
        }

        return findings;
    }
}
