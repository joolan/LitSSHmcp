using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class RemoteFileBrowserTerminalCommandTests
{
    [Fact]
    public void SendTerminalCommand_raises_event_only_after_success()
    {
        var vm = new RemoteFileBrowserViewModel(
            new SshServerConfig { Name = "t", Host = "127.0.0.1" },
            new SshService());
        var fired = 0;
        vm.TerminalCommandSent += () => fired++;

        vm.TerminalCommandSender = _ => null;
        Assert.Null(vm.SendTerminalCommand("tail -n 1 -- '/tmp/a.log'"));
        Assert.Equal(1, fired);
        Assert.Contains("已发送到终端", vm.StatusMessage);

        vm.TerminalCommandSender = _ => "busy";
        Assert.Equal("busy", vm.SendTerminalCommand("tail -n 1 -- '/tmp/a.log'"));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Symlinks_use_a_distinct_glyph()
    {
        Assert.Equal("🔗", new RemoteFileItem { Name = "link", FullName = "/link", IsSymbolicLink = true, IsDirectory = true }.Glyph);
        Assert.Equal("📁", new RemoteFileItem { Name = "dir", FullName = "/dir", IsDirectory = true }.Glyph);
        Assert.Equal("📄", new RemoteFileItem { Name = "file", FullName = "/file" }.Glyph);
    }

    [Fact]
    public void SendTerminalCommand_does_not_raise_event_without_sender()
    {
        var vm = new RemoteFileBrowserViewModel(
            new SshServerConfig { Name = "t", Host = "127.0.0.1" },
            new SshService());
        var fired = 0;
        vm.TerminalCommandSent += () => fired++;

        Assert.NotNull(vm.SendTerminalCommand("tail -n 1 -- '/tmp/a.log'"));
        Assert.Equal(0, fired);
    }

    [Fact]
    public void RequestCd_raises_event_when_terminal_callback_is_present()
    {
        var vm = new RemoteFileBrowserViewModel(
            new SshServerConfig { Name = "t", Host = "127.0.0.1" },
            new SshService());
        var fired = 0;
        string? directory = null;
        vm.TerminalCommandSent += () => fired++;
        vm.CdRequested = path => directory = path;

        vm.RequestCd(new RemoteFileItem { Name = "a.log", FullName = "/var/log/a.log", IsDirectory = false });

        Assert.Equal("/var/log", directory);
        Assert.Equal(1, fired);
    }
}
