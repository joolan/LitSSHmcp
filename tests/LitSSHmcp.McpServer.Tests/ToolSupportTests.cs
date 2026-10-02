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
}
