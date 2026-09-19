using System.ComponentModel;
using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

[McpServerToolType]
public class FileTransferTools
{
    private readonly IConfigService _configService;
    private readonly ISshService _sshService;
    private readonly ICommandFilterService _commandFilter;
    private readonly IApprovalService _approvalService;
    private readonly IAuditLogService _auditLogService;

    public FileTransferTools(
        IConfigService configService,
        ISshService sshService,
        ICommandFilterService commandFilter,
        IApprovalService approvalService,
        IAuditLogService auditLogService)
    {
        _configService = configService;
        _sshService = sshService;
        _commandFilter = commandFilter;
        _approvalService = approvalService;
        _auditLogService = auditLogService;
    }

    [McpServerTool]
    [Description("Upload a local file to an SSH server")]
    public async Task<string> UploadFile(
        [Description("Server ID")] string serverId,
        [Description("Local file path")] string localPath,
        [Description("Remote file path")] string remotePath)
    {
        var config = await _configService.LoadConfigAsync();
        var server = config.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server == null)
            return JsonSerializer.Serialize(new { success = false, error = $"服务器未找到: {serverId}", status = "server_not_found" });

        if (!File.Exists(localPath))
            return JsonSerializer.Serialize(new { success = false, error = $"本地文件不存在: {localPath}", status = "file_not_found" });

        var fileInfo = new FileInfo(localPath);
        if (fileInfo.Length > config.Security.FileTransfer.MaxFileSizeBytes)
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"文件大小超过限制。当前: {FormatFileSize(fileInfo.Length)}, 最大允许: {FormatFileSize(config.Security.FileTransfer.MaxFileSizeBytes)}",
                status = "file_too_large"
            });

        if (config.Security.FileTransfer.RequireApproval)
        {
            var approved = await _approvalService.RequestApprovalAsync(
                server.Name,
                $"上传 {Path.GetFileName(localPath)} ({FormatFileSize(fileInfo.Length)})",
                CommandFilterResult.Sensitive,
                $"{localPath} -> {remotePath}");

            if (!approved)
            {
                await _auditLogService.LogCommandAsync(new CommandAuditLog
                {
                    ServerId = server.Id,
                    ServerName = server.Name,
                    Command = $"UPLOAD: {localPath} -> {remotePath}",
                    Status = CommandStatus.Rejected,
                    IsFileTransfer = true,
                    FilePath = remotePath,
                    FileSize = fileInfo.Length
                });
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "文件上传被用户拒绝。需要用户手动确认后才能执行文件传输操作。",
                    status = "rejected",
                    reason = "user_rejected"
                });
            }
        }

        var result = await _sshService.UploadFileAsync(server, localPath, remotePath);

        await _auditLogService.LogCommandAsync(new CommandAuditLog
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Command = $"UPLOAD: {localPath} -> {remotePath}",
            Result = result.Message,
            Status = result.Success ? CommandStatus.Executed : CommandStatus.Failed,
            IsFileTransfer = true,
            FilePath = remotePath,
            FileSize = fileInfo.Length
        });

        return JsonSerializer.Serialize(new
        {
            success = result.Success,
            message = result.Message,
            bytesTransferred = result.BytesTransferred,
            durationMs = result.Duration.TotalMilliseconds
        });
    }

    [McpServerTool]
    [Description("Download a file from an SSH server to local")]
    public async Task<string> DownloadFile(
        [Description("Server ID")] string serverId,
        [Description("Remote file path")] string remotePath,
        [Description("Local file path")] string localPath)
    {
        var config = await _configService.LoadConfigAsync();
        var server = config.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server == null)
            return JsonSerializer.Serialize(new { success = false, error = $"服务器未找到: {serverId}", status = "server_not_found" });

        if (config.Security.FileTransfer.RequireApproval)
        {
            var approved = await _approvalService.RequestApprovalAsync(
                server.Name,
                $"下载 {Path.GetFileName(remotePath)}",
                CommandFilterResult.Sensitive,
                $"{remotePath} -> {localPath}");

            if (!approved)
            {
                await _auditLogService.LogCommandAsync(new CommandAuditLog
                {
                    ServerId = server.Id,
                    ServerName = server.Name,
                    Command = $"DOWNLOAD: {remotePath} -> {localPath}",
                    Status = CommandStatus.Rejected,
                    IsFileTransfer = true,
                    FilePath = remotePath
                });
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "文件下载被用户拒绝。需要用户手动确认后才能执行文件传输操作。",
                    status = "rejected",
                    reason = "user_rejected"
                });
            }
        }

        var result = await _sshService.DownloadFileAsync(server, remotePath, localPath);

        await _auditLogService.LogCommandAsync(new CommandAuditLog
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Command = $"DOWNLOAD: {remotePath} -> {localPath}",
            Result = result.Message,
            Status = result.Success ? CommandStatus.Executed : CommandStatus.Failed,
            IsFileTransfer = true,
            FilePath = remotePath,
            FileSize = result.BytesTransferred
        });

        return JsonSerializer.Serialize(new
        {
            success = result.Success,
            message = result.Message,
            bytesTransferred = result.BytesTransferred,
            durationMs = result.Duration.TotalMilliseconds
        });
    }

    [McpServerTool]
    [Description("List files in a remote directory")]
    public async Task<string> ListRemoteFiles(
        [Description("Server ID")] string serverId,
        [Description("Remote directory path")] string remotePath)
    {
        var config = await _configService.LoadConfigAsync();
        var server = config.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server == null)
            return JsonSerializer.Serialize(new { success = false, error = $"服务器未找到: {serverId}", status = "server_not_found" });

        var files = await _sshService.ListRemoteFilesAsync(server, remotePath);
        return JsonSerializer.Serialize(new
        {
            success = true,
            path = remotePath,
            files = files.Select(f => new
            {
                f.Name,
                f.FullName,
                size = FormatFileSize(f.Size),
                f.LastModified,
                f.IsDirectory,
                f.IsSymbolicLink
            })
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}