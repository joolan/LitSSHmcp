using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Snapshot;
using LitSSHmcp.Core.Services.Snapshot.Collectors;
using LitSSHmcp.Core.Services.SSH;
using Xunit;

namespace LitSSHmcp.Tests;

/// <summary>快照采集器的纯解析逻辑（不依赖真实 SSH/服务器）。</summary>
public class SnapshotCollectorTests
{
    // ---------- resource ----------

    [Fact]
    public void Resource_parses_combined_probe_output()
    {
        var output = string.Join("\n", new[]
        {
            "##loadavg", "0.52 0.58 0.59 1/423 12345",
            "##mem",
            "              total        used        free      shared  buff/cache   available",
            "Mem:           16000        4000        2000         100       10000       11000",
            "Swap:           2048           0        2048",
            "##disk",
            "Filesystem      Size  Used Avail Use% Mounted on",
            "/dev/vda1        40G   12G   27G  32% /",
            "/dev/vdb1       100G   20G   80G  20% /data",
            "##uptime", "123456.78 500000.00",
            "##cpu", "model name\t: Intel(R) Xeon(R) CPU E5-2680",
            "##cores", "8",
            "##os", "NAME=\"Ubuntu\"", "VERSION_ID=\"22.04\"", "PRETTY_NAME=\"Ubuntu 22.04.3 LTS\"",
            "##kernel", "5.15.0-91-generic",
            "##arch", "x86_64",
            "##host", "web01",
            "##ip", "10.0.0.5 172.17.0.1"
        });

        var data = ResourceSnapshotCollector.Parse(output);

        Assert.NotNull(data);
        var load = Assert.IsType<Dictionary<string, object?>>(data!["load"]);
        Assert.Equal(0.52, load["1m"]);
        var mem = Assert.IsType<Dictionary<string, object?>>(data["memory"]);
        Assert.Equal(16000L, mem["totalMb"]);
        Assert.Equal(11000L, mem["availableMb"]);
        Assert.Equal(31.2, mem["usedPercent"]);
        var disks = Assert.IsType<List<Dictionary<string, object?>>>(data["disks"]);
        Assert.Equal(2, disks.Count);
        Assert.Equal(40.0, disks[0]["sizeGb"]);
        Assert.Equal(32, disks[0]["usedPercent"]);
        Assert.Equal("/", disks[0]["mount"]);
        Assert.Equal(123457.0, data["uptimeSeconds"]);
        var cpu = Assert.IsType<Dictionary<string, object?>>(data["cpu"]);
        Assert.Equal("Intel(R) Xeon(R) CPU E5-2680", cpu["model"]);
        Assert.Equal(8, cpu["cores"]);
        var os = Assert.IsType<Dictionary<string, object?>>(data["os"]);
        Assert.Equal("Ubuntu 22.04.3 LTS", os["prettyName"]);
        Assert.Equal("5.15.0-91-generic", data["kernel"]);
        Assert.Equal("web01", data["hostname"]);
        Assert.Equal(2, Assert.IsType<string[]>(data["ips"]).Length);
    }

    [Theory]
    [InlineData("40G", 40.0)]
    [InlineData("512M", 0.5)]
    [InlineData("1.5T", 1536.0)]
    [InlineData("981K", 0.0)]
    public void Resource_converts_sizes_to_gb(string input, double expected)
    {
        Assert.Equal(expected, ResourceSnapshotCollector.ToGb(input), 1);
    }

    [Fact]
    public void Resource_missing_loadavg_returns_null()
    {
        Assert.Null(ResourceSnapshotCollector.Parse("##mem\ntotal 1"));
    }

    [Fact]
    public void SplitSections_ignores_marker_like_echo_fragments()
    {
        // 模拟 su 交互式 PTY 回显换行后产生的"以 ## 开头"的命令碎片: 不应被当成分段标记,
        // 否则真正的 ps 输出会被吸进伪分段, 导致 user 全空。
        var output = string.Join("\n", new[]
        {
            "ps -o pid=,user=,comm= -p 1,2",       // 回显的命令首行(前导)
            "##cg'; grep -H . /proc/1/cgroup",      // 回显换行碎片(伪标记)
            "6375 root rpcbind",                     // 真正的 ps 输出
            "##cg",                                  // 真正的标记
            "/proc/6375/cgroup:0::/system.slice/rpcbind.service"
        });

        var sections = ResourceSnapshotCollector.SplitSections(output);

        Assert.Contains("6375 root rpcbind", sections[string.Empty]);
        Assert.Contains("rpcbind.service", sections["cg"]);
        Assert.False(sections.ContainsKey("cg'; grep -H . /proc/1/cgroup"));
    }

