using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.Services.Backup;

public interface IBackupUploader
{
    Task UploadAsync(BackupOssConfig config, string accessKeySecret, string localPath,
        string objectKey, CancellationToken ct = default);

    Task TestAsync(BackupOssConfig config, string accessKeySecret, CancellationToken ct = default);
}
