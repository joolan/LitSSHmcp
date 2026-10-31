using System.IO;
using System.Linq;
using LitSSHmcp.Agent;
using LitSSHmcp.App.Services.LocalTerminal;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.App.Services;

/// <summary>
/// 提升命令（评估报告需求 D / §10.D.1）：会话内一条指令把指定文件从**本会话工作目录**归档到共享工作区。
/// 安全策略（待办 #9）：**脚本类产物提升须桌面确认**（命中 ScriptRiskAnalyzer 扩展名 → 复用本机终端审批弹窗，
/// 弹窗文案带来源会话标识）；非脚本类直接提升但记审计（LocalAudit, 谁从哪个会话提升了什么, 与 §10.A.1 呼应）。
/// 路径约束：源必须在会话目录内（PathGuard 物理判定）；目标在共享区且禁止落入 jobs/（不得反向写会话树）；
/// 目标已存在时拒绝（防静默覆盖共享脚本 = 投毒路径）。
/// </summary>
public sealed class PromoteTool : IAgentTool
{
    private readonly Func<string?> _sessionDir;
    private readonly Func<string> _sharedRoot;
    private readonly LocalApprovalHandler _approval;
    private readonly Func<string> _sessionKey;
    private readonly AITool _schema;

    public const string ToolName = "ops_promote";

    public PromoteTool(
        Func<string?> sessionDir,
        Func<string> sharedRoot,
        LocalApprovalHandler approval,
        Func<string> sessionKey)
    {
        _sessionDir = sessionDir;
        _sharedRoot = sharedRoot;
        _approval = approval;
        _sessionKey = sessionKey;
        _schema = AIFunctionFactory.Create(
            (string path, string? to) => (path, to),
            ToolName,
            Description);
    }

    public string Name => ToolName;

    public bool Destructive => true;

    public bool ReadOnly => false;

    public string? ToolGroup => "local";

    public bool IsLocal => true;

    public AITool Tool => _schema;

    private const string Description =
        "提升(归档)文件：把本会话工作目录中的产出文件提升到共享工作区(跨会话共享)。" +
        "path=会话目录内的相对路径；to=共享区内目标相对路径(可选, 默认保持原相对路径)。" +
        "脚本类文件(.sh/.py/.ps1等)提升会弹桌面确认框(须用户人工允许)；非脚本类直接提升。" +
        "目标已存在会拒绝(不覆盖)；会话目录外/共享区内的文件不可提升。";

    public async Task<object?> InvokeAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
    {
        var path = arguments is not null && arguments.TryGetValue("path", out var p) ? p?.ToString()?.Trim() : null;
        var to = arguments is not null && arguments.TryGetValue("to", out var t) ? t?.ToString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(path))
            return "错误: path 为必填(会话目录内的相对路径)";

        var sessionDir = _sessionDir();
        if (string.IsNullOrWhiteSpace(sessionDir))
            return "错误: 当前会话未分配独立工作目录(共享工作区模式), 无须提升——产出本就在共享区。";

        if (path.StartsWith(WorkspaceTools.SharedPrefix, StringComparison.OrdinalIgnoreCase))
            return "错误: 提升方向为 会话目录 → 共享区, 源不能带 shared/ 前缀。";

        string source;
        try
        {
            source = WorkspaceTools.ResolvePath(sessionDir, path);
        }
        catch (InvalidOperationException ex)
        {
            return $"错误: {ex.Message}";
        }
        if (!File.Exists(source))
            return $"错误: 源文件不存在 {path}";

        var sharedRoot = Path.GetFullPath(_sharedRoot());
        var jobsRoot = Path.Combine(sharedRoot, WorkspaceLayout.JobsDirName);
        string target;
        try
        {
            var relTarget = string.IsNullOrWhiteSpace(to)
                ? Path.GetRelativePath(sessionDir, source).Replace('\\', '/')
                : to!.Replace('\\', '/').TrimStart('/');
            if (relTarget.Contains("..", StringComparison.Ordinal))
                return "错误: 非法路径(禁止 .. 越界)";
            if (relTarget.StartsWith(WorkspaceLayout.JobsDirName + "/", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(relTarget, WorkspaceLayout.JobsDirName, StringComparison.OrdinalIgnoreCase))
                return "错误: 目标禁止落在 jobs/ 会话树内(只能提升到共享资产/脚本区)";
            target = Path.GetFullPath(Path.Combine(sharedRoot, relTarget));
            if (!PathGuard.IsUnder(sharedRoot, target) || !PathGuard.IsUnder(jobsRoot, source))
                return "错误: 路径越界(源须在本会话工作目录内, 目标须在共享工作区且不在 jobs/ 下)";
            if (string.Equals(PathGuard.TryCanonicalize(target)?.TrimEnd(Path.DirectorySeparatorChar),
                    PathGuard.TryCanonicalize(sharedRoot)?.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                return "错误: 目标不能是共享区根自身";
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            return $"错误: {ex.Message}";
        }

        if (File.Exists(target))
            return $"错误: 共享区已存在同名文件 {relTargetOf(sharedRoot, target)}, 不覆盖。请先改名(to=新路径)或人工处理。";

        // —— 脚本类提升须桌面确认（§10.D.1 待办 #9：投毒路径防护；弹窗带来源会话）——
        var isScript = ScriptRiskAnalyzer.IsScriptFile(target);
        var decisionText =
            $"提升脚本到共享区(需确认): {relTargetOf(sharedRoot, target)} ← 会话目录 {path}\n" +
            $"来源会话: {_sessionKey()} · 提升后所有会话可能将其作为可复用脚本信任执行";
        if (isScript)
        {
            var decision = await _approval(decisionText, sessionDir, ct);
            if (decision == LocalApprovalDecision.Deny)
            {
                await AuditAsync(source, target, sessionDir, sharedRoot,
                    CommandStatus.Rejected, "manual-rejected", "用户拒绝脚本提升");
                return "用户拒绝了脚本提升。";
            }
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target, overwrite: false);
        }
        catch (IOException)
        {
            return $"错误: 目标已存在或移动失败 {relTargetOf(sharedRoot, target)} (不覆盖)";
        }
        catch (Exception ex)
        {
            return $"错误: 提升失败 {ex.Message}";
        }

        await AuditAsync(source, target, sessionDir, sharedRoot,
            CommandStatus.Executed,
            isScript ? "manual-approved" : null,
            isScript ? "脚本提升经桌面确认" : "非脚本直接提升");
        return $"已提升 {path} → {WorkspaceTools.SharedPrefix}{relTargetOf(sharedRoot, target)}" +
               (isScript ? " (脚本类, 已经桌面确认)" : string.Empty);

        static string relTargetOf(string root, string full) =>
            Path.GetRelativePath(root, full).Replace('\\', '/');
    }

    /// <summary>提升事件入审计链（§10.A.1：谁把什么文件提升进共享区, 对应 §10.D.1 投毒可追溯）。</summary>
    private static Task AuditAsync(string source, string target, string sessionDir, string sharedRoot,
        CommandStatus status, string? decision, string result)
        => LocalAudit.LogAsync(ToolName,
            $"PROMOTE: {Path.GetRelativePath(sessionDir, source).Replace('\\', '/')} -> " +
            $"{WorkspaceTools.SharedPrefix}{Path.GetRelativePath(sharedRoot, target).Replace('\\', '/')}",
            status,
            result: result,
            filePath: target,
            decision: decision);
}
