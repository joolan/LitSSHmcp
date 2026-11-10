using System.Text.Json;
using LitSSHmcp.Core.Services.Snapshot;
using Xunit;

namespace LitSSHmcp.Tests;

public class SnapshotDiffTests
{
    private static string Snapshot(string users, string keys, string listeners, string units,
        string containers, string domains, string configHash)
    {
        var json = new
        {
            collectorVersion = 2,
            sections = new
            {
                accounts = new
                {
                    status = "ok",
                    data = new
                    {
                        users = JsonSerializer.Deserialize<object>(users),
                        authorizedKeys = JsonSerializer.Deserialize<object>(keys)
                    }
                },
                portmap = new
                {
                    status = "ok",
                    data = new { listeners = JsonSerializer.Deserialize<object>(listeners) }
                },
                systemd = new
                {
                    status = "ok",
                    data = new { units = JsonSerializer.Deserialize<object>(units) }
                },
                docker = new
                {
                    status = "ok",
                    data = new { containers = JsonSerializer.Deserialize<object>(containers) }
                },
                nginx_tls = new
                {
                    status = "ok",
                    data = new
                    {
                        configHash,
                        domains = JsonSerializer.Deserialize<object>(domains)
                    }
                }
            }
        };
        return JsonSerializer.Serialize(json);
    }

    [Fact]
    public void Compares_accounts_ports_services_docker_and_nginx()
    {
        var from = Snapshot(
            users: """[{"name":"alice","passwordStatus":"已设置","riskLevel":"正常"},{"name":"bob","passwordStatus":"已设置","riskLevel":"中风险"}]""",
            keys: """[{"user":"alice","keys":[{"fingerprint":"SHA256:one"}]}]""",
            listeners: """[{"proto":"tcp","bind":"0.0.0.0","port":22,"process":"sshd"},{"proto":"tcp","bind":"0.0.0.0","port":80,"process":"nginx"}]""",
            units: """[{"unit":"nginx.service","active":"failed"}]""",
            containers: """[{"name":"app","state":"running"}]""",
            domains: """[{"domain":"a.com"}]""",
            configHash: "aaa");

        var to = Snapshot(
            users: """[{"name":"alice","passwordStatus":"空密码","riskLevel":"高风险"},{"name":"carol","passwordStatus":"已设置","riskLevel":"高风险"}]""",
            keys: """[{"user":"carol","keys":[{"fingerprint":"SHA256:two"}]}]""",
            listeners: """[{"proto":"tcp","bind":"0.0.0.0","port":22,"process":"sshd"},{"proto":"tcp","bind":"0.0.0.0","port":443,"process":"nginx"}]""",
            units: """[{"unit":"nginx.service","active":"active"},{"unit":"mysql.service","active":"failed"}]""",
            containers: """[{"name":"app","state":"exited"},{"name":"api","state":"running"}]""",
            domains: """[{"domain":"b.com"}]""",
            configHash: "bbb");

        var diff = SnapshotDiffService.Compare(from, to);
        Assert.True((bool)diff["available"]!);

        var accounts = (Dictionary<string, object?>)diff["accounts"]!;
        Assert.Contains("carol", (List<string>)accounts["addedUsers"]!);
        Assert.Contains("bob", (List<string>)accounts["removedUsers"]!);
        Assert.Contains((List<Dictionary<string, object?>>)accounts["passwordStatusChanged"]!,
            item => (string?)item["user"] == "alice" && (string?)item["to"] == "空密码");
        Assert.Contains("carol|SHA256:two", (List<string>)accounts["addedAuthorizedKeys"]!);
        Assert.Contains("alice|SHA256:one", (List<string>)accounts["removedAuthorizedKeys"]!);

        var ports = (Dictionary<string, object?>)diff["ports"]!;
        Assert.Contains("tcp 0.0.0.0:443 (nginx)", (List<string>)ports["added"]!);
        Assert.Contains("tcp 0.0.0.0:80 (nginx)", (List<string>)ports["removed"]!);

        var services = (Dictionary<string, object?>)diff["services"]!;
        Assert.Equal(new[] { "mysql.service" }, (List<string>)services["newlyFailed"]!);
        Assert.Equal(new[] { "nginx.service" }, (List<string>)services["recovered"]!);

        var docker = (Dictionary<string, object?>)diff["docker"]!;
        Assert.Contains("api (running)", (List<string>)docker["added"]!);
        Assert.Contains((List<Dictionary<string, object?>>)docker["stateChanged"]!,
            item => (string?)item["name"] == "app" &&
                    (string?)item["from"] == "running" &&
                    (string?)item["to"] == "exited");

        var nginx = (Dictionary<string, object?>)diff["nginx"]!;
        Assert.True((bool)nginx["configChanged"]!);
        Assert.Contains("b.com", (List<string>)nginx["addedDomains"]!);
        Assert.Contains("a.com", (List<string>)nginx["removedDomains"]!);
    }
}
