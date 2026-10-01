// 【同步约定 · 请勿删除】本文件中的工具若发生变动(新增/改名/删除、参数或描述变化), 必须同步更新:
//   ① docs/TOOLS.md —— 工具说明的唯一事实来源(接入说明、意图路由表、参数与返回结构);
//   ② App 端菜单"配置 → MCP工具说明"(McpToolsWindow, 内容由 docs/TOOLS.md 嵌入) + get_usage_guide 内置清单(由注解反射生成, 无需手改);
//   ③ 若新增了工具类, 记得在 Program.cs 注册 WithTools<T>()。
// 只同步其一, AI 客户端拿到的工具说明就会与实际能力不一致。详见 docs/TOOLS.md 顶部"同步约定"。
using System.ComponentModel;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol;
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

    [McpServerTool(Name = "ssh_upload_file", UseStructuredContent = true, OutputSchemaType = typeof(FileTransferResultDto), Destructive = true, OpenWorld = true)]
    [Description("上传本地文件到SSH服务器(需桌面确认)")]
    public async Task<FileTransferResultDto> UploadFile(
        [Description("服务器ID, 可用ssh_list_servers列出")] string serverId,
        [Description("本地文件路径(本机Windows路径)")] string localPath,
        [Description("服务器上的目标文件路径(含文件名)")] string remotePath,
        IProgress<ProgressNotificationValue>? progress = null)
    {
        var config = await _configService.LoadConfigAsync();
        var server = config.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server == null)
            return FileTransferResultDto.Fail("server_not_found", $"服务器未找到: {serverId}");

        var ft = config.Security.FileTransfer;
        if (!ft.Enabled)
            return FileTransferResultDto.Fail("file_transfer_disabled", "文件传输功能已被禁用。");

        if (!PathPolicy.IsLocalPathAllowed(localPath, ft.AllowedLocalPaths))
            return FileTransferResultDto.Fail("path_not_allowed",
                $"本地路径不在允许范围内: {localPath}", null, ft.AllowedLocalPaths, null);

        if (!PathPolicy.IsRemotePathAllowed(remotePath, ft.AllowedRemotePaths))
            return FileTransferResultDto.Fail("path_not_allowed",
                $"远程路径不在允许范围内: {remotePath}", null, null, ft.AllowedRemotePaths);

        if (!File.Exists(localPath))
            return FileTransferResultDto.Fail("file_not_found", $"本地文件不存在: {localPath}");

        var fileInfo = new FileInfo(localPath);
        if (fileInfo.Length > ft.MaxFileSizeBytes)
            return FileTransferResultDto.Fail("file_too_large",
                $"文件大小超过限制。当前: {FormatFileSize(fileInfo.Length)}, 最大允许: {FormatFileSize(ft.MaxFileSizeBytes)}");

        if (ft.RequireApproval)
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
                return FileTransferResultDto.Fail("rejected",
                    "文件上传被用户拒绝。需要用户手动确认后才能执行文件传输操作。", "user_rejected");
            }
        }

        var result = await _sshService.UploadFileAsync(server, localPath, remotePath, ToTransferProgress(progress));

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

        return new FileTransferResultDto
        {
            Success = result.Success,
            Status = result.Success ? null : "transfer_error",
            Message = result.Message,
            BytesTransferred = result.BytesTransferred,
            DurationMs = result.Duration.TotalMilliseconds
        };
    }

    [McpServerTool(Name = "ssh_download_file", UseStructuredContent = true, OutputSchemaType = typeof(FileTransferResultDto), OpenWorld = true)]
    [Description("从SSH服务器下载文件到本地(需桌面确认)")]
    public async Task<FileTransferResultDto> DownloadFile(
        [Description("服务器ID, 可用ssh_list_servers列出")] string serverId,
        [Description("服务器上的源文件路径")] string remotePath,
        [Description("本地保存路径(本机Windows路径, 含文件名)")] string localPath,
        IProgress<ProgressNotificationValue>? progress = null)
    {
        var config = await _configService.LoadConfigAsync();
        var server = config.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server == null)
            return FileTransferResultDto.Fail("server_not_found", $"服务器未找到: {serverId}");

        var ft = config.Security.FileTransfer;
        if (!ft.Enabled)
            return FileTransferResultDto.Fail("file_transfer_disabled", "文件传输功能已被禁用。");

        if (!PathPolicy.IsRemotePathAllowed(remotePath, ft.AllowedRemotePaths))
            return FileTransferResultDto.Fail("path_not_allowed",
                $"远程路径不在允许范围内: {remotePath}", null, null, ft.AllowedRemotePaths);

        if (!PathPolicy.IsLocalPathAllowed(localPath, ft.AllowedLocalPaths))
            return FileTransferResultDto.Fail("path_not_allowed",
                $"本地路径不在允许范围内: {localPath}", null, ft.AllowedLocalPaths, null);

        if (ft.RequireApproval)
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
                return FileTransferResultDto.Fail("rejected",
                    "文件下载被用户拒绝。需要用户手动确认后才能执行文件传输操作。", "user_rejected");
            }
        }

        var result = await _sshService.DownloadFileAsync(server, remotePath, localPath, ToTransferProgress(progress));

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

        return new FileTransferResultDto
        {
            Success = result.Success,
            Status = result.Success ? null : "transfer_error",
            Message = result.Message,
            BytesTransferred = result.BytesTransferred,
            DurationMs = result.Duration.TotalMilliseconds
        };
    }

    // 把 MCP 进度通知(IProgress<ProgressNotificationValue>)适配为 SSH 文件传输进度回调, 供前端展示上传/下载进度
    private static IProgress<FileTransferProgress>? ToTransferProgress(IProgress<ProgressNotificationValue>? progress) =>
        progress is null
            ? null
            : new Progress<FileTransferProgress>(p => progress.Report(new ProgressNotificationValue
            {
                Progress = p.BytesTransferred,
                Total = p.TotalBytes > 0 ? (float?)p.TotalBytes : null,
                Message = p.TotalBytes > 0 ? $"{p.Percentage:F0}%" : null
            }));

    [McpServerTool(Name = "ssh_list_files", UseStructuredContent = true, OutputSchemaType = typeof(RemoteFileListDto), ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("列出SSH服务器上指定目录的文件")]
    public async Task<RemoteFileListDto> ListRemoteFiles(
        [Description("服务器ID, 可用ssh_list_servers列出")] string serverId,
        [Description("服务器上的目录路径, 如 /var/log")] string remotePath)
    {
        var config = await _configService.LoadConfigAsync();
        var server = config.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server == null)
            return RemoteFileListDto.Fail("server_not_found", $"服务器未找到: {serverId}");

        var files = await _sshService.ListRemoteFilesAsync(server, remotePath);
        return new RemoteFileListDto
        {
            Success = true,
            Path = remotePath,
            Files = files.Select(f => new RemoteFileDto
            {
                Name = f.Name,
                FullName = f.FullName,
                Size = FormatFileSize(f.Size),
                LastModified = f.LastModified,
                IsDirectory = f.IsDirectory,
                IsSymbolicLink = f.IsSymbolicLink
            }).ToList()
        };
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
