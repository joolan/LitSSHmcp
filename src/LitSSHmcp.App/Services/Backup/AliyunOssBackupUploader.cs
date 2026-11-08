using System.IO;
using Aliyun.OSS;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.Services.Backup;

public sealed class OssBackupItem
{
    public required string Key { get; init; }
    public required string FileName { get; init; }
    public required DateTime LastModified { get; init; }
    public required long Size { get; init; }
    public required bool Encrypted { get; init; }
    public string SizeText => BackupFormat.Size(Size);
    public string ModifiedText => LastModified.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string EncryptedText => Encrypted ? "加密" : "未加密";
}

public sealed class AliyunOssBackupUploader : IBackupUploader
{
    public Task UploadAsync(BackupOssConfig config, string accessKeySecret, string localPath,
        string objectKey, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var client = CreateClient(config, accessKeySecret);
            client.PutObject(config.Bucket.Trim(), objectKey, localPath);
        }, ct);

    public Task TestAsync(BackupOssConfig config, string accessKeySecret, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var client = CreateClient(config, accessKeySecret);
            if (!client.DoesBucketExist(config.Bucket.Trim()))
                throw new InvalidOperationException("Bucket 不存在或无权访问：" + config.Bucket);
        }, ct);

    public Task<IReadOnlyList<OssBackupItem>> ListAsync(BackupOssConfig config, string accessKeySecret,
        string prefix, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<OssBackupItem>>(() =>
        {
            var client = CreateClient(config, accessKeySecret);
            var bucket = config.Bucket.Trim();
            var result = new List<OssBackupItem>();
            string? marker = null;
            do
            {
                ct.ThrowIfCancellationRequested();
                var request = new ListObjectsRequest(bucket)
                {
                    Prefix = prefix,
                    MaxKeys = 1000,
                    Marker = marker
                };
                var listing = client.ListObjects(request);
                foreach (var summary in listing.ObjectSummaries)
                {
                    var fileName = Path.GetFileName(summary.Key);
                    if (!fileName.StartsWith("LitSSH-backup-", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var encrypted = fileName.EndsWith(".lsbak", StringComparison.OrdinalIgnoreCase);
                    if (!encrypted && !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        continue;
                    result.Add(new OssBackupItem
                    {
                        Key = summary.Key,
                        FileName = fileName,
                        LastModified = summary.LastModified,
                        Size = summary.Size,
                        Encrypted = encrypted
                    });
                }
                marker = listing.NextMarker;
                if (!listing.IsTruncated || string.IsNullOrEmpty(marker))
                    break;
            } while (true);
            return result.OrderByDescending(item => item.LastModified).ToArray();
        }, ct);

    public Task DownloadAsync(BackupOssConfig config, string accessKeySecret, string objectKey,
        string localPath, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var client = CreateClient(config, accessKeySecret);
            using var obj = client.GetObject(config.Bucket.Trim(), objectKey);
            var directory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            using var stream = File.Create(localPath);
            obj.Content.CopyTo(stream);
        }, ct);

    private static OssClient CreateClient(BackupOssConfig config, string accessKeySecret)
    {
        if (string.IsNullOrWhiteSpace(config.Endpoint) ||
            string.IsNullOrWhiteSpace(config.Bucket) ||
            string.IsNullOrWhiteSpace(config.AccessKeyId) ||
            string.IsNullOrWhiteSpace(accessKeySecret))
            throw new InvalidOperationException("OSS 配置不完整，请填写 Endpoint、Bucket、AccessKeyId 和 AccessKeySecret。");

        var endpoint = config.Endpoint.Trim().TrimEnd('/');
        if (!endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            endpoint = (config.UseHttps ? "https://" : "http://") + endpoint;

        return new OssClient(endpoint, config.AccessKeyId.Trim(), accessKeySecret);
    }
}
