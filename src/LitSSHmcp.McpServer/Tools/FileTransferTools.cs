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
    private readonly IApprovalService _approvalService;
    private readonly IAuditLogService _auditLogService;
    private readonly ISecurityOptionsProvider _securityOptions;

    public FileTransferTools(
        IConfigService configService,
        ISshService sshService,
        IApprovalService approvalService,
        IAuditLogService auditLogService,
        ISecurityOptionsProvider securityOptions)
    {
        _configService = configService;
        _sshService = sshService;
        _approvalService = approvalService;
        _auditLogService = auditLogService;
        _securityOptions = securityOptions;
    }

    [McpServerTool(Name = "ssh_upload_file", UseStructuredContent = true, OutputSchemaType = typeof(FileTransferResultDto), Destructive = true, OpenWorld = true)]
    [Description("上传本地文件到SSH服务器。需要人工确认(可返回status=rejected/approval_timeout/approval_unavailable); 路径必须在白名单内, 越界返回path_not_allowed并在错误里给出允许的路径; 文件过大返回file_too_large")]
    public async Task<FileTransferResultDto> UploadFile(
        [Description("服务器标识: ID/名称/主机名均可, 可用ssh_list_servers列出")] string serverId,
        [Description("本地文件路径(本机Windows路径, 必须在security.fileTransfer.allowedLocalPaths内)")] string localPath,
        [Description("服务器上的目标文件路径(含文件名, 必须在allowedRemotePaths内), 如 /var/log/app/data.json")] string remotePath,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var config = await _configService.LoadConfigAsync();
        var (server, resolveStatus, resolveError) = ToolSupport.ResolveServer(config, serverId);
        if (server == null)
            return FileTransferResultDto.Fail(resolveStatus!, resolveError!, "server_not_found");

        var ft = config.Security.FileTransfer;
        if (!ft.Enabled)
            return FileTransferResultDto.Fail("file_transfer_disabled",
                "文件传输功能已被禁用(security.fileTransfer.enabled=false)。", null, null, null, server.Id, server.Name, server.Host);

        if (!PathPolicy.IsLocalPathAllowed(localPath, ft.AllowedLocalPaths))
            return FileTransferResultDto.Fail("path_not_allowed",
                $"本地路径不在允许范围内: {localPath}", null, ft.AllowedLocalPaths, null, server.Id, server.Name, server.Host);

        if (!PathPolicy.IsRemotePathAllowed(remotePath, ft.AllowedRemotePaths))
            return FileTransferResultDto.Fail("path_not_allowed",
                $"远程路径不在允许范围内: {remotePath}", null, null, ft.AllowedRemotePaths, server.Id, server.Name, server.Host);

        if (!File.Exists(localPath))
            return FileTransferResultDto.Fail("file_not_found", $"本地文件不存在: {localPath}", null, null, null, server.Id, server.Name, server.Host);

        var fileInfo = new FileInfo(localPath);
        if (fileInfo.Length > ft.MaxFileSizeBytes)
            return FileTransferResultDto.Fail("file_too_large",
                $"文件大小超过限制。当前: {FormatFileSize(fileInfo.Length)}, 最大允许: {FormatFileSize(ft.MaxFileSizeBytes)}",
                null, null, null, server.Id, server.Name, server.Host);

        if (ft.RequireApproval)
        {
            var outcome = await _approvalService.RequestApprovalAsync(
                ToolSupport.ServerLabel(server),
                $"上传 {Path.GetFileName(localPath)} ({FormatFileSize(fileInfo.Length)})",
                CommandFilterResult.Sensitive,
                $"{localPath} -> {remotePath}",
                "文件上传",
                cancellationToken);

            if (outcome != ApprovalOutcome.Approved)
            {
                await ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
                {
                    ServerId = server.Id,
                    ServerName = server.Name,
                    Command = $"UPLOAD: {localPath} -> {remotePath}",
                    Status = CommandStatus.Rejected,
                    IsFileTransfer = true,
                    FilePath = remotePath,
                    FileSize = fileInfo.Length
                });
                var (status, error) = ApprovalOutcomeText.Describe(outcome, _securityOptions.Approval.TimeoutSeconds);
                return FileTransferResultDto.Fail(status, error, "approval", null, null, server.Id, server.Name, server.Host);
            }
        }

        var result = await _sshService.UploadFileAsync(
            server, localPath, remotePath, ToTransferProgress(progress), cancellationToken);

        await ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
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
            Status = result.Success ? null : TransferFailureStatus(result),
            Error = result.Success ? null : result.Message,
            ServerId = server.Id,
            ServerName = server.Name,
            Host = server.Host,
            Message = result.Message,
            BytesTransferred = result.BytesTransferred,
            DurationMs = result.Duration.TotalMilliseconds
        };
    }

    [McpServerTool(Name = "ssh_download_file", UseStructuredContent = true, OutputSchemaType = typeof(FileTransferResultDto), Destructive = true, OpenWorld = true)]
    [Description("从SSH服务器下载文件到本地。需要人工确认(可返回status=rejected/approval_timeout/approval_unavailable); 路径必须在白名单内, 越界返回path_not_allowed并在错误里给出允许的路径")]
    public async Task<FileTransferResultDto> DownloadFile(
        [Description("服务器标识: ID/名称/主机名均可, 可用ssh_list_servers列出")] string serverId,
        [Description("服务器上的源文件路径(必须在security.fileTransfer.allowedRemotePaths内), 如 /var/log/app/app.log")] string remotePath,
        [Description("本地保存路径(本机Windows路径, 含文件名, 必须在allowedLocalPaths内)")] string localPath,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var config = await _configService.LoadConfigAsync();
        var (server, resolveStatus, resolveError) = ToolSupport.ResolveServer(config, serverId);
        if (server == null)
            return FileTransferResultDto.Fail(resolveStatus!, resolveError!, "server_not_found");

        var ft = config.Security.FileTransfer;
        if (!ft.Enabled)
            return FileTransferResultDto.Fail("file_transfer_disabled",
                "文件传输功能已被禁用(security.fileTransfer.enabled=false)。", null, null, null, server.Id, server.Name, server.Host);

        if (!PathPolicy.IsRemotePathAllowed(remotePath, ft.AllowedRemotePaths))
            return FileTransferResultDto.Fail("path_not_allowed",
                $"远程路径不在允许范围内: {remotePath}", null, null, ft.AllowedRemotePaths, server.Id, server.Name, server.Host);

        if (!PathPolicy.IsLocalPathAllowed(localPath, ft.AllowedLocalPaths))
            return FileTransferResultDto.Fail("path_not_allowed",
                $"本地路径不在允许范围内: {localPath}", null, ft.AllowedLocalPaths, null, server.Id, server.Name, server.Host);

        if (ft.RequireApproval)
        {
            var outcome = await _approvalService.RequestApprovalAsync(
                ToolSupport.ServerLabel(server),
                $"下载 {Path.GetFileName(remotePath)}",
                CommandFilterResult.Sensitive,
                $"{remotePath} -> {localPath}",
                "文件下载",
                cancellationToken);

            if (outcome != ApprovalOutcome.Approved)
            {
                await ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
                {
                    ServerId = server.Id,
                    ServerName = server.Name,
                    Command = $"DOWNLOAD: {remotePath} -> {localPath}",
                    Status = CommandStatus.Rejected,
                    IsFileTransfer = true,
                    FilePath = remotePath
                });
                var (status, error) = ApprovalOutcomeText.Describe(outcome, _securityOptions.Approval.TimeoutSeconds);
                return FileTransferResultDto.Fail(status, error, "approval", null, null, server.Id, server.Name, server.Host);
            }
        }

        var result = await _sshService.DownloadFileAsync(
            server, remotePath, localPath, ToTransferProgress(progress), cancellationToken);

        await ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
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
            Status = result.Success ? null : TransferFailureStatus(result),
            Error = result.Success ? null : result.Message,
            ServerId = server.Id,
            ServerName = server.Name,
            Host = server.Host,
            Message = result.Message,
            BytesTransferred = result.BytesTransferred,
            DurationMs = result.Duration.TotalMilliseconds
        };
    }

    private static string TransferFailureStatus(FileTransferResult result) =>
        result.Message.Contains("限流", StringComparison.Ordinal) ? "rate_limited"
            : result.Message.Contains("超时", StringComparison.Ordinal) ? "timeout"
            : "transfer_error";

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
    [Description("列出SSH服务器上的远程目录(是服务器上的目录, 不是本机)。返回size为'1.5 MB'格式的字符串; 目录条目过多会截断并置truncated=true; 路径不存在/无权限会明确返回失败而不是空列表")]
    public async Task<RemoteFileListDto> ListRemoteFiles(
        [Description("服务器标识: ID/名称/主机名均可, 可用ssh_list_servers列出")] string serverId,
        [Description("服务器上的目录路径, 如 /var/log")] string remotePath,
        CancellationToken cancellationToken = default)
    {
        var config = await _configService.LoadConfigAsync();
        var (server, resolveStatus, resolveError) = ToolSupport.ResolveServer(config, serverId);
        if (server == null)
            return RemoteFileListDto.Fail(resolveStatus!, resolveError!);

        var listing = await _sshService.ListRemoteFilesAsync(server, remotePath, cancellationToken);
        if (!listing.Success)
        {
            var status = listing.ErrorKind switch
            {
                "auth" => "auth_failed",
                "host_key" => "host_key_mismatch",
                "timeout" => "timeout",
                "network" => "connection_error",
                _ => "list_failed"
            };
            return RemoteFileListDto.Fail(status,
                $"列目录失败: {listing.Error}。请确认路径正确且有读权限(路径形如 /var/log), " +
                "或先用 ssh_test_connection 检查连通性。",
                server.Id, server.Name, server.Host);
        }

        return new RemoteFileListDto
        {
            Success = true,
            ServerId = server.Id,
            ServerName = server.Name,
            Host = server.Host,
            Path = remotePath,
            Count = listing.Files.Length,
            Truncated = listing.Truncated,
            Files = listing.Files.Select(f => new RemoteFileDto
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
