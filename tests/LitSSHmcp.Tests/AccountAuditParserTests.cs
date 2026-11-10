using LitSSHmcp.Core.Services.Snapshot.Collectors;
using Xunit;

namespace LitSSHmcp.Tests;

public class AccountAuditParserTests
{
    [Fact]
    public void Parses_accounts_keys_logins_and_risks()
    {
        var output = string.Join("\n", new[]
        {
            "##PASSWD",
            "root:x:0:0:root:/root:/bin/bash",
            "alice:x:1000:1000:Alice:/home/alice:/bin/bash",
            "sys:x:999:999:sys:/home/sys:/usr/sbin/nologin",
            "evil:x:0:0:evil:/home/evil:/bin/bash",
            "##GROUP",
            "root:x:0:",
            "wheel:x:10:alice",
            "alice:x:1000:",
            "##SHADOW",
            "root:$6$salt$hash:19000:0:99999:7:::",
            "alice::19000:0:99999:7:::",
            "sys:!:19000:0:99999:7:::",
            "evil:$1$salt$hash:19000:0:99999:7:::",
            "##LASTLOG",
            "Username         Port     From             Latest",
            "alice            pts/0    10.0.0.5         Mon Nov 10 10:00:00 +0800 2026",
            "##LAST",
            "alice    pts/0    10.0.0.5    Mon Nov 10 10:00   still logged in",
            "##WHO",
            "alice    pts/0        2026-11-10 10:00 (10.0.0.5)",
            "##LASTB",
            "alice    ssh:notty    1.2.3.4    Mon Nov 10 09:00 - 09:05  (00:05)",
            "alice    ssh:notty    1.2.3.5    Mon Nov 10 09:10 - 09:11  (00:01)",
            "##KEYS",
            "@@KEYFILE /home/alice/.ssh/authorized_keys 1762700000 600",
            "@@KEYLINE 1 2048 SHA256:abc123 alice@example (RSA)",
            "@@KEYLINE 2 2048 SHA256:def456 laptop (RSA)",
            "##SSHD",
            "permitrootlogin yes",
            "passwordauthentication yes",
            "permitemptypasswords no",
            "##SUDO",
            "wheel:x:10:alice",
            "alice ALL=(ALL) NOPASSWD: ALL"
        });

        var data = AccountAuditParser.Parse(output, new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc));

        Assert.True(data.ShadowReadable);
        Assert.Contains(data.Users, u => (string?)u["name"] == "alice" && (string?)u["passwordStatus"] == "空密码");
        Assert.Contains(data.Users, u => (string?)u["name"] == "evil" && (string?)u["riskLevel"] == "高风险");
        Assert.Contains(data.Users, u => (string?)u["name"] == "alice" && (int)u["failedLogins"]! == 2);
        Assert.Contains(data.Users, u => (string?)u["name"] == "alice" && (int)u["authorizedKeyCount"]! == 2);
        Assert.Contains(data.Users, u => (string?)u["name"] == "evil" && (string?)u["hashAlgorithm"] == "MD5");

        var keyFile = Assert.Single(data.AuthorizedKeys);
        Assert.Equal("alice", keyFile["user"]);
        Assert.Equal(2, keyFile["keyCount"]);
        var keys = Assert.IsType<List<Dictionary<string, object?>>>(keyFile["keys"]);
        Assert.Contains(keys, k => (string?)k["fingerprint"] == "SHA256:abc123" &&
                                  (string?)k["type"] == "RSA" &&
                                  (string?)k["comment"] == "alice@example");

        Assert.Contains(data.Findings, f => (string?)f["id"] == "permit_root_login");
        Assert.Contains(data.Findings, f => (string?)f["id"] == "sudo_nopasswd");
        Assert.Contains(data.Findings, f => (string?)f["id"] == "user_risk");
        Assert.True((int)data.Summary["high"]! > 0);
    }
}
