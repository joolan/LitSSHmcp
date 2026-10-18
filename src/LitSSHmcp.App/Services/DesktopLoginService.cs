using System.IO;
using System.Text.Json;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Web;

namespace LitSSHmcp.App.Services;

/// <summary>桌面登录态（仅归因用）。</summary>
public sealed record DesktopLoginState(long UserId, string Username);

/// <summary>
/// 桌面可选登录（评估报告 D1 收尾）：复用 Web 账号库 <c>web.db</c> 校验（与 Web 共用失败计数/锁定曲线，
/// 防止桌面绕过登录限速），登录态经 DPAPI 加密落盘 <c>%APPDATA%\LitSSH\desktop-login.dat</c>（跨重启保持）。
/// 登录只影响**归因**——新会话 owner、审计 UserId 记为登录用户；桌面不做任何权限限制（D1「仅归因不限权」）。
/// </summary>
public sealed class DesktopLoginService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _dbPath;
    private readonly string _statePath;
    private readonly ISecretProtector _protector;
    private readonly WebAccountsStore _accounts;
    private readonly WebAuthService _auth;

    public DesktopLoginService(string? dbPath = null, string? statePath = null, ISecretProtector? protector = null)
    {
        _dbPath = dbPath ?? ConfigPaths.WebDb;
        _statePath = statePath ?? Path.Combine(ConfigPaths.AppDataDir, "desktop-login.dat");
        _protector = protector ?? new DpapiSecretProtector();
        _accounts = new WebAccountsStore(_dbPath);
        _auth = new WebAuthService(_accounts);
    }

    /// <summary>当前登录态（null=未登录，归因 local）。</summary>
    public DesktopLoginState? Current { get; private set; }

    /// <summary>
    /// 把当前登录态同步到进程审计归因（<see cref="AuditContext.CurrentUserId"/>）。
    /// 必须由调用方在**同步上下文**里调用：AsyncLocal 的写入不会跨 <c>await</c> 回传到调用方
    /// （服务内部的登录/恢复流程在 await 之后设置，调用方看不到）。
    /// </summary>
    public void ApplyAttribution() => AuditContext.CurrentUserId = Current?.Username;

    /// <summary>
    /// 启动时恢复登录态：文件缺失/解密失败/账号已删除 → 视为未登录并清理残留文件（幂等）。
    /// </summary>
    public async Task<DesktopLoginState?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_statePath) || !File.Exists(_dbPath))
        {
            Apply(null);
            return null;
        }

        try
        {
            var plain = _protector.Unprotect(File.ReadAllText(_statePath));
            var state = string.IsNullOrWhiteSpace(plain)
                ? null
                : JsonSerializer.Deserialize<DesktopLoginState>(plain, JsonOptions);
            if (state is null)
            {
                ClearFile();
                Apply(null);
                return null;
            }

            await _accounts.InitializeAsync(ct);
            var user = await _accounts.FindByIdAsync(state.UserId, ct);
            if (user is null || !string.Equals(user.Username, state.Username, StringComparison.Ordinal))
            {
                ClearFile();
                Apply(null);
                return null;
            }

            Apply(new DesktopLoginState(user.Id, user.Username));
            return Current;
        }
        catch
        {
            ClearFile();
            Apply(null);
            return null;
        }
    }

    /// <summary>
    /// 登录：web.db 校验（失败计数/锁定与 Web 共享）；成功后立即吊销本次临时令牌
    /// （桌面不需要 Web 会话令牌，避免 web.db 令牌表堆积），并持久化登录态。
    /// </summary>
    public async Task<(bool Ok, string? Error)> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        if (!File.Exists(_dbPath))
            return (false, "尚未创建 Web 账号（先在命令行执行 --create-user 创建账号后重试）");

        try
        {
            await _accounts.InitializeAsync(ct);
            var outcome = await _auth.LoginAsync((username ?? string.Empty).Trim(), password ?? string.Empty, ct);
            if (outcome.Status != WebLoginStatus.Success || outcome.Token is null || outcome.User is null)
            {
                return (false, outcome.Status == WebLoginStatus.Locked
                    ? $"账号已锁定，请约 {(int)Math.Ceiling((outcome.RetryAfter ?? TimeSpan.Zero).TotalMinutes):0} 分钟后再试"
                    : "用户名或密码错误");
            }

            try { await _auth.LogoutAsync(outcome.Token, ct); } catch { /* 吊销失败不阻断登录态 */ }

            var state = new DesktopLoginState(outcome.User.Id, outcome.User.Username);
            Persist(state);
            Apply(state);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, "登录失败：" + ex.Message);
        }
    }

    /// <summary>注销：清除持久化文件与进程内归因（新会话恢复 local）。</summary>
    public void Logout()
    {
        ClearFile();
        Apply(null);
    }

    private void Persist(DesktopLoginState state)
    {
        var directory = Path.GetDirectoryName(_statePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(_statePath, _protector.Protect(json) ?? string.Empty);
    }

    private void ClearFile()
    {
        try
        {
            if (File.Exists(_statePath))
                File.Delete(_statePath);
        }
        catch
        {
            // 删除失败忽略（下次 Load 会重新校验账号有效性）
        }
    }

    /// <summary>同步进程内归因：仅更新服务状态；审计上下文由调用方经 <see cref="ApplyAttribution"/> 同步写入。</summary>
    private void Apply(DesktopLoginState? state) => Current = state;
}
