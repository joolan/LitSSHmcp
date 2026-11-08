using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.Services.Backup;

public interface IBackupUploader
{
    Task UploadAsync(BackupOssConfig config, string accessKeySecret, string localPath,
        string objectKey, CancellationToken ct = default);

    Task TestAsync(BackupOssConfig config, string accessKeySecret, CancellationToken ct = default);

    Task<IReadOnlyList<OssBackupItem>> ListAsync(BackupOssConfig config, string accessKeySecret,
        string prefix, CancellationToken ct = default);

    Task DownloadAsync(BackupOssConfig config, string accessKeySecret, string objectKey,
        string localPath, CancellationToken ct = default);
}
