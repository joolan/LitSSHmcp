using System.IO;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class AgentRelatedTargetsTests
{
    private static AppConfig Config() => new()
    {
        Servers = new[]
        {
            new SshServerConfig { Id = "s1", Name = "web-01", Host = "10.0.0.1", Port = 22 }
        },
        DataSources = new[]
        {
            new DataSourceConfig { Id = "ds1", Name = "mysql-01", Type = "mysql", Host = "10.0.0.2", Port = 3306 }
        }
    };

    private static readonly string Workspace = Path.Combine(Path.GetTempPath(), "litssh-related-ws");
    private static readonly string Shared = Path.Combine(Path.GetTempPath(), "litssh-related-shared");

    [Fact]
    public void Extracts_server_and_datasource_from_tool_args()
    {
        var server = Assert.Single(AgentRelatedTargetExtractor.Extract(
            "ssh_execute_command", "{\"serverId\":\"web-01\"}", Config(), Workspace, Shared, false, true));
        Assert.Equal(AgentRelatedTargetKind.Server, server.Kind);
        Assert.Equal("s1", server.Id);
        Assert.Equal("web-01", server.Title);

        var datasource = Assert.Single(AgentRelatedTargetExtractor.Extract(
            "mysql_query", "{\"datasourceId\":\"ds1\"}", Config(), Workspace, Shared, false, true));
        Assert.Equal(AgentRelatedTargetKind.DataSource, datasource.Kind);
        Assert.Equal("mysql-01", datasource.Title);
    }

    [Fact]
    public void Extracts_successful_ops_doc_write_only()
    {
        var target = Assert.Single(AgentRelatedTargetExtractor.Extract(
            "ops_doc_write", "{\"mode\":\"append\",\"path\":\"notes/today.md\",\"content\":\"x\"}",
            Config(), Workspace, Shared, true, true));
        Assert.Equal(AgentRelatedTargetKind.Document, target.Kind);
        Assert.Equal(Path.Combine(Workspace, "notes", "today.md"), target.DocumentPath);
        Assert.Equal("notes/today.md", target.DocumentRelativePath);

        Assert.Empty(AgentRelatedTargetExtractor.Extract(
            "ops_doc_write", "{\"content\":\"x\"}", Config(), Workspace, Shared, false, false));
        Assert.Empty(AgentRelatedTargetExtractor.Extract(
            "ops_doc_read", "{\"path\":\"notes/today.md\"}", Config(), Workspace, Shared, false, true));
    }

    [Fact]
    public void Omitted_doc_path_uses_shared_assets_document_in_session_mode()
    {
        var target = Assert.Single(AgentRelatedTargetExtractor.Extract(
            "ops_doc_write", "{\"content\":\"x\"}", Config(), Workspace, Shared, true, true));
        Assert.Equal(Path.Combine(Shared, "OPS_ASSETS.md"), target.DocumentPath);
        Assert.Equal("shared/OPS_ASSETS.md", target.DocumentRelativePath);
    }
}