    [Fact]
    public void ParseCgroup_tolerates_trailing_cr()
    {
        var cg = PortMapSnapshotCollector.ParseCgroup(
            "/proc/6375/cgroup:0::/system.slice/rpcbind.service\r\n/proc/1/cgroup:0::/init.scope\r\n");

        Assert.Equal("rpcbind.service", cg[6375].Unit);
        Assert.Equal("/system.slice/rpcbind.service", cg[6375].Path);
        Assert.Null(cg[1].Unit);
    }

    // ---------- portmap ----------

    [Fact]
    public void Portmap_parses_tcp_listeners_and_filters_non_listen_states()
    {
        var output = string.Join("\n", new[]
        {
            "LISTEN 0 511 0.0.0.0:80 0.0.0.0:* users:((\"nginx\",pid=123,fd=6))",
            "LISTEN 0 511 [::]:443 [::]:* users:((\"nginx\",pid=123,fd=7))",
            "LISTEN 0 511 *:8080 *:* users:((\"java\",pid=500,fd=8))",
            "ESTAB 0 0 10.0.0.5:22 10.0.0.9:50000"
        });

        var rows = PortMapSnapshotCollector.ParseSsListeners(output, "tcp");

        Assert.Equal(3, rows.Count);
        Assert.Equal(80, rows[0]["port"]);
        Assert.Equal("ipv4", rows[0]["family"]);
        Assert.True((bool)rows[0]["anyBind"]!);
        Assert.Equal(123L, rows[0]["pid"]);
        Assert.Equal("nginx", rows[0]["process"]);
        Assert.Equal("ipv6", rows[1]["family"]);
        Assert.Equal("::", rows[1]["bind"]);
        Assert.Equal("any", rows[2]["family"]);
    }

    [Fact]
    public void Portmap_parses_udp_and_unix_sockets()
    {
        var udp = PortMapSnapshotCollector.ParseSsListeners(
            "UNCONN 0 0 0.0.0.0:68 0.0.0.0:* users:((\"dhclient\",pid=500,fd=6))", "udp");
        Assert.Single(udp);
        Assert.Equal("udp", udp[0]["proto"]);

        var unix = PortMapSnapshotCollector.ParseSsUnix(string.Join("\n", new[]
        {
            "u_str LISTEN 0 128 /run/systemd/private 5520 users:((\"systemd\",pid=1,fd=20))",
            "u_dgr UNCONN 0 0 /run/systemd/notify 5845"
        }));
        Assert.Equal(2, unix.Count);
        Assert.Equal("stream", unix[0]["type"]);
        Assert.Equal("/run/systemd/private", unix[0]["path"]);
        Assert.Equal(1L, unix[0]["pid"]);
        Assert.Equal("datagram", unix[1]["type"]);
    }

    [Fact]
    public void Portmap_merged_output_infers_proto_and_skips_echo_and_unix_lines()
    {
        // 无 "##标记" 方案: 同一份 ss 输出里, 由状态推断 tcp/udp, 并跳过 PTY 回显命令行与 Unix 行。
        var output = string.Join("\n", new[]
        {
            "ss -H -tlnp 2>/dev/null; ss -H -lunp 2>/dev/null; ss -H -xlp 2>/dev/null | head -120", // PTY 回显
            "LISTEN 0 511 0.0.0.0:80 0.0.0.0:* users:((\"nginx\",pid=123,fd=6))",
            "UNCONN 0 0 0.0.0.0:68 0.0.0.0:* users:((\"dhclient\",pid=500,fd=6))",
            "u_str LISTEN 0 128 /run/systemd/private 5520 users:((\"systemd\",pid=1,fd=20))"
        });

        var rows = PortMapSnapshotCollector.ParseSsListeners(output, proto: null);

        Assert.Equal(2, rows.Count);
        Assert.Equal("tcp", rows[0]["proto"]);
        Assert.Equal(80, rows[0]["port"]);
        Assert.Equal("udp", rows[1]["proto"]);
        Assert.Equal(68, rows[1]["port"]);
    }

