using System.Text;
using LitSSHmcp.App.Services.RemoteFiles;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>
/// 真实 SSH 集成测试。仅当设置以下环境变量时执行，否则直接返回：
/// LITSSH_TEST_SSH_HOST / LITSSH_TEST_SSH_USER / LITSSH_TEST_SSH_PASSWORD
/// 可选：LITSSH_TEST_SSH_PORT（默认 22）/ LITSSH_TEST_SSH_KEYFILE。
/// </summary>
public class RemoteFileSshIntegrationTests
{
    [Fact]
    public async Task Save_read_backup_and_restore_roundtrip()
    {
        var host = Environment.GetEnvironmentVariable("LITSSH_TEST_SSH_HOST");
        var user = Environment.GetEnvironmentVariable("LITSSH_TEST_SSH_USER");
        var password = Environment.GetEnvironmentVariable("LITSSH_TEST_SSH_PASSWORD");
        var keyFile = Environment.GetEnvironmentVariable("LITSSH_TEST_SSH_KEYFILE");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) ||
            (string.IsNullOrWhiteSpace(password) && string.IsNullOrWhiteSpace(keyFile)))
            return;

        var portText = Environment.GetEnvironmentVariable("LITSSH_TEST_SSH_PORT");
        var port = int.TryParse(portText, out var parsedPort) ? parsedPort : 22;
        var server = new SshServerConfig
        {
            Id = "integration",
            Name = "integration",
            Host = host,
            Port = port,
            Username = user,
            AuthType = string.IsNullOrWhiteSpace(keyFile) ? AuthType.Password : AuthType.KeyFile,
            Password = password,
            KeyFilePath = keyFile
        };
        var ssh = new SshService();
        var service = new RemoteTextFileService(ssh);
        var remotePath = $"/tmp/litssh-editor-{Guid.NewGuid():N}.txt";

        try
        {
            var first = await service.SaveAsync(
                server, remotePath, "line1\nline2\n", new UTF8Encoding(false),
                hasBom: false, lineEnding: "LF", expected: null, force: false, isNew: true);
            Assert.True(first.Success, first.Message);

            var read = await service.ReadAsync(server, remotePath);
            Assert.True(read.Success, read.Message);
            Assert.Contains("line2", read.Content);

            var second = await service.SaveAsync(
                server, remotePath, "line1\nchanged\n", new UTF8Encoding(false),
                hasBom: false, lineEnding: "LF", expected: read.Snapshot, force: false, isNew: false);
            Assert.True(second.Success, second.Message);
            Assert.False(string.IsNullOrWhiteSpace(second.BackupPath));

            var backups = await service.ListBackupsAsync(server, remotePath);
            Assert.NotEmpty(backups);

            var restore = await service.RestoreBackupAsync(server, backups[0], remotePath);
            Assert.True(restore.Success, restore.Error);
        }
        finally
        {
            try { await ssh.ExecuteCommandAsync(server, $"rm -f -- '{remotePath.Replace("'", "'\\''")}'"); }
            catch { /* ignore */ }
        }
    }
}
