using Aliyun.OSS;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.Services.Backup;

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
