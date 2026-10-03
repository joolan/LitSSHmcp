using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.McpServer.Services;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

public class ToolSupportTests
{
    [Fact]
    public void Truncate_marks_and_reports_original_length()
    {
        var (text, truncated, original) = ToolSupport.Truncate(new string('x', 25), 10);

        Assert.True(truncated);
        Assert.Equal(25, original);
        Assert.StartsWith(new string('x', 10), text);
    }

    [Fact]
    public void Truncate_keeps_short_values_intact()
    {
        var (text, truncated, original) = ToolSupport.Truncate("hello", 10);

        Assert.False(truncated);
        Assert.Equal("hello", text);
        Assert.Equal(5, original);
    }

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(50, 50)]
    [InlineData(9999, ToolSupport.MaxHistoryLimit)]
    public void ClampLimit_keeps_limit_in_range(int input, int expected)
    {
        Assert.Equal(expected, ToolSupport.ClampLimit(input));
    }

    [Theory]
    [InlineData("rate_limited", "rate_limited")]
    [InlineData("timeout", "timeout")]
    [InlineData("auth", "auth_failed")]
    [InlineData("host_key", "host_key_mismatch")]
    [InlineData("network", "connection_error")]
    [InlineData(null, "failed")]
    public void CommandFailureStatus_maps_error_kinds(string? kind, string expected)
    {
        Assert.Equal(expected, ToolSupport.CommandFailureStatus(new CommandResult { ErrorKind = kind }));
    }

    [Theory]
    [InlineData(ApprovalOutcome.Rejected, "rejected")]
    [InlineData(ApprovalOutcome.AutoRejected, "rejected")]
    [InlineData(ApprovalOutcome.Timeout, "approval_timeout")]
    [InlineData(ApprovalOutcome.Unavailable, "approval_unavailable")]
    public void ApprovalOutcomeText_maps_outcome(ApprovalOutcome outcome, string expectedStatus)
    {
        var (status, error) = ApprovalOutcomeText.Describe(outcome, 45);

        Assert.Equal(expectedStatus, status);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ResolveServer_refuses_ambiguous_name()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "1", Name = "web", Host = "10.0.0.1" },
                new SshServerConfig { Id = "2", Name = "web", Host = "10.0.0.2" }
            }
        };

        var (server, status, error) = ToolSupport.ResolveServer(config, "web");

        Assert.Null(server);
        Assert.Equal("server_ambiguous", status);
        Assert.Contains("10.0.0.1", error);
        Assert.Contains("10.0.0.2", error);
    }

    [Fact]
    public void ResolveServer_prefers_exact_id_over_name_collision()
    {
        var config = new AppConfig
        {
            Servers = new[]
            {
                new SshServerConfig { Id = "web", Name = "prod", Host = "10.0.0.1" },
                new SshServerConfig { Id = "2", Name = "web", Host = "10.0.0.2" }
            }
        };

        var (server, status, _) = ToolSupport.ResolveServer(config, "web");

        Assert.Null(status);
        Assert.NotNull(server);
        Assert.Equal("web", server!.Id);
    }

    [Fact]
    public void ResolveServer_refuses_disabled_server_by_id()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web", Host = "10.0.0.1", Disabled = true } }
        };

        var (server, status, error) = ToolSupport.ResolveServer(config, "s1");

        Assert.Null(server);
        Assert.Equal("server_disabled", status);
        Assert.Contains("禁用", error);
    }

    [Fact]
    public void ResolveServer_refuses_disabled_server_by_name()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web", Host = "10.0.0.1", Disabled = true } }
        };

        var (server, status, _) = ToolSupport.ResolveServer(config, "web");

        Assert.Null(server);
        Assert.Equal("server_disabled", status);
    }

    [Fact]
    public void ResolveServer_allows_enabled_server()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web", Host = "10.0.0.1" } }
        };

        var (server, status, _) = ToolSupport.ResolveServer(config, "s1");

        Assert.NotNull(server);
        Assert.Null(status);
    }

    [Fact]
    public void ServerNotFound_hints_about_disabled_servers()
    {
        var config = new AppConfig
        {
            Servers = new[] { new SshServerConfig { Id = "s1", Name = "web", Host = "10.0.0.1", Disabled = true } }
        };

        var (status, error) = ToolSupport.ServerNotFound(config, "missing");

        Assert.Equal("server_not_found", status);
        Assert.Contains("禁用", error);
    }

    [Fact]
    public void ResolveDatasource_refuses_ambiguous_name()
    {
        var config = new AppConfig
        {
            DataSources = new[]
            {
                new DataSourceConfig { Id = "1", Name = "order", Host = "10.0.0.1", Type = "mysql" },
                new DataSourceConfig { Id = "2", Name = "order", Host = "10.0.0.2", Type = "mysql" }
            }
        };

        var (ds, status, _) = ToolSupport.ResolveDatasource(config, "order");

        Assert.Null(ds);
        Assert.Equal("datasource_ambiguous", status);
    }

    [Fact]
    public void RedactSecrets_masks_all_server_secrets()
    {
        var server = new SshServerConfig
        {
            Password = "sshPass",
            KeyFilePassphrase = "keyPass",
            SudoPassword = "sudoPass"
        };

        var text = "stdout sshPass stderr keyPass prompt sudoPass done";
        var red = ToolSupport.RedactSecrets(text, server);

        Assert.DoesNotContain("sshPass", red);
        Assert.DoesNotContain("keyPass", red);
        Assert.DoesNotContain("sudoPass", red);
        Assert.Contains("******", red);
    }

    [Fact]
    public void RedactSecrets_handles_null_and_empty()
    {
        var server = new SshServerConfig();
        Assert.Equal(string.Empty, ToolSupport.RedactSecrets(null, server));
        Assert.Equal(string.Empty, ToolSupport.RedactSecrets(string.Empty, server));
        Assert.Equal("plain", ToolSupport.RedactSecrets("plain", server));
    }

    [Theory]
    [InlineData("tail -f /var/log/app.log")]
    [InlineData("tail -F /x")]
    [InlineData("docker logs -f web")]
    [InlineData("docker logs --follow web")]
    [InlineData("journalctl -f")]
    [InlineData("kubectl logs -f pod")]
    [InlineData("vi /etc/hosts")]
    [InlineData("top")]
    [InlineData("watch -n1 date")]
    [InlineData("sudo rm -rf /tmp/x")]
    [InlineData("su - root")]
    [InlineData("ping 10.0.0.1")]
    [InlineData("nc 10.0.0.1 80")]
    [InlineData("read x")]
    [InlineData("docker exec -it web bash")]
    [InlineData("docker attach web")]
    public void BlockingCommandHint_flags_hanging_or_interactive(string command)
    {
        Assert.NotNull(ToolSupport.BlockingCommandHint(command));
    }

    [Theory]
    [InlineData("df -h")]
    [InlineData("tail -n 200 /var/log/app.log")]
    [InlineData("docker logs --tail 200 web")]
    [InlineData("journalctl -n 200 --no-pager")]
    [InlineData("ping -c 4 10.0.0.1")]
    [InlineData("ps -eo pid,args | grep -i java")]
    [InlineData("grep -n error /var/log/app.log")]
    [InlineData("docker exec web ps -ef")]
    [InlineData("")]
    [InlineData(null)]
    public void BlockingCommandHint_allows_normal_commands(string? command)
    {
        Assert.Null(ToolSupport.BlockingCommandHint(command!));
    }
}