    [Fact]
    public void Portmap_phase2_parses_merged_ps_and_cgroup_without_markers()
    {
        var merged = string.Join("\n", new[]
        {
            "ps -o pid=,user=,comm= -p 123,1 2>/dev/null; grep -HE '0::|name=systemd' /proc/123/cgroup /proc/1/cgroup 2>/dev/null | head -400", // 回显
            "123 root nginx",
            "1 root systemd",
            "/proc/123/cgroup:0::/system.slice/nginx.service",
            "/proc/1/cgroup:1:name=systemd:/init.scope"
        });

        var users = PortMapSnapshotCollector.ParsePs(merged);
        var cgroups = PortMapSnapshotCollector.ParseCgroup(merged);

        Assert.Equal("root", users[123].User);
        Assert.Equal("nginx", users[123].Comm);
        Assert.Equal("nginx.service", cgroups[123].Unit);
        Assert.Equal("/system.slice/nginx.service", cgroups[123].Path);
        Assert.Null(cgroups[1].Unit);            // init.scope 不是 .service
    }

    [Fact]
    public void Portmap_parses_ps_and_cgroup_to_service_unit()
    {
        var ps = PortMapSnapshotCollector.ParsePs("1 root systemd\n500 app myapp");
        Assert.Equal("root", ps[1].User);
        Assert.Equal("myapp", ps[500].Comm);

        var cg = PortMapSnapshotCollector.ParseCgroup(string.Join("\n", new[]
        {
            "/proc/123/cgroup:0::/system.slice/nginx.service",
            "/proc/1/cgroup:0::/init.scope",
            "/proc/500/cgroup:1:name=systemd:/system.slice/myapp.service"
        }));
        Assert.Equal("nginx.service", cg[123].Unit);
        Assert.Equal("/system.slice/nginx.service", cg[123].Path);
        Assert.Null(cg[1].Unit);            // init.scope 不是 .service
        Assert.Equal("myapp.service", cg[500].Unit);  // cgroup v1 格式
    }

    [Fact]
    public void Portmap_parses_exe_paths()
    {
        var merged = string.Join("\n", new[]
        {
            "for p in 1 2; do printf 'EXE %s %s\\n' \"$p\" \"$(readlink /proc/$p/exe)\"; done", // 回显
            "EXE 6375 /usr/sbin/rpcbind",
            "EXE 7142 /usr/sbin/nginx (deleted)"
        });

        var exe = PortMapSnapshotCollector.ParseExePaths(merged);

        Assert.Equal("/usr/sbin/rpcbind", exe[6375]);
        Assert.Equal("/usr/sbin/nginx (deleted)", exe[7142]);
    }

    [Fact]
    public void Portmap_parses_cmdlines_and_masks_secrets()
    {
        var merged = string.Join("\n", new[]
        {
            "for p in 1; do printf 'CMD %s %s\\n' ...; done", // 回显
            "CMD 6375 /usr/sbin/rpcbind -w",
            "CMD 1526 /usr/bin/java -jar jenkins.war --httpPort=8080"
        });

        var cmds = PortMapSnapshotCollector.ParseCmdlines(merged);

        Assert.Equal("/usr/sbin/rpcbind -w", cmds[6375]);
        Assert.Contains("--httpPort=8080", cmds[1526]);

        Assert.Equal("java -jar app.jar --spring.datasource.password=****** -Xmx1g",
            PortMapSnapshotCollector.MaskSecretArgs("java -jar app.jar --spring.datasource.password=Secret123 -Xmx1g"));
        Assert.Equal("/opt/app --token ****** --port 8080",
            PortMapSnapshotCollector.MaskSecretArgs("/opt/app --token abcdef --port 8080"));
    }

    [Theory]
    [InlineData("0.0.0.0:80", "0.0.0.0", 80)]
    [InlineData("[::]:443", "::", 443)]
    [InlineData("*:8080", "*", 8080)]
    public void Portmap_splits_bind_and_port(string local, string bind, int port)
    {
        Assert.True(PortMapSnapshotCollector.TrySplitAddress(local, out var actualBind, out var actualPort));
        Assert.Equal(bind, actualBind);
        Assert.Equal(port, actualPort);
    }

    // ---------- nginx_tls ----------

