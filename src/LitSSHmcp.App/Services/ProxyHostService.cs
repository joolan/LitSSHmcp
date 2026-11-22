using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Proxy;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services;

/// <summary>
/// 代理配置与进程级宿主：App 启动时按配置自动启动，退出时统一停止。
/// 工具箱窗口与管理流程都通过该单例读写，避免多个窗口各自监听同一端口。
/// </summary>
public sealed class ProxyHostService : IDisposable
{
    private readonly ProxyStore _store;
    private readonly ProxyServerHost _host;
    private readonly ISecretProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _errorGate = new();
    private string? _lastError;

    public ProxyHostService(string? storePath = null, ProxyServerHost? host = null,
        ISecretProtector? protector = null)
    {
        _store = new ProxyStore(storePath);
        _host = host ?? new ProxyServerHost();
        _protector = protector ?? new DpapiSecretProtector();
    }

    public string ConfigPath => _store.Path;
    public ProxyRequestLog Log => _host.Log;
    public ProxyStatus Status => _host.Status;
    public IReadOnlyList<ProxyAccount> RunningAccounts => _host.Accounts;
    public IReadOnlyList<ProxyConnectionSnapshot> ActiveConnections => _host.ActiveConnections;

    public string? LastError
    {
        get { lock (_errorGate) return _lastError; }
    }

    public event EventHandler? StateChanged
    {
        add => _host.StateChanged += value;
        remove => _host.StateChanged -= value;
    }

    public async Task<ProxyDocument> LoadAsync(CancellationToken ct = default)
    {
        var document = await _store.LoadAsync(ct).ConfigureAwait(false);
        DecryptSecrets(document);
        return document;
    }

    public async Task SaveAsync(ProxyDocument document, CancellationToken ct = default)
    {
        document.Normalize();
        ValidateDocument(document, requireAccount: false);
        ProtectSecrets(document);
        await _store.SaveAsync(document, ct).ConfigureAwait(false);
    }

    /// <summary>保存并对运行中的代理热更新；监听地址/端口变化会返回 RestartRequired。</summary>
    public async Task<ProxyRuntimeUpdateResult> SaveAndApplyAsync(ProxyDocument document,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            document.Normalize();
            ValidateDocument(document, requireAccount: _host.Status.Running);
            ProtectSecrets(document);
            await _store.SaveAsync(document, ct).ConfigureAwait(false);

            if (!_host.Status.Running)
                return new ProxyRuntimeUpdateResult(false, false,
                    "设置已保存；代理未运行，下次启动生效。");

            var result = _host.UpdateRuntime(document.Options, document.Accounts);
            SetError(null);
            await LocalAudit.LogAsync("proxy_config_update", "热更新局域网代理配置",
                CommandStatus.Executed, result: result.Message).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool Disconnect(long connectionId) => _host.Disconnect(connectionId);

    public int DisconnectAll() => _host.DisconnectAll();

    /// <summary>保存并启动；配置不合法或没有可用账号时抛出。</summary>
    public async Task StartAsync(ProxyDocument document, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            document.Normalize();
            ValidateDocument(document, requireAccount: true);
            ProtectSecrets(document);
            await _store.SaveAsync(document, ct).ConfigureAwait(false);
            await _host.StartAsync(document.Options, document.Accounts, ct).ConfigureAwait(false);
            SetError(null);
            await LocalAudit.LogAsync("proxy_start",
                $"HTTP {document.Options.HttpEndpointText()} / SOCKS5 {document.Options.Socks5EndpointText()}",
                CommandStatus.Executed,
                result: "局域网代理已启动").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
            await LocalAudit.LogAsync("proxy_start", "启动局域网代理",
                CommandStatus.Failed, result: ex.Message).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _host.StopAsync().ConfigureAwait(false);
            SetError(null);
            await LocalAudit.LogAsync("proxy_stop", "停止局域网代理",
                CommandStatus.Executed).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>App 启动时调用：读取配置，只有 Enabled=true 才启动；失败只记录错误，不阻断 App。</summary>
    public async Task<bool> StartIfEnabledAsync(CancellationToken ct = default)
    {
        try
        {
            var document = await LoadAsync(ct).ConfigureAwait(false);
            if (!document.Options.Enabled)
                return false;
            await StartAsync(document, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        try { _host.Dispose(); } catch { /* ignore */ }
        _gate.Dispose();
    }

    private void SetError(string? message)
    {
        lock (_errorGate)
            _lastError = message;
    }

    private static void ValidateDocument(ProxyDocument document, bool requireAccount)
    {
        var errors = document.Options.Validate();
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join("；", errors));
        if (requireAccount)
            ValidateAccountRequirement(document);
    }

    private static void ValidateAccountRequirement(ProxyDocument document)
    {
        if (!document.Options.AllowAnonymous &&
            !document.Accounts.Any(account => account.Enabled &&
                !string.IsNullOrWhiteSpace(account.Username) &&
                !string.IsNullOrWhiteSpace(account.PasswordHash)))
        {
            throw new InvalidOperationException(
                "未开启匿名模式时，至少需要一个已启用且设置了密码的账号。");
        }
    }

    private void DecryptSecrets(ProxyDocument document)
    {
        var protectedValue = document.Options.UpstreamProxyPasswordProtected;
        document.Options.UpstreamProxyPassword = string.IsNullOrWhiteSpace(protectedValue)
            ? string.Empty
            : _protector.Unprotect(protectedValue) ?? string.Empty;
    }

    private void ProtectSecrets(ProxyDocument document)
    {
        var plain = document.Options.UpstreamProxyPassword;
        if (!string.IsNullOrEmpty(plain))
        {
            document.Options.UpstreamProxyPasswordProtected =
                _protector.Protect(plain) ?? string.Empty;
            return;
        }

        if (string.IsNullOrWhiteSpace(document.Options.UpstreamProxyPasswordProtected))
            document.Options.UpstreamProxyPasswordProtected = string.Empty;
    }
}

internal static class ProxyEndpointText
{
    public static string HttpEndpointText(this ProxyOptions options) =>
        options.HttpEnabled ? $"{options.BindAddress}:{options.HttpPort}" : "关闭";

    public static string Socks5EndpointText(this ProxyOptions options) =>
        options.Socks5Enabled ? $"{options.BindAddress}:{options.Socks5Port}" : "关闭";
}
