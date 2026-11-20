using System.Threading;
using LitSSHmcp.App.Services.RemoteFiles;
using LitSSHmcp.App.Views;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class RemoteFileDiffTests
{
    [Fact]
    public void Diff_aligns_equal_added_and_removed_lines()
    {
        var diff = RemoteFileDiff.Build("a\nb\nc", "a\nx\nc\nd");

        Assert.Equal(2, diff.Added);
        Assert.Equal(1, diff.Removed);
        Assert.Contains(diff.Left, line => line.Kind == DiffLineKind.Removed && line.Text == "b");
        Assert.Contains(diff.Right, line => line.Kind == DiffLineKind.Added && line.Text == "x");
        Assert.Contains(diff.Right, line => line.Kind == DiffLineKind.Added && line.Text == "d");
    }

    [Fact]
    public void Diff_window_parses_xaml()
    {
        var ex = RunSta(() => _ = new RemoteFileDiffWindow(
            "diff", "left", "right", "a\nb", "a\nc"));
        Assert.Null(ex);
    }

    [Fact]
    public void Conflict_and_history_windows_parse_xaml()
    {
        var ex = RunSta(() =>
        {
            _ = new RemoteFileConflictWindow("/tmp/a", "left", "right");
            var vm = new LitSSHmcp.App.ViewModels.RemoteFileEditorViewModel(
                new LitSSHmcp.Core.Models.SshServerConfig { Id = "s1", Name = "s1", Host = "127.0.0.1" },
                null!);
            var tab = new LitSSHmcp.App.ViewModels.RemoteFileEditTab("/tmp") { RemotePath = "/tmp/a" };
            _ = new RemoteFileHistoryWindow(vm, tab, "content");
        });
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