    [Fact]
    public void Nginx_parses_server_blocks_with_tls()
    {
        var config = string.Join("\n", new[]
        {
            "# configuration file /etc/nginx/nginx.conf:",
            "server {",
            "    listen 443 ssl;",
            "    listen [::]:443 ssl;",
            "    server_name a.com www.a.com;",
            "    ssl_certificate /etc/nginx/cert/a.pem;",
            "    ssl_certificate_key /etc/nginx/cert/a.key;",
            "    location / { proxy_pass http://127.0.0.1:8080; }",
            "}",
            "server {",
            "    listen 80;",
            "    server_name b.com;",
            "}"
        });

        var (sites, truncated) = NginxTlsSnapshotCollector.ParseNginxConfig(config);

        Assert.False(truncated);
        Assert.Equal(2, sites.Count);
        Assert.Equal(new[] { "a.com", "www.a.com" }, sites[0].ServerNames);
        Assert.Equal(2, sites[0].Listens.Count);
        Assert.True(sites[0].Listens[0].Ssl);
        Assert.Equal(443, sites[0].Listens[0].Port);
        Assert.Equal("::", sites[0].Listens[1].Bind);
        Assert.Equal(new[] { "/etc/nginx/cert/a.pem" }, sites[0].CertPaths);
        Assert.False(sites[1].HasTls);
    }

    [Fact]
    public void Nginx_parses_certificate_expiry_and_san()
    {
        var output = "##CERT:/etc/nginx/cert/a.pem\n" +
                     "notAfter=Jan 15 12:00:00 2027 GMT\n" +
                     "subject=CN = a.com\n" +
                     "X509v3 Subject Alternative Name:\n" +
                     "    DNS:a.com, DNS:www.a.com, IP Address:10.0.0.5\n" +
                     "##CERT:/etc/nginx/cert/b.pem\n" +
                     "openssl: command not found";

        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var certs = NginxTlsSnapshotCollector.ParseCertificateBlocks(output, now, out var errors);

        Assert.Equal(2, certs.Count);
        Assert.Equal(1, errors);
        Assert.Equal("a.com", certs[0]["subjectCn"]);
        Assert.True((long)certs[0]["daysRemaining"]! > 300);
        Assert.False((bool)certs[0]["expired"]!);
        Assert.Contains("www.a.com", (List<string>)certs[0]["dnsNames"]!);
        Assert.Contains("10.0.0.5", (List<string>)certs[0]["ipNames"]!);
        Assert.Null(certs[0]["error"]);
        Assert.NotNull(certs[1]["error"]);
    }

    [Fact]
    public void Nginx_parses_server_block_with_brace_on_next_line()
    {
        // 宝塔等配置把 '{' 写在 server 的下一行
        var config = string.Join("\n", new[]
        {
            "http {",
            "    server",
            "    {",
            "        listen 888;",
            "        server_name phpmyadmin;",
            "        root /www/server/phpmyadmin;",
            "    }",
            "    server {",
            "        listen 443 ssl;",
            "        server_name a.com;",
            "        ssl_certificate /c/a.pem;",
            "    }",
            "}"
        });

        var (sites, _) = NginxTlsSnapshotCollector.ParseNginxConfig(config);

        Assert.Equal(2, sites.Count);
        Assert.Contains(sites, s => s.ServerNames.Contains("phpmyadmin") && s.Listens.Any(l => l.Port == 888));
        Assert.Contains(sites, s => s.ServerNames.Contains("a.com") && s.HasTls);
    }

    [Fact]
    public void Nginx_builds_domain_list_with_ssl_certificates()
    {
        var config = string.Join("\n", new[]
        {
            "server {",
            "  listen 443 ssl;",
            "  server_name a.com www.a.com;",
            "  ssl_certificate /c/a.pem;",
            "}",
            "server {",
            "  listen 80;",
            "  server_name b.com;",
            "}",
            "server {",
            "  listen 443 ssl;",
            "  server_name b.com;",
            "  ssl_certificate /c/a.pem;",
            "}"
        });

        var (sites, _) = NginxTlsSnapshotCollector.ParseNginxConfig(config);
        var certs = new List<Dictionary<string, object?>>
        {
            new() { ["path"] = "/c/a.pem", ["subjectCn"] = "a.com", ["daysRemaining"] = 100L, ["expired"] = false, ["dnsNames"] = new List<string> { "a.com", "www.a.com" } }
        };

        var domains = NginxTlsSnapshotCollector.BuildDomains(sites, certs);

        var a = domains.Single(d => (string)d["domain"]! == "a.com");
        Assert.True((bool)a["ssl"]!);
        Assert.NotEmpty((List<Dictionary<string, object?>>)a["certificates"]!);

        var b = domains.Single(d => (string)d["domain"]! == "b.com");
        Assert.True((bool)b["ssl"]!);                         // 与 443 块合并后为 ssl
        Assert.Contains(80, (int[])b["ports"]!);
        Assert.Contains(443, (int[])b["ports"]!);
    }

