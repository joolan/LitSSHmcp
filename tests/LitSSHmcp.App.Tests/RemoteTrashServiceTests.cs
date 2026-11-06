using System.Text;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class RemoteTrashServiceTests
{
    private static SshServerConfig Server() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "测试服务器",
        Host = "127.0.0.1",
        Port = 22
    };

    [Fact]
    public async Task Move_to_trash_uses_mv_and_writes_manifest_without_rm()
    {
        var ssh = new FakeSshService();
        var service = new RemoteTrashService(Server(), ssh);
        var item = new RemoteFileItem
        {
            Name = "a b.txt",
            FullName = "/var/www/a b.txt",
            IsDirectory = false
        };

        var result = await service.MoveToTrashAsync(new[] { item });

        Assert.Equal(1, result.Moved);
        Assert.Empty(result.Errors);
        var command = Assert.Single(ssh.CommandLines);
        Assert.Contains("mv -- '/var/www/a b.txt' \"$ROOT/files/", command);
        Assert.Contains("$ROOT/info/", command);
        Assert.Contains("trashinfo", command);
        Assert.Contains("PathB64=", command);
        Assert.DoesNotContain("rm -rf -- '/var/www/a b.txt'", command);
    }

    [Fact]
    public async Task Remote_file_delete_moves_to_trash_instead_of_rm()
    {
        var ssh = new FakeSshService();
        var viewModel = new RemoteFileBrowserViewModel(Server(), ssh);

        await viewModel.DeleteAsync(new[]
        {
            new RemoteFileItem { Name = "a.txt", FullName = "/tmp/a.txt" }
        });

        var command = Assert.Single(ssh.CommandLines);
        Assert.Contains("mv -- '/tmp/a.txt'", command);
        Assert.Contains("回收站", viewModel.StatusMessage);
    }

    [Fact]
    public async Task List_parses_manifest_and_decodes_original_path()
    {
        var path = "/home/user/旧文件.txt";
        var name = "旧文件.txt";
        var manifest =
            "##TRASH:20261106-153012-abcd1234\n" +
            "Version=1\n" +
            "PathB64=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(path)) + "\n" +
            "NameB64=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(name)) + "\n" +
            "Type=file\n" +
            "Size=2048\n" +
            "DeletionDate=2026-11-06T10:00:00+00:00\n" +
            "##END\n";
        var ssh = new FakeSshService { OutputResolver = _ => manifest };
        var service = new RemoteTrashService(Server(), ssh);

        var items = await service.ListAsync();

        var item = Assert.Single(items);
        Assert.Equal(path, item.OriginalPath);
        Assert.Equal(name, item.Name);
        Assert.Equal(2048, item.Size);
        Assert.Equal("2 KB", item.SizeText);
    }

    [Fact]
    public async Task Restore_quotes_original_path_and_reports_final_path()
    {
        var ssh = new FakeSshService
        {
            OutputResolver = _ => "##RESTORED:/tmp/a'b.txt\n"
        };
        var service = new RemoteTrashService(Server(), ssh);

        var result = await service.RestoreAsync(new RemoteTrashItem
        {
            Id = "20261106-153012-abcd1234",
            OriginalPath = "/tmp/a'b.txt",
            Name = "a'b.txt"
        });

        Assert.True(result.Success);
        Assert.Equal("/tmp/a'b.txt", result.RestoredPath);
        var command = Assert.Single(ssh.CommandLines);
        Assert.Contains("'/tmp/a'\\''b.txt'", command);
        Assert.Contains("##RESTORED:", command);
    }
}
