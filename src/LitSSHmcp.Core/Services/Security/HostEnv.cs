namespace LitSSHmcp.Core.Services.Security;

/// <summary>
/// 宿主 spawn MCP 子进程时注入的环境变量（评估报告 §10.A.5 / D1）：身份与授权只能由宿主注入，
/// 子进程只读取，<b>绝不接受工具参数或前端传入</b>（防伪造）。stdio 子进程经
/// InheritEnvironmentVariables 继承宿主进程级变量，会话级变量由 spawn 时的显式字典追加。
/// </summary>
public static class HostEnv
{
    /// <summary>操作归因用户 Id；缺省按 "local" 归因（桌面未登录会话）。</summary>
    public const string OwnerUserId = "LITSSH_OWNER_USER_ID";

    /// <summary>会话工作目录；缺省 = 共享工作区（旧行为，D6）。</summary>
    public const string SessionWorkspaceDir = "LITSSH_SESSION_WORKSPACE_DIR";

    /// <summary>
    /// 授权矩阵执行模式（M3-4 / §10.B.4）：<c>enforce</c> = 按"用户→目标"授权矩阵强制校验。
    /// 仅 Web 宿主注入（Web 会话强制）；桌面进程不注入 = 桌面仅归因不限权（D1）。
    /// </summary>
    public const string AuthzMode = "LITSSH_AUTHZ_MODE";

    /// <summary>
    /// 授权矩阵匹配用的用户名，与 <see cref="OwnerUserId"/> 同批由宿主注入（宿主持有账号库）；
    /// 缺省 = 矩阵仅按用户 Id 匹配。与 OwnerUserId 同信任模型，先到先配。
    /// </summary>
    public const string AuthzUser = "LITSSH_AUTHZ_USER";

    /// <summary><see cref="AuthzMode"/> 的强制值（Web 宿主启动时设置）。</summary>
    public const string Enforce = "enforce";
}
