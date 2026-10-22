using System.ComponentModel;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

/// <summary>
/// 文件传输旧工具名兼容层（Phase A 过渡）：仅在 <c>tools.legacyAliases=true</c> 时注册。
/// 新客户端应使用 ssh_upload / ssh_download。
/// </summary>
[McpServerToolType]
public sealed class LegacyFileTransferTools
{
    private readonly FileTransferTools _inner;

    public LegacyFileTransferTools(
        IConfigService configService,
        ISshService sshService,
        IApprovalService approvalService,
        IAuditLogService auditLogService,
        ISecurityOptionsProvider securityOptions,
        ScriptRiskAnalyzer scriptRisk)
        => _inner = new FileTransferTools(configService, sshService, approvalService, auditLogService,
            securityOptions, scriptRisk);

    [McpServerTool(Name = "ssh_upload_file", UseStructuredContent = true, OutputSchemaType = typeof(FileTransferResultDto), Destructive = true, OpenWorld = true)]
    [Description("兼容旧名：上传单个文件。建议改用 ssh_upload（支持一个或多个文件/目录）。")]
    public Task<FileTransferResultDto> UploadFile(
        string serverId, string localPath, string remotePath, string? riskNote = null,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken cancellationToken = default)
        => _inner.UploadFile(serverId, localPath, remotePath, riskNote, progress, cancellationToken);

    [McpServerTool(Name = "ssh_upload_files", UseStructuredContent = true, OutputSchemaType = typeof(BatchTransferResultDto), Destructive = true, OpenWorld = true)]
    [Description("兼容旧名：批量上传文件/目录。建议改用 ssh_upload。")]
    public Task<BatchTransferResultDto> UploadFiles(
        string serverId, string localPaths, string remoteDirectory, bool recursive = false,
        int maxFiles = 200, string? riskNote = null, CancellationToken cancellationToken = default)
        => _inner.UploadFiles(serverId, localPaths, remoteDirectory, recursive, maxFiles, riskNote, cancellationToken);

    [McpServerTool(Name = "ssh_download_file", UseStructuredContent = true, OutputSchemaType = typeof(FileTransferResultDto), Destructive = true, OpenWorld = true)]
    [Description("兼容旧名：下载单个文件。建议改用 ssh_download（支持一个或多个文件/目录）。")]
    public Task<FileTransferResultDto> DownloadFile(
        string serverId, string remotePath, string? localPath = null,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken cancellationToken = default)
        => _inner.DownloadFile(serverId, remotePath, localPath, progress, cancellationToken);

    [McpServerTool(Name = "ssh_download_files", UseStructuredContent = true, OutputSchemaType = typeof(BatchTransferResultDto), Destructive = true, OpenWorld = true)]
    [Description("兼容旧名：批量下载文件/目录。建议改用 ssh_download。")]
    public Task<BatchTransferResultDto> DownloadFiles(
        string serverId, string remotePaths, string? localDirectory = null, bool recursive = false,
        int maxFiles = 200, CancellationToken cancellationToken = default)
        => _inner.DownloadFiles(serverId, remotePaths, localDirectory, recursive, maxFiles, cancellationToken);
}
