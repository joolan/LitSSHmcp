namespace LitSSHmcp.Core.Services.Security;

/// <summary>
/// 会话工作目录上下文（评估报告需求 D / §5.2 工具级默认落地、§5.3.1 条件注入）。
/// 目录身份只能由宿主 spawn 子进程时经环境变量 LITSSH_SESSION_WORKSPACE_DIR 注入
/// （与 LITSSH_OWNER_USER_ID 同信任模型），子进程不自行决定、不接受工具/前端参数（防伪造）。
/// 未注入 = 旧会话/直连 MCP 客户端（共享工作区，现状行为）。
/// </summary>
public static class SessionWorkspace
{
    /// <summary>本进程（MCP 子进程）注入的会话工作目录绝对路径；null=未注入。</summary>
    public static string? Current { get; set; }

    /// <summary>设置注入目录（非法/空视为未注入；统一取绝对路径）。</summary>
    public static void SetCurrent(string? dir)
        => Current = string.IsNullOrWhiteSpace(dir) ? null : Path.GetFullPath(dir.Trim());

    /// <summary>会话目录条件注入后的 fileTransfer 本地允许列表（§5.3.1）。</summary>
    public static string[] WithAllowedRoots(string[]? allowedRoots)
        => PathPolicy.WithSessionDir(allowedRoots, Current);

    /// <summary>工具级默认落地路径解析（§5.2，不能只靠提示词）：
    /// 省略 → 会话目录（文件下载=会话目录+远端文件名）；相对路径 → 会话目录之下；绝对路径 → 原样（照旧过白名单）。
    /// 未注入会话目录时：省略 → null（须显式给绝对路径），相对路径 → 原样（维持现状 CWD 语义）。
    /// </summary>
    public static string? ResolveLanding(string? localPath, string remotePath, bool isDirectory)
        => ResolveLanding(localPath, remotePath, isDirectory, Current);

    /// <summary>测试可注入 sessionDir 的落地解析（与 <see cref="Current"/> 语义一致）。</summary>
    public static string? ResolveLanding(string? localPath, string remotePath, bool isDirectory, string? sessionDir)
    {
        if (string.IsNullOrWhiteSpace(localPath))
        {
            if (string.IsNullOrWhiteSpace(sessionDir))
                return null;
            return isDirectory
                ? sessionDir
                : Path.Combine(sessionDir, Path.GetFileName(remotePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        if (Path.IsPathRooted(localPath))
            return localPath;

        // GetFullPath 归一分隔符并规范化 ..（逃逸仍由白名单校验兜底）
        return string.IsNullOrWhiteSpace(sessionDir)
            ? localPath
            : Path.GetFullPath(Path.Combine(sessionDir, localPath));
    }
}
