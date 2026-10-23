// 【同步约定 · 请勿删除】本文件中的工具若发生变动(新增/改名/删除、参数或描述变化), 必须同步更新:
//   ① docs/TOOLS.md —— 工具说明的唯一事实来源(接入说明、意图路由表、参数与返回结构);
//   ② App 端菜单"配置 → MCP工具说明"(McpToolsWindow, 内容由 docs/TOOLS.md 嵌入) + get_usage_guide 内置清单(由注解反射生成, 无需手改);
//   ③ 若新增了工具类, 记得在 Program.cs 注册 WithTools<T>()。
// 只同步其一, AI 客户端拿到的工具说明就会与实际能力不一致。详见 docs/TOOLS.md 顶部"同步约定"。
using System.ComponentModel;
using System.Text;
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
    private readonly ScriptRiskAnalyzer _scriptRisk;

    public FileTransferTools(
        IConfigService configService,
        ISshService sshService,
        IApprovalService approvalService,
        IAuditLogService auditLogService,
        ISecurityOptionsProvider securityOptions,
        ScriptRiskAnalyzer scriptRisk)
    {
        _configService = configService;
        _sshService = sshService;
        _approvalService = approvalService;
        _auditLogService = auditLogService;
        _securityOptions = securityOptions;
        _scriptRisk = scriptRisk;
    }

    [McpServerTool(Name = "ssh_upload", UseStructuredContent = true, OutputSchemaType = typeof(BatchTransferResultDto), Destructive = true, OpenWorld = true)]
    [Description("上传一个或多个文件/目录到 SSH 目标：localPaths 每行一个（目录可递归）；单次操作统一人工审批，返回逐项结果。")]
    public Task<BatchTransferResultDto> Upload(
        [Description("服务器标识（ID/名称/主机名）")] string serverId,
        [Description("本地源路径，每行一个（文件或目录）")] string localPaths,
        [Description("远端目标目录")] string remoteDirectory,
        [Description("是否递归上传目录，默认 false")] bool recursive = false,
        [Description("最多上传文件数（默认 200，1~5000）")] int maxFiles = 200,
        [Description("助手对本操作的摘要（可选，仅供参考）")] string? riskNote = null,
        CancellationToken cancellationToken = default)
        => UploadFiles(serverId, localPaths, remoteDirectory, recursive, maxFiles, riskNote, cancellationToken);

    [McpServerTool(Name = "ssh_download", UseStructuredContent = true, OutputSchemaType = typeof(BatchTransferResultDto), Destructive = true, OpenWorld = true)]
    [Description("从 SSH 目标下载一个或多个文件/目录：remotePaths 每行一个（目录可递归）；单次操作统一人工审批，返回逐项结果。")]
    public Task<BatchTransferResultDto> Download(
        [Description("服务器标识（ID/名称/主机名）")] string serverId,
        [Description("远端源路径，每行一个（文件或目录）")] string remotePaths,
        [Description("本地目标目录（可选；省略=会话工作目录）")] string? localDirectory = null,
        [Description("是否递归下载目录，默认 false")] bool recursive = false,
        [Description("最多下载文件数（默认 200，1~5000）")] int maxFiles = 200,
        CancellationToken cancellationToken = default)
        => DownloadFiles(serverId, remotePaths, localDirectory, recursive, maxFiles, cancellationToken);
    [Description("上传本地文件到SSH服务器。需要人工确认(可返回status=rejected/approval_timeout/approval_unavailable); 路径必须在白名单内, 会话已分配工作目录时源还须在该会话目录内, 越界返回path_not_allowed并在错误里给出指引; 文件过大返回file_too_large; 上传脚本文件(.sh/.py/.ps1等)会先做服务端风险扫描并在审批弹窗展示, 扫描出危险模式直接status=blocked_script")]
    public async Task<FileTransferResultDto> UploadFile(
        [Description("服务器标识: ID/名称/主机名均可, 可用ssh_list_servers列出")] string serverId,
        [Description("本地文件路径(本机Windows路径; 会话已分配工作目录时必须在该会话目录内, 且必须在security.fileTransfer.allowedLocalPaths内)")] string localPath,
        [Description("服务器上的目标文件路径(含文件名, 必须在allowedRemotePaths内), 如 /var/log/app/data.json")] string remotePath,
        [Description("助手对该操作的风险评估摘要(可选), 展示在审批弹窗并标注『助手评估(仅供参考)』; 服务端扫描才是权威")] string? riskNote = null,
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

        // 上传源对称约束(§5.2 需求D6): 会话目录已分配时, 上传源必须位于本会话工作目录内
        var sessionViolation = SessionSourceViolation(localPath);
        if (sessionViolation is not null)
            return FileTransferResultDto.Fail("path_not_allowed",
                sessionViolation, null, SessionWorkspace.Current is { } sd ? new[] { sd } : null, null,
                server.Id, server.Name, server.Host);

        // 会话工作目录条件注入(§5.3.1): 仅当会话目录位于配置允许根之下才进白名单,越界不注入
        var allowedLocal = SessionWorkspace.WithAllowedRoots(ft.AllowedLocalPaths);
        if (!PathPolicy.IsLocalPathAllowed(localPath, allowedLocal))
            return FileTransferResultDto.Fail("path_not_allowed",
                $"本地路径不在允许范围内: {localPath}", null, allowedLocal, null, server.Id, server.Name, server.Host);

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

        // —— 脚本上传安全门: 本地内容服务端规则扫描(权威), 危险模式硬拒, 报告附审批 ——
        string? riskReport = null;
        if (ScriptRiskAnalyzer.IsScriptFile(localPath))
        {
            var report = _scriptRisk.Analyze(Path.GetFileName(localPath), ReadLocalText(localPath));
            if (report.Level == ScriptRiskLevel.Blocked)
            {
                await ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
                {
                    ServerId = server.Id,
                    ServerName = server.Name,
                    Command = $"UPLOAD: {localPath} -> {remotePath}",
                    Status = CommandStatus.Blocked,
                    Category = AuditCategory.Gate,
                    Decision = "blocked",
                    IsFileTransfer = true,
                    FilePath = remotePath,
                    FileSize = fileInfo.Length
                });
                return FileTransferResultDto.Fail("blocked_script",
                    $"脚本内容扫描未通过, 服务端规则发现危险模式(sha256={report.Sha256}):\n" +
                    string.Join("\n", report.Findings.Where(f => f.Level == ScriptRiskLevel.Blocked)
                        .Select(f => $"L{f.Line} [{f.Rule}] {f.Excerpt}")),
                    "script_risk", null, null, server.Id, server.Name, server.Host);
            }
            riskReport = ScriptRiskAnalyzer.FormatReport(report, riskNote);
        }
        else if (!string.IsNullOrWhiteSpace(riskNote))
        {
            riskReport = $"【助手评估(仅供参考, 未经服务端扫描)】{riskNote.Trim()}";
        }

        if (ft.RequireApproval)
        {
            var outcome = await _approvalService.RequestApprovalAsync(
                ToolSupport.ServerLabel(server),
                $"上传 {Path.GetFileName(localPath)} ({FormatFileSize(fileInfo.Length)})",
                CommandFilterResult.Sensitive,
                $"{localPath} -> {remotePath}",
                "文件上传",
                cancellationToken,
                riskReport: riskReport);

            if (outcome != ApprovalOutcome.Approved)
            {
                await ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
                {
                    ServerId = server.Id,
                    ServerName = server.Name,
                    Command = $"UPLOAD: {localPath} -> {remotePath}",
                    Status = CommandStatus.Rejected,
                    Category = AuditCategory.Gate,
                    Decision = ToolSupport.DecisionFor(outcome, _securityOptions.Approval.Mode),
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
            Category = AuditCategory.Transfer,
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
    [Description("批量/目录上传: 用**一条 SFTP 连接**上传多个本地文件或整个本地目录(可递归, 保持子目录结构)到服务器目录, 避免每个文件重复建连。localPaths 每行一个(文件或目录); **整批一次人工确认**; 受 allowedLocalPaths/allowedRemotePaths 白名单(会话已分配工作目录时源还须在该会话目录内)、单文件大小与 maxFiles 上限约束; 批内脚本文件(.sh/.py/.ps1等)会做服务端风险扫描并在审批弹窗展示, 任一脚本命中危险模式整批 status=blocked_script")]
    public async Task<BatchTransferResultDto> UploadFiles(
        [Description("服务器标识: ID/名称/主机名, 可用ssh_list_servers列出")] string serverId,
        [Description("本地源路径, 每行一个(文件或目录); 目录会被展开; 会话已分配工作目录时源必须在该会话目录内")] string localPaths,
        [Description("服务器上的目标目录(必须在security.fileTransfer.allowedRemotePaths内); 目录内文件按相对路径还原")] string remoteDirectory,
        [Description("是否递归上传子目录, 默认 false")] bool recursive = false,
        [Description("最多上传文件数(默认 200, 1~5000)")] int maxFiles = 200,
        [Description("助手对该操作的风险评估摘要(可选), 展示在审批弹窗并标注『助手评估(仅供参考)』; 服务端扫描才是权威")] string? riskNote = null,
        CancellationToken cancellationToken = default)
    {
        var config = await _configService.LoadConfigAsync();
        var (server, resolveStatus, resolveError) = ToolSupport.ResolveServer(config, serverId);
        if (server == null)
            return BatchTransferResultDto.Fail(resolveStatus!, resolveError!, "server_not_found");

        var ft = config.Security.FileTransfer;
        if (!ft.Enabled)
            return BatchTransferResultDto.Fail("file_transfer_disabled",
                "文件传输功能已被禁用(security.fileTransfer.enabled=false)。", server.Id, server.Name, server.Host);

        var paths = SplitPaths(localPaths);
        if (paths.Count == 0)
            return BatchTransferResultDto.Fail("bad_request", "localPaths 不能为空(每行一个文件或目录)", server.Id, server.Name, server.Host);

        foreach (var path in paths)
        {
            // 上传源对称约束(§5.2 需求D6): 会话目录已分配时, 上传源必须位于本会话工作目录内
            var sessionViolation = SessionSourceViolation(path);
            if (sessionViolation is not null)
                return BatchTransferResultDto.Fail("path_not_allowed", sessionViolation, server.Id, server.Name, server.Host);
            if (!PathPolicy.IsLocalPathAllowed(path, SessionWorkspace.WithAllowedRoots(ft.AllowedLocalPaths)))
                return BatchTransferResultDto.Fail("path_not_allowed",
                    $"本地路径不在允许范围内: {path}", server.Id, server.Name, server.Host);
        }

        if (!PathPolicy.IsRemotePathAllowed(remoteDirectory, ft.AllowedRemotePaths))
            return BatchTransferResultDto.Fail("path_not_allowed",
                $"远程目录不在允许范围内: {remoteDirectory}", server.Id, server.Name, server.Host);

        // —— 脚本上传安全门(批量): 聚合扫描批内全部脚本, 任一脚本危险模式 → 整批硬拒 ——
        string? riskReport = null;
        var scriptFiles = CollectScriptFiles(paths, recursive);
        if (scriptFiles.Count > 0)
        {
            var blockedParts = new List<string>();
            var reportParts = new List<string>();
            foreach (var (scriptPath, report) in ScanScripts(scriptFiles))
            {
                if (report.Level == ScriptRiskLevel.Blocked)
                {
                    blockedParts.Add($"【{report.FileName} sha256={report.Sha256}】\n" +
                        string.Join("\n", report.Findings.Where(f => f.Level == ScriptRiskLevel.Blocked)
                            .Select(f => $"  L{f.Line} [{f.Rule}] {f.Excerpt}")));
                }
                else
                {
                    reportParts.Add(ScriptRiskAnalyzer.FormatReport(report));
                }
            }

            if (blockedParts.Count > 0)
            {
                await ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
                {
                    ServerId = server.Id,
                    ServerName = server.Name,
                    Command = $"UPLOAD_BATCH: {paths.Count} path(s) -> {remoteDirectory}",
                    Status = CommandStatus.Blocked,
                    Category = AuditCategory.Gate,
                    Decision = "blocked",
                    IsFileTransfer = true,
                    FilePath = remoteDirectory
                });
                return BatchTransferResultDto.Fail("blocked_script",
                    $"批内 {blockedParts.Count} 个脚本内容扫描未通过(整批拒绝):\n" + string.Join("\n", blockedParts),
                    server.Id, server.Name, server.Host);
            }

            riskReport = $"【脚本风险分析(服务端规则扫描), 共 {scriptFiles.Count} 个脚本】\n" +
                string.Join("\n", reportParts.Take(10)) +
                (reportParts.Count > 10 ? $"\n… 另有 {reportParts.Count - 10} 个脚本未展示" : "") +
                (string.IsNullOrWhiteSpace(riskNote) ? "" : $"\n助手评估(仅供参考, 非权威): {riskNote.Trim()}");
        }
        else if (!string.IsNullOrWhiteSpace(riskNote))
        {
            riskReport = $"【助手评估(仅供参考, 未经服务端扫描)】{riskNote.Trim()}";
        }

        if (ft.RequireApproval)
        {
            var outcome = await _approvalService.RequestApprovalAsync(
                ToolSupport.ServerLabel(server),
                $"批量上传 {paths.Count} 个源路径 -> {remoteDirectory}{(recursive ? " (递归)" : "")}",
                CommandFilterResult.Sensitive,
                string.Join("; ", paths.Take(5)) + (paths.Count > 5 ? " ..." : ""),
                "批量文件上传",
                cancellationToken,
                riskReport: riskReport);

            if (outcome != ApprovalOutcome.Approved)
            {
                await ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
                {
                    ServerId = server.Id,
                    ServerName = server.Name,
                    Command = $"UPLOAD_BATCH: {paths.Count} path(s) -> {remoteDirectory}",
                    Status = CommandStatus.Rejected,
                    Category = AuditCategory.Gate,
                    Decision = ToolSupport.DecisionFor(outcome, _securityOptions.Approval.Mode),
                    IsFileTransfer = true,
                    FilePath = remoteDirectory
                });
                var (status, error) = ApprovalOutcomeText.Describe(outcome, _securityOptions.Approval.TimeoutSeconds);
                return BatchTransferResultDto.Fail(status, error, server.Id, server.Name, server.Host);
            }
        }

        var result = await _sshService.UploadBatchAsync(server, paths, remoteDirectory, recursive,
            Math.Clamp(maxFiles, 1, 5000), ft.MaxFileSizeBytes, 0, ct: cancellationToken);

        await ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Command = $"UPLOAD_BATCH: {paths.Count} path(s) -> {remoteDirectory}",
            Result = result.Success ? $"{result.Succeeded}/{result.Total} 文件, {result.TotalBytes} bytes" : result.Error,
            Status = result.Success ? CommandStatus.Executed : CommandStatus.Failed,
            Category = AuditCategory.Transfer,
            IsFileTransfer = true,
            FilePath = remoteDirectory,
            FileSize = result.TotalBytes
        });

        if (!result.Success && result.Total == 0)
        {
            var status = result.ErrorKind switch
            {
                "auth" => "auth_failed",
                "host_key" => "host_key_mismatch",
                "timeout" => "timeout",
                "network" => "connection_error",
                "rate_limited" => "rate_limited",
                "no_files" => "no_files",
                _ => "transfer_error"
            };
            return BatchTransferResultDto.Fail(status, result.Error ?? "批量上传失败", server.Id, server.Name, server.Host);
        }

        return new BatchTransferResultDto
        {
            Success = result.Success,
            Error = result.Success ? null : result.Error,
            ServerId = server.Id,
            ServerName = server.Name,
            Host = server.Host,
            RemoteDirectory = remoteDirectory,
            Total = result.Total,
            Succeeded = result.Succeeded,
            Failed = result.Failed,
            Truncated = result.Truncated,
            TotalBytes = result.TotalBytes,
            DurationMs = result.Duration.TotalMilliseconds,
            Files = result.Files.Select(f => new BatchFileDto
            {
                RemotePath = f.RemotePath,
                LocalPath = f.LocalPath,
                Success = f.Success,
                Size = FormatFileSize(f.Bytes),
                Error = f.Error
            }).ToList()
        };
    }
    [Description("从SSH服务器下载文件到本地。需要人工确认(可返回status=rejected/approval_timeout/approval_unavailable); 路径必须在白名单内, 越界返回path_not_allowed并在错误里给出允许的路径")]
    public async Task<FileTransferResultDto> DownloadFile(
        [Description("服务器标识: ID/名称/主机名均可, 可用ssh_list_servers列出")] string serverId,
        [Description("服务器上的源文件路径(必须在security.fileTransfer.allowedRemotePaths内), 如 /var/log/app/app.log")] string remotePath,
        [Description("本地保存路径(本机Windows路径, 含文件名, 必须在allowedLocalPaths内)。省略=自动落会话工作目录并沿用远端文件名; 相对路径=会话工作目录之下(工具级默认落地, 评估报告§5.2); 绝对路径原样校验")] string? localPath = null,
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

        // 工具级默认落地(§5.2): 省略/相对路径 → 会话工作目录(宿主 spawn 注入, 非提示词约定)
        localPath = SessionWorkspace.ResolveLanding(localPath, remotePath, isDirectory: false);
        if (localPath is null)
            return FileTransferResultDto.Fail("bad_request",
                "未提供 localPath, 且当前进程未注入会话工作目录(旧会话/直连 MCP): 请显式提供绝对本地路径。",
                null, ft.AllowedLocalPaths, null, server.Id, server.Name, server.Host);

        // 会话工作目录条件注入(§5.3.1): 仅当会话目录位于配置允许根之下才进白名单,越界不注入
        var allowedLocal = SessionWorkspace.WithAllowedRoots(ft.AllowedLocalPaths);
        if (!PathPolicy.IsLocalPathAllowed(localPath, allowedLocal))
            return FileTransferResultDto.Fail("path_not_allowed",
                $"本地路径不在允许范围内: {localPath}", null, allowedLocal, null, server.Id, server.Name, server.Host);

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
                    Category = AuditCategory.Gate,
                    Decision = ToolSupport.DecisionFor(outcome, _securityOptions.Approval.Mode),
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
            Category = AuditCategory.Transfer,
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
    [Description("批量/目录下载: 用**一条 SFTP 连接**下载多个文件或整个目录(可递归)到本地目录, 避免每个文件重复建连。remotePaths 每行一个(文件或目录); 目录会展开(recursive=true 递归子目录, 软链接跳过); **整批一次人工确认**; 受 allowedRemotePaths/allowedLocalPaths 白名单与 maxFiles 上限约束")]
    public async Task<BatchTransferResultDto> DownloadFiles(
        [Description("服务器标识: ID/名称/主机名, 可用ssh_list_servers列出")] string serverId,
        [Description("服务器上的源路径, 每行一个(文件或目录); 目录会被展开")] string remotePaths,
        [Description("本地目标目录(必须在security.fileTransfer.allowedLocalPaths内); 目录内文件按相对路径还原。省略=会话工作目录; 相对路径=会话工作目录之下(工具级默认落地, 评估报告§5.2)")] string? localDirectory = null,
        [Description("是否递归下载子目录, 默认 false")] bool recursive = false,
        [Description("最多下载文件数(默认 200, 1~5000)")] int maxFiles = 200,
        CancellationToken cancellationToken = default)
    {
        var config = await _configService.LoadConfigAsync();
        var (server, resolveStatus, resolveError) = ToolSupport.ResolveServer(config, serverId);
        if (server == null)
            return BatchTransferResultDto.Fail(resolveStatus!, resolveError!, "server_not_found");

        var ft = config.Security.FileTransfer;
        if (!ft.Enabled)
            return BatchTransferResultDto.Fail("file_transfer_disabled",
                "文件传输功能已被禁用(security.fileTransfer.enabled=false)。", server.Id, server.Name, server.Host);

        var paths = SplitPaths(remotePaths);
        if (paths.Count == 0)
            return BatchTransferResultDto.Fail("bad_request", "remotePaths 不能为空(每行一个文件或目录)", server.Id, server.Name, server.Host);

        foreach (var path in paths)
            if (!PathPolicy.IsRemotePathAllowed(path, ft.AllowedRemotePaths))
                return BatchTransferResultDto.Fail("path_not_allowed",
                    $"远程路径不在允许范围内: {path}", server.Id, server.Name, server.Host);

        // 工具级默认落地(§5.2): 省略/相对路径 → 会话工作目录
        localDirectory = SessionWorkspace.ResolveLanding(localDirectory, remotePaths, isDirectory: true);
        if (localDirectory is null)
            return BatchTransferResultDto.Fail("bad_request",
                "未提供 localDirectory, 且当前进程未注入会话工作目录(旧会话/直连 MCP): 请显式提供绝对本地目录。",
                server.Id, server.Name, server.Host);

        // 会话工作目录条件注入(§5.3.1)
        var allowedLocal = SessionWorkspace.WithAllowedRoots(ft.AllowedLocalPaths);
        if (!PathPolicy.IsLocalPathAllowed(localDirectory, allowedLocal))
            return BatchTransferResultDto.Fail("path_not_allowed",
                $"本地目录不在允许范围内: {localDirectory}", server.Id, server.Name, server.Host);

        if (ft.RequireApproval)
        {
            var outcome = await _approvalService.RequestApprovalAsync(
                ToolSupport.ServerLabel(server),
                $"批量下载 {paths.Count} 个源路径 -> {localDirectory}{(recursive ? " (递归)" : "")}",
                CommandFilterResult.Sensitive,
                string.Join("; ", paths.Take(5)) + (paths.Count > 5 ? " ..." : ""),
                "批量文件下载",
                cancellationToken);

            if (outcome != ApprovalOutcome.Approved)
            {
                await ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
                {
                    ServerId = server.Id,
                    ServerName = server.Name,
                    Command = $"DOWNLOAD_BATCH: {paths.Count} path(s) -> {localDirectory}",
                    Status = CommandStatus.Rejected,
                    Category = AuditCategory.Gate,
                    Decision = ToolSupport.DecisionFor(outcome, _securityOptions.Approval.Mode),
                    IsFileTransfer = true,
                    FilePath = localDirectory
                });
                var (status, error) = ApprovalOutcomeText.Describe(outcome, _securityOptions.Approval.TimeoutSeconds);
                return BatchTransferResultDto.Fail(status, error, server.Id, server.Name, server.Host);
            }
        }

        var result = await _sshService.DownloadBatchAsync(server, paths, localDirectory, recursive, Math.Clamp(maxFiles, 1, 5000), 0, ct: cancellationToken);

        await ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Command = $"DOWNLOAD_BATCH: {paths.Count} path(s) -> {localDirectory}",
            Result = result.Success ? $"{result.Succeeded}/{result.Total} 文件, {result.TotalBytes} bytes" : result.Error,
            Status = result.Success ? CommandStatus.Executed : CommandStatus.Failed,
            Category = AuditCategory.Transfer,
            IsFileTransfer = true,
            FilePath = localDirectory,
            FileSize = result.TotalBytes
        });

        if (!result.Success && result.Total == 0)
        {
            var status = result.ErrorKind switch
            {
                "auth" => "auth_failed",
                "host_key" => "host_key_mismatch",
                "timeout" => "timeout",
                "network" => "connection_error",
                "rate_limited" => "rate_limited",
                "no_files" => "no_files",
                _ => "transfer_error"
            };
            return BatchTransferResultDto.Fail(status, result.Error ?? "批量下载失败", server.Id, server.Name, server.Host);
        }

        return new BatchTransferResultDto
        {
            Success = result.Success,
            Error = result.Success ? null : result.Error,
            ServerId = server.Id,
            ServerName = server.Name,
            Host = server.Host,
            LocalDirectory = localDirectory,
            Total = result.Total,
            Succeeded = result.Succeeded,
            Failed = result.Failed,
            Truncated = result.Truncated,
            TotalBytes = result.TotalBytes,
            DurationMs = result.Duration.TotalMilliseconds,
            Files = result.Files.Select(f => new BatchFileDto
            {
                RemotePath = f.RemotePath,
                LocalPath = f.LocalPath,
                Success = f.Success,
                Size = FormatFileSize(f.Bytes),
                Error = f.Error
            }).ToList()
        };
    }

    private static List<string> SplitPaths(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? new List<string>()
            : text.Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// 上传源路径对称约束（评估报告 §5.2 需求 D6 / §10.C.5）：会话目录已分配时上传源必须在本会话工作目录内
    /// （与"下载落点限会话目录"对称, 附带 PathGuard 物理解析防 symlink 逃逸）；未分配(共享区模式)维持现状仅白名单。
    /// </summary>
    private static string? SessionSourceViolation(string path)
    {
        var sessionDir = SessionWorkspace.Current;
        if (string.IsNullOrWhiteSpace(sessionDir))
            return null;
        if (PathGuard.IsUnder(sessionDir, path))
            return null;
        return $"上传源路径限定在本会话工作目录内(对称约束): {path}\n" +
               "请改用会话目录内的文件; 共享工作区文件可先经 ops_doc_read/ops_doc_write 复制到会话目录后再上传";
    }

    /// <summary>读取本地文件文本供风险扫描(最多读到扫描上限+1字符, 读失败返回 null → 按未审脚本处理)。</summary>
    private static string? ReadLocalText(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[ScriptRiskAnalyzer.MaxAnalyzeChars + 1];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = sr.Read(buffer, read, buffer.Length - read);
                if (n <= 0)
                    break;
                read += n;
            }
            return new string(buffer, 0, read);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>收集批量上传路径中的脚本文件(文件按扩展名直接判; 目录按递归标志展开, 上限 500 个)。</summary>
    private List<string> CollectScriptFiles(IReadOnlyList<string> paths, bool recursive)
    {
        const int maxScripts = 500;
        var result = new List<string>();
        foreach (var p in paths)
        {
            if (File.Exists(p))
            {
                if (ScriptRiskAnalyzer.IsScriptFile(p))
                    result.Add(p);
            }
            else if (Directory.Exists(p))
            {
                try
                {
                    var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    result.AddRange(Directory.EnumerateFiles(p, "*", option)
                        .Where(ScriptRiskAnalyzer.IsScriptFile)
                        .Take(maxScripts));
                }
                catch
                {
                    // 目录不可枚举(权限/中断): 交给后续上传流程报错
                }
            }
            if (result.Count >= maxScripts)
                break;
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private IEnumerable<(string Path, ScriptRiskReport Report)> ScanScripts(IEnumerable<string> scriptFiles) =>
        scriptFiles.Select(p => (p, _scriptRisk.Analyze(Path.GetFileName(p), ReadLocalText(p))));

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

        await ToolSupport.SafeLogCommandAsync(_auditLogService, new CommandAuditLog
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Command = $"LIST: {remotePath}",
            Result = listing.Success ? $"{listing.Files.Length} 项" : listing.Error,
            Status = listing.Success ? CommandStatus.Executed : CommandStatus.Failed,
            Category = AuditCategory.Probe
        });

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
