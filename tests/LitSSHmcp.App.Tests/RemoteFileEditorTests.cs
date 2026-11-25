using System.Threading;
using LitSSHmcp.App.Services.RemoteFiles;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class RemoteFileEditorTests
{
    [Fact]
    public void New_tab_title_tracks_name_and_dirty_state()
    {
        var tab = new RemoteFileEditTab("/etc");
        Assert.Equal("未命名", tab.Title);
        tab.IsNew = true;
        tab.RemotePath = "/etc/nginx.conf";
        Assert.True(tab.IsNew);
        tab.IsDirty = true;
        Assert.Equal("nginx.conf *", tab.Title);
        tab.IsDirty = false;
        Assert.Equal("nginx.conf", tab.Title);
    }

    [Fact]
    public void Encoding_helpers_roundtrip_gbk_and_line_endings()
    {
        var gbk = RemoteTextFileService.EncodingForName("gbk");
        var bytes = gbk.GetBytes("你好");
        Assert.Equal("你好", RemoteTextFileService.DecodeBytes(bytes, gbk));

        Assert.Equal("a\r\nb\r\n", RemoteTextFileService.NormalizeLineEnding("a\nb\n", "CRLF"));
        Assert.Equal("a\nb\n", RemoteTextFileService.NormalizeLineEnding("a\r\nb\r\n", "LF"));
        Assert.Equal("a\rb\r", RemoteTextFileService.NormalizeLineEnding("a\nb\n", "CR"));
    }

    [Fact]
    public void Delete_backup_command_is_scoped_to_current_file_backup_directory()
    {
        const string backup = "/home/u/.litssh-backups/0123456789abcdef/20260101-120000-000-a.txt";
        var command = RemoteTextFileService.BuildDeleteBackupCommand(backup, "/home/u/notes/a.txt");

        Assert.Contains($"rm -f -- '{backup}'", command);
        Assert.Contains($"case '{backup}' in \"$dir\"/*", command);
        Assert.Contains("exit 1", command);
    }

    [Fact]
    public void Editor_window_parses_xaml()
    {
        var ex = RunSta(() => _ = new RemoteFileEditorWindow(
            new SshServerConfig { Id = "s1", Name = "web-01", Host = "127.0.0.1" },
            null!, "/home/test"));
        Assert.Null(ex);
    }

    private static Exception? RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return captured;
    }
}
