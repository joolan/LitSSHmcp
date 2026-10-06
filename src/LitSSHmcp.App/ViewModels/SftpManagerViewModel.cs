using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.App.ViewModels;

/// <summary>SFTP 文件管理（双栏 + 传输队列）。</summary>
public sealed class SftpManagerViewModel
{
    public SftpManagerViewModel(SshServerConfig server, ISshService ssh)
    {
        Server = server;
        Local = new LocalFileBrowserViewModel();
        Remote = new RemoteFileBrowserViewModel(server, ssh);
        Queue = new TransferQueueViewModel(ssh, server, () => Remote.LoadAsync(), () => { Local.Load(); return Task.CompletedTask; });
        Bookmarks = SftpBookmarkStore.Load(server.Id);
    }

    /// <summary>窗口加载时触发远程目录初始化（放到此处以便测试构造窗口时不触发连接）。</summary>
    public void Initialize() => _ = Remote.InitializeAsync();

    public SshServerConfig Server { get; }
    public string Title => $"SFTP 文件管理 - {Server.Name}";
    public string Subtitle => $"{Server.Host}:{Server.Port}";

    public LocalFileBrowserViewModel Local { get; }
    public RemoteFileBrowserViewModel Remote { get; }
    public TransferQueueViewModel Queue { get; }

    /// <summary>当前服务器的远程目录书签。</summary>
    public List<string> Bookmarks { get; private set; }

    public void ReloadBookmarks() => Bookmarks = SftpBookmarkStore.Load(Server.Id);

    public void AddBookmark(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Bookmarks.Contains(path))
            return;
        Bookmarks.Add(path);
        SftpBookmarkStore.Save(Server.Id, Bookmarks);
    }

    public void RemoveBookmark(string path)
    {
        if (Bookmarks.Remove(path))
            SftpBookmarkStore.Save(Server.Id, Bookmarks);
    }

    /// <summary>上传本地当前选中项到远程当前目录。</summary>
    public void UploadSelected()
    {
        if (Local.SelectedItem is { } item)
            Queue.EnqueueUpload(new[] { item }, Remote.CurrentPath);
    }

    /// <summary>下载远程当前选中项到本地当前目录。</summary>
    public void DownloadSelected()
    {
        if (Remote.SelectedItem is { } item)
            Queue.EnqueueDownload(new[] { item }, Local.CurrentPath);
    }
}