    [Fact]
    public async Task Nginx_collector_ignores_echoed_missing_sentinel_from_pty()
    {
        // su 交互式 PTY 会回显整条命令(含 '##nginx_missing' 字面量); 不能因此误判为"未安装"。
        var echoed = "NGX=''; for p in $(pgrep -x nginx); do :; done; echo '##nginx_missing'; \"$NGX\" -T";
        var config = echoed + "\n# configuration file /etc/nginx/nginx.conf:\n" +
                     "http {\n server {\n   listen 80;\n   server_name example.com;\n }\n}";

        var context = new SnapshotContext
        {
            Server = new SshServerConfig { Id = "s1", Name = "n", Host = "h" },
            Elevate = false,
            ExecuteAsync = (_, _, _) => Task.FromResult(new CommandResult { Success = true, Output = config })
        };

        var result = await new NginxTlsSnapshotCollector().CollectAsync(context, default);

        Assert.True(result.Success);
        Assert.False(result.Skipped);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Data);
        Assert.Equal(1, data["serverCount"]);
        var domains = Assert.IsType<List<Dictionary<string, object?>>>(data["domains"]);
        Assert.Contains(domains, d => (string?)d["domain"] == "example.com");
    }

    // ---------- docker ----------

    [Fact]
    public void Docker_parses_info_containers_and_stats()
    {
        var info = "docker info --format '{{json .}}'\n" +
            "{\"ServerVersion\":\"27.1.1\",\"OperatingSystem\":\"Debian GNU/Linux 13\",\"Driver\":\"overlay2\"," +
            "\"Name\":\"vm\",\"Containers\":5,\"ContainersRunning\":3,\"ContainersPaused\":0,\"ContainersStopped\":2," +
            "\"Images\":12,\"NCPU\":2,\"MemTotal\":4100000000}";
        var parsedInfo = DockerSnapshotCollector.ParseDockerInfo(info);
        Assert.NotNull(parsedInfo);
        Assert.Equal("27.1.1", parsedInfo!["serverVersion"]);
        Assert.Equal(5L, parsedInfo["containers"]);
        Assert.Equal(3L, parsedInfo["containersRunning"]);
        Assert.Equal(2L, parsedInfo["cpus"]);

        var merged = string.Join("\n", new[]
        {
            "docker ps -a --format '{{json .}}'; docker stats --no-stream --format '{{json .}}'", // 回显
            "{\"ID\":\"abc\",\"Names\":\"web\",\"Image\":\"nginx:1.27\",\"State\":\"running\",\"Status\":\"Up 2 hours\",\"Ports\":\"0.0.0.0:80->80/tcp\",\"CreatedAt\":\"2026-10-01 10:00:00 +0000 UTC\",\"RunningFor\":\"2 hours ago\"}",
            "{\"ID\":\"def\",\"Names\":\"db\",\"Image\":\"mysql:8\",\"State\":\"exited\",\"Status\":\"Exited (0) 1 hour ago\",\"Ports\":\"\",\"CreatedAt\":\"x\",\"RunningFor\":\"3 hours ago\"}",
            "{\"Name\":\"web\",\"ID\":\"abc\",\"CPUPerc\":\"1.20%\",\"MemUsage\":\"10MiB / 1GiB\",\"MemPerc\":\"1.00%\",\"NetIO\":\"1kB / 2kB\",\"BlockIO\":\"0B / 0B\",\"PIDs\":\"5\"}",
            "{{json .}}' ; docker ps"   // PTY 换行回显碎片(以 { 开头但非合法 JSON)
        });

        var (containers, stats) = DockerSnapshotCollector.ParsePsAndStats(merged);

        Assert.Equal(2, containers.Count);
        Assert.Equal("web", containers[0]["name"]);
        Assert.Equal("running", containers[0]["state"]);
        Assert.Equal("0.0.0.0:80->80/tcp", containers[0]["ports"]);
        Assert.Single(stats);
        Assert.Equal("1.20%", stats[0]["cpuPercent"]);
    }

    [Fact]
    public async Task Docker_collector_skips_when_not_installed()
    {
        var context = new SnapshotContext
        {
            Server = new SshServerConfig { Id = "s", Name = "n", Host = "h" },
            Elevate = false,
            ExecuteAsync = (_, _, _) => Task.FromResult(new CommandResult { Success = true, Output = "" })
        };

        var result = await new DockerSnapshotCollector().CollectAsync(context, default);

        Assert.True(result.Success);
        Assert.True(result.Skipped);
    }

    [Fact]
    public async Task Docker_collector_reports_daemon_down_gracefully()
    {
        var context = new SnapshotContext
        {
            Server = new SshServerConfig { Id = "s", Name = "n", Host = "h" },
            Elevate = false,
            ExecuteAsync = (cmd, _, _) => Task.FromResult(new CommandResult
            {
                Success = true,
                Output = cmd.StartsWith("command -v", StringComparison.Ordinal) ? "/usr/bin/docker\n" : ""
            })
        };

        var result = await new DockerSnapshotCollector().CollectAsync(context, default);

        Assert.True(result.Success);
        Assert.True(result.Degraded);
        var data = Assert.IsType<Dictionary<string, object?>>(result.Data);
        Assert.Equal(false, data["daemonRunning"]);
    }

    // ---------- security ----------

    [Fact]
    public void Security_parses_ssh_firewall_fail2ban()
    {
        var ssh = SecurityAuditSnapshotCollector.ParseSshConfig(
            "permitrootlogin yes\npasswordauthentication no\nport 2222\npermitemptypasswords no\npubkeyauthentication yes\nmaxauthtries 6");
        Assert.Equal("yes", ssh["permitrootlogin"]);
        Assert.Equal("2222", ssh["port"]);

        var fwOut = string.Join("\n", new[]
        {
            "Status: inactive",
            "running",
            "42",
            "/usr/bin/fail2ban-client",
            "Status",
            "|- Number of jail: 1",
            "`- Jail list: sshd"
        });
        var fw = SecurityAuditSnapshotCollector.ParseFirewall(fwOut);
        Assert.Equal("inactive", fw.Ufw);
        Assert.Equal("running", fw.Firewalld);
        Assert.Equal(42, fw.IptablesRules);

        var f2b = SecurityAuditSnapshotCollector.ParseFail2ban(fwOut);
        Assert.True(f2b.Installed);
        Assert.True(f2b.ServiceRunning);
        Assert.Contains("sshd", f2b.Jails);
    }

    [Fact]
    public void Security_parses_accounts_and_local_checks()
    {
        var output = string.Join("\n", new[]
        {
            "/usr/bin/mysql",
            "root\tlocalhost",
            "root\t%",
            "\tlocalhost",              // 匿名账户
            "EMPTY_PW:tester",
            "/etc/sudoers:deploy ALL=(ALL) NOPASSWD: ALL"
        });

        var (emptyPw, nopasswd, mysql) = SecurityAuditSnapshotCollector.ParseAccounts(output);

        Assert.True(mysql.Installed);
        Assert.True(mysql.Checked);
        Assert.Contains(mysql.Accounts, a => a.Item1 == "" && a.Item2 == "localhost");
        Assert.Contains(mysql.Accounts, a => a.Item1 == "root" && a.Item2 == "%");
        Assert.Contains("tester", emptyPw);
        Assert.Single(nopasswd);
    }

    [Fact]
    public void Security_ignores_pty_echoed_command_fragments()
    {
        var output = string.Join("\n", new[]
        {
            "su - root -c 'command -v mysql; awk ... {print \"EMPTY_PW:\"$1} /etc/shadow; grep -rhE '^[^#]*NOPASSWD' /etc/sudoers'",
            "EMPTY_PW:\"$1}'\\'' /etc/shadow 2>/dev/null",   // 回显换行碎片(伪 EMPTY_PW)
            "grep -rhE '^[^#]*NOPASSWD' /etc/sudoers",        // 回显(伪 NOPASSWD, 无冒号)
            "/usr/bin/mysql",
            "root\tlocalhost"
        });

        var (emptyPw, nopasswd, mysql) = SecurityAuditSnapshotCollector.ParseAccounts(output);

        Assert.Empty(emptyPw);
        Assert.Empty(nopasswd);
        Assert.True(mysql.Installed);
        Assert.Contains(mysql.Accounts, a => a.Item1 == "root" && a.Item2 == "localhost");
    }

    [Fact]
    public void Security_finds_public_high_risk_ports_only()
    {
        var ss = string.Join("\n", new[]
        {
            "LISTEN 0 128 *:3306 *:* users:((\"mysqld\",pid=1,fd=1))",                 // 公网高危及
            "LISTEN 0 128 127.0.0.1:5432 0.0.0.0:* users:((\"postgres\",pid=2,fd=1))", // 回环 → 不告警
            "LISTEN 0 128 *:8080 *:* users:((\"java\",pid=3,fd=1))"                    // 非高危端口
        });
        var listeners = PortMapSnapshotCollector.ParseSsListeners(ss, proto: null);

        var exposed = SecurityAuditSnapshotCollector.FindExposedPorts(listeners);

        Assert.Single(exposed);
        Assert.Equal(3306, exposed[0]["port"]);
        Assert.Equal("MySQL", exposed[0]["service"]);
    }

    [Fact]
    public void Security_builds_findings_for_common_risks()
    {
        var ssh = new Dictionary<string, string> { ["permitrootlogin"] = "yes", ["passwordauthentication"] = "yes" };
        var fw = new SecurityAuditSnapshotCollector.FirewallInfo("inactive", null, 3);
        var f2b = new SecurityAuditSnapshotCollector.Fail2banInfo(false, false, new List<string>());
        var mysql = new SecurityAuditSnapshotCollector.MysqlInfo(true, true,
            new List<(string, string)> { ("", "localhost"), ("root", "%") }, null);
        var exposed = new List<Dictionary<string, object?>>
        {
            new() { ["port"] = 3306, ["proto"] = "tcp", ["bind"] = "*", ["process"] = "mysqld", ["service"] = "MySQL", ["risk"] = "high" }
        };

        var findings = SecurityAuditSnapshotCollector.BuildFindings(ssh, fw, f2b, mysql, exposed, new List<string>(), new List<string>());
        var ids = findings.Select(f => (string?)f["id"]).ToList();

        Assert.Contains("SSH_PERMIT_ROOT_LOGIN", ids);
        Assert.Contains("SSH_PASSWORD_AUTH", ids);
        Assert.Contains("FW_UFW_INACTIVE", ids);
        Assert.Contains("F2B_ABSENT", ids);
        Assert.Contains("MYSQL_ANONYMOUS_ACCOUNT", ids);
        Assert.Contains("MYSQL_REMOTE_ROOT", ids);
        Assert.Contains("PORT_3306_EXPOSED", ids);
        Assert.True(findings.Count(f => (string?)f["severity"] == "high") >= 4);
    }

    [Theory]
    [InlineData("GRANT ALL PRIVILEGES ON *.* TO 'root2'@'%' WITH GRANT OPTION", true)]
    [InlineData("GRANT SELECT, INSERT ON *.* TO 'app'@'%'", false)]
    [InlineData("GRANT ALL PRIVILEGES ON `app`.* TO 'app'@'%'", false)]
    [InlineData("GRANT FILE, PROCESS ON *.* TO 'svc'@'10.0.0.%'", true)]
    public void Security_detects_privileged_grants(string grant, bool expected)
    {
        Assert.Equal(expected, SecurityAuditSnapshotCollector.IsPrivilegedGrant(grant));
    }

    [Fact]
    public void Security_flags_remote_privileged_mysql_account()
    {
        var ssh = new Dictionary<string, string>();
        var fw = new SecurityAuditSnapshotCollector.FirewallInfo(null, "running", 5);
        var f2b = new SecurityAuditSnapshotCollector.Fail2banInfo(true, true, new List<string> { "sshd" });
        var mysql = new SecurityAuditSnapshotCollector.MysqlInfo(true, true,
            new List<(string, string)> { ("root2", "%"), ("root", "localhost") }, null);
        var grants = new List<MysqlRemoteGrant>
        {
            new("root2", "%", new List<string> { "GRANT ALL PRIVILEGES ON *.* TO 'root2'@'%' WITH GRANT OPTION" }),
            new("root", "localhost", new List<string> { "GRANT ALL PRIVILEGES ON *.* TO 'root'@'localhost'" })
        };

        var findings = SecurityAuditSnapshotCollector.BuildFindings(ssh, fw, f2b, mysql, new(), new(), new(), grants);

        Assert.Contains(findings, f => (string?)f["id"] == "MYSQL_REMOTE_PRIVILEGED");
        // root@localhost 是本机, 不应告警
        Assert.DoesNotContain(findings, f => (string?)f["id"] == "MYSQL_REMOTE_PRIVILEGED" && ((string?)f["evidence"])!.Contains("localhost"));
    }

    // ---------- systemd ----------

    [Fact]
    public void Systemd_parses_units_and_failed_flag()
    {
        var output = string.Join("\n", new[]
        {
            "nginx.service loaded active running A high performance web server",
            "● foo.service loaded failed failed Foo service",
            "bar.service loaded inactive dead Bar service",
            "not-a-service.target loaded active active Some target"
        });

        var units = SystemdHealthSnapshotCollector.ParseUnits(output);

        Assert.Equal(3, units.Count);
        Assert.Equal("nginx.service", units[0].Name);
        Assert.Equal("active", units[0].Active);
        Assert.Equal("failed", units[1].Active);
        Assert.Equal("Foo service", units[1].Description);
    }

    // ---------- resource 阈值/告警 ----------

    [Fact]
    public void Resource_builds_threshold_warnings()
    {
        var data = new Dictionary<string, object?>
        {
            ["memory"] = new Dictionary<string, object?> { ["usedPercent"] = 74.7, ["swapTotalMb"] = 2048L, ["swapUsedPercent"] = 53.0 },
            ["disks"] = new List<Dictionary<string, object?>> { new() { ["mount"] = "/", ["usedPercent"] = 91 } },
            ["cpu"] = new Dictionary<string, object?> { ["cores"] = 2 },
            ["load"] = new Dictionary<string, object?> { ["1m"] = 5.0 }
        };

        var warnings = ResourceSnapshotCollector.BuildWarnings(data);

        Assert.Contains(warnings, w => (string?)w["metric"] == "swap" && (string?)w["severity"] == "warning");
        Assert.Contains(warnings, w => (string?)w["metric"] == "disk" && (string?)w["severity"] == "critical");
        Assert.Contains(warnings, w => (string?)w["metric"] == "load");                       // 5/2=2.5 倍 → warning
        Assert.DoesNotContain(warnings, w => (string?)w["metric"] == "memory");               // 74.7 < 80
    }

    [Fact]
    public void Resource_parses_top_processes()
    {
        var psOut = "  1234 root     12.5  3.2  102400 /usr/bin/java -jar app.jar\n" +
                    "9999 appuser   8.0 20.0  512000 /opt/app/bin/node server.js\n" +
                    "some echoed header line";

        var top = ResourceSnapshotCollector.ParseTopProcesses(psOut);

        Assert.Equal(2, top.Count);
        Assert.Equal(1234L, top[0]["pid"]);
        Assert.Equal(12.5, top[0]["cpuPercent"]);
        Assert.Equal(100.0, top[0]["rssMb"]);      // 102400 KB → 100 MB
        Assert.Equal("appuser", top[1]["user"]);
        Assert.Contains("server.js", (string)top[1]["command"]!);
    }

    // ---------- nginx PTY 噪声清洗 ----------

    [Fact]
    public void Nginx_sanitizes_pty_login_noise()
    {
        var raw = "\x1b]0;joolan@192\x07\x1b[?1034hLast login: Sat Oct  4 10:00:00 2026 from 10.0.0.1\r\n" +
                  "[joolan@192 ~]$ su - root -c 'NGX=...; \"$NGX\" -T 2>&1 | head -2000'\r\n" +
                  "Password: \r\n" +
                  "# configuration file /etc/nginx/nginx.conf:\r\n" +
                  "user nginx;\r\nhttp {\r\n    server {\r\n        listen 80;\r\n        server_name a.com;\r\n    }\r\n}\r\n" +
                  "LITSSH_EXIT:0\r\n";

        var clean = NginxTlsSnapshotCollector.SanitizeNginxConfig(raw);

        Assert.StartsWith("# configuration file", clean);
        Assert.DoesNotContain("Last login", clean);
        Assert.DoesNotContain("su - root", clean);
        Assert.DoesNotContain("Password:", clean);
        Assert.False(clean.Contains('\u001b'), "clean 仍含 ESC: " + clean);
        var (sites, _) = NginxTlsSnapshotCollector.ParseNginxConfig(clean);
        Assert.Contains(sites, s => s.ServerNames.Contains("a.com"));
    }

    // ---------- portmap 桌面 socket 折叠 ----------

    [Theory]
    [InlineData("/run/user/1000/bus", "dbus-daemon", true)]
    [InlineData("@/tmp/dbus-abc", "gnome-shell", true)]
    [InlineData("/tmp/.X11-unix/X0", "X", true)]
    [InlineData("/home/u/.cache/ibus/dbus-x", "ibus-daemon", true)]
    [InlineData("/run/systemd/private", "systemd", false)]
    [InlineData("/var/run/docker.sock", "dockerd", false)]
    [InlineData("/var/run/mysqld/mysqlx.sock", "mysqld", false)]
    public void Portmap_classifies_desktop_vs_system_sockets(string path, string process, bool desktop)
    {
        var row = new Dictionary<string, object?> { ["path"] = path, ["process"] = process };
        Assert.Equal(desktop ? "desktop" : "system", PortMapSnapshotCollector.ClassifyUnixSocket(row));
    }
}
