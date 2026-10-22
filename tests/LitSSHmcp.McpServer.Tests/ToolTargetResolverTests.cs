using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.McpServer.Services;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

/// <summary>
/// 工具安全目录的目标解析与访问级别判定：服务器/数据源/可选目标/多服务器/应用聚合，
/// 以及 SQL/Redis/Shell 的动态访问级别。
/// </summary>
public class ToolTargetResolverTests
{
    [Fact]
    public void Resolve_server_by_id_and_name()
    {
        var descriptor = ToolSecurityCatalog.Find("ssh_list_files")!;

        Assert.Equal(new[] { "ssh:s-1" },
            ToolTargetResolver.Resolve(Config(), descriptor, Args(("serverId", "s-1"))).TargetKeys);
        Assert.Equal(new[] { "ssh:s-1" },
            ToolTargetResolver.Resolve(Config(), descriptor, Args(("serverId", "web1"))).TargetKeys);
        Assert.Equal(new[] { "ssh:s-1" },
            ToolTargetResolver.Resolve(Config(), descriptor, Args(("serverId", "10.0.0.1"))).TargetKeys);
    }

    [Fact]
    public void Resolve_unknown_server_returns_error()
    {
        var result = ToolTargetResolver.Resolve(
            Config(), ToolSecurityCatalog.Find("ssh_list_files")!, Args(("serverId", "ghost")));

        Assert.Empty(result.TargetKeys);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Resolve_multi_server_uses_all_enabled_servers_when_omitted()
    {
        var config = Config();
        config.Servers = new[]
        {
            new SshServerConfig { Id = "s-1", Name = "web1", Host = "10.0.0.1" },
            new SshServerConfig { Id = "s-2", Name = "web2", Host = "10.0.0.2" },
            new SshServerConfig { Id = "s-disabled", Name = "old", Host = "10.0.0.3", Disabled = true }
        };

        var result = ToolTargetResolver.Resolve(
            config, ToolSecurityCatalog.Find("topology_discover")!, null);

        Assert.Null(result.Error);
        Assert.Equal(new[] { "ssh:s-1", "ssh:s-2" }, result.TargetKeys);
    }

    [Fact]
    public void Resolve_multi_server_requires_each_named_server()
    {
        var descriptor = ToolSecurityCatalog.Find("topology_discover")!;

        var result = ToolTargetResolver.Resolve(
            Config(), descriptor, Args(("serverIds", "s-1, ghost")));

        Assert.Empty(result.TargetKeys);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Resolve_application_expands_runsOn_and_connectsTo_relations()
    {
        var config = Config();
        config.Applications = new[] { new ApplicationConfig { Id = "app-1", Name = "shop" } };
        config.Relations = new[]
        {
            new RelationConfig { From = "app:app-1", To = "ssh:s-1", Type = "runsOn" },
            new RelationConfig { From = "app:app-1", To = "ds:ds-1", Type = "connectsTo" }
        };

        var result = ToolTargetResolver.Resolve(
            config, ToolSecurityCatalog.Find("app_health_snapshot")!, Args(("appId", "app-1")));

        Assert.Null(result.Error);
        Assert.Contains("ssh:s-1", result.TargetKeys);
        Assert.Contains("ds:ds-1", result.TargetKeys);
    }

    [Fact]
    public void Resolve_application_falls_back_to_host()
    {
        var config = Config();
        config.Applications = new[] { new ApplicationConfig { Id = "app-1", Name = "shop", Host = "10.0.0.1" } };

        var result = ToolTargetResolver.Resolve(
            config, ToolSecurityCatalog.Find("app_health_snapshot")!, Args(("appId", "app-1")));

        Assert.Null(result.Error);
        Assert.Equal(new[] { "ssh:s-1" }, result.TargetKeys);
    }

    [Fact]
    public void Resolve_application_without_targets_is_denied()
    {
        var config = Config();
        config.Applications = new[] { new ApplicationConfig { Id = "app-1", Name = "shop" } };

        var result = ToolTargetResolver.Resolve(
            config, ToolSecurityCatalog.Find("app_health_snapshot")!, Args(("appId", "app-1")));

        Assert.Empty(result.TargetKeys);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Required_access_is_static_for_shell_and_file_upload()
    {
        Assert.Equal(ToolAccessLevel.Exec, McpAuthorizationFilter.ResolveRequiredAccess(
            ToolSecurityCatalog.Find("ssh_execute_command")!,
            "ssh_execute_command", Args(("command", "df -h")), null));
        Assert.Equal(ToolAccessLevel.Write, McpAuthorizationFilter.ResolveRequiredAccess(
            ToolSecurityCatalog.Find("ssh_upload")!,
            "ssh_upload", Args(("serverId", "s-1")), null));
        Assert.Equal(ToolAccessLevel.Read, McpAuthorizationFilter.ResolveRequiredAccess(
            ToolSecurityCatalog.Find("ssh_list_files")!,
            "ssh_list_files", Args(("serverId", "s-1")), null));
    }

    [Fact]
    public void Required_access_for_sql_follows_filter_result()
    {
        var descriptor = ToolSecurityCatalog.Find("mysql_execute")!;

        Assert.Equal(ToolAccessLevel.Read, McpAuthorizationFilter.ResolveRequiredAccess(
            descriptor, "mysql_execute", Args(("sql", "SELECT 1")), new FixedSqlFilter(SqlFilterResult.Allowed)));

        Assert.Equal(ToolAccessLevel.Write, McpAuthorizationFilter.ResolveRequiredAccess(
            descriptor, "mysql_execute", Args(("sql", "SELECT 1")), new FixedSqlFilter(SqlFilterResult.Sensitive)));

        Assert.Equal(ToolAccessLevel.Write, McpAuthorizationFilter.ResolveRequiredAccess(
            descriptor, "mysql_execute", Args(("sql", "UPDATE t SET a=1")), new FixedSqlFilter(SqlFilterResult.Blocked)));

        Assert.Equal(ToolAccessLevel.Write, McpAuthorizationFilter.ResolveRequiredAccess(
            descriptor, "mysql_execute", Args(("sql", "SELECT 1")), null));
    }

    [Fact]
    public void Required_access_for_redis_follows_command_policy()
    {
        var descriptor = ToolSecurityCatalog.Find("redis_execute")!;

        Assert.Equal(ToolAccessLevel.Read, McpAuthorizationFilter.ResolveRequiredAccess(
            descriptor, "redis_execute", Args(("command", "GET user:1")), null));
        Assert.Equal(ToolAccessLevel.Write, McpAuthorizationFilter.ResolveRequiredAccess(
            descriptor, "redis_execute", Args(("command", "SET user:1 x")), null));
    }

    private static AppConfig Config() => new()
    {
        Servers = new[]
        {
            new SshServerConfig { Id = "s-1", Name = "web1", Host = "10.0.0.1" }
        },
        DataSources = new[]
        {
            new DataSourceConfig { Id = "ds-1", Name = "order-mysql", Host = "10.0.0.2", Type = "mysql" }
        }
    };

    private static Dictionary<string, JsonElement> Args(params (string Name, string Value)[] values)
    {
        var args = new Dictionary<string, JsonElement>();
        foreach (var (name, value) in values)
            args[name] = JsonSerializer.SerializeToElement(value);
        return args;
    }

    private sealed class FixedSqlFilter : ISqlFilterService
    {
        private readonly SqlFilterResult _result;

        public FixedSqlFilter(SqlFilterResult result) => _result = result;

        public SqlFilterResult CheckReadOnly(string sql) => _result;
        public SqlFilterResult CheckWrite(string sql) => _result;
    }
}
