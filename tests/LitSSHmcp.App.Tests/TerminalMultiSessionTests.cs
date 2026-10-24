using System.IO;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class TerminalMultiSessionTests
{
    private static SshServerConfig Server() => new() { Id = "s1", Name = "web01", Host = "10.0.0.5" };

    [Fact]
    public void Same_server_terminals_are_numbered_and_have_distinct_layout_keys()
    {
        var ssh = new SshService();
        var first = new TerminalSessionViewModel(Server(), ssh);
        var second = new TerminalSessionViewModel(Server(), ssh, ordinal: 2);

        Assert.Equal("web01", first.Title);
        Assert.Equal("web01 (2)", second.Title);
        Assert.NotEqual(first.LayoutKey, second.LayoutKey);
    }

    [Fact]
    public void Renamed_terminal_keeps_custom_title()
    {
        var terminal = new TerminalSessionViewModel(Server(), new SshService(), ordinal: 3);

        terminal.Rename("custom");

        Assert.Equal("custom", terminal.Title);
    }

    [Fact]
    public void Session_loggers_created_in_same_second_do_not_collide()
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-sessionlog-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var a = new SessionLogger("web01", "10.0.0.5", logRoot: dir);
            using var b = new SessionLogger("web01", "10.0.0.5", logRoot: dir);

            Assert.NotEqual(a.FilePath, b.FilePath);
            Assert.NotEqual(a.CastPath, b.CastPath);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }
}
