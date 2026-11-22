using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Proxy;
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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _errorGate = new();
    private string? _lastError;

    public ProxyHostService(string? storePath = null, ProxyServerHost? host = null)
    {
        _store = new ProxyStore(storePath);
        _host = host ?? new ProxyServerHost();
    }

    public string ConfigPath => _store.Path;
    public ProxyRequestLog Log => _host.Log;
    public ProxyStatus Status => _host.Status;
    public IReadOnlyList<ProxyAccount> RunningAccounts => _host.Accounts;

    public string? LastError
    {
        get { lock (_errorGate) return _lastError; }
    }

    public event EventHandler? StateChanged
    {
        add => _host.StateChanged += value;
        remove => _host.StateChanged -= value;
    }

    public Task<ProxyDocument> LoadAsync(CancellationToken ct = default) => _store.LoadAsync(ct);

    public async Task SaveAsync(ProxyDocument document, CancellationToken ct = default)
    {
        await _store.SaveAsync(document, ct).ConfigureAwait(false);
    }

    /// <summary>保存并启动；配置不合法或没有可用账号时抛出。</summary>
    public async Task StartAsync(ProxyDocument document, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            document.Normalize();
            if (!document.Options.AllowAnonymous &&
                !document.Accounts.Any(account => account.Enabled &&
                    !string.IsNullOrWhiteSpace(account.Username) &&
                    !string.IsNullOrWhiteSpace(account.PasswordHash)))
            {
                throw new InvalidOperationException(
                    "未开启匿名模式时，至少需要一个已启用且设置了密码的账号。");
            }

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
}

internal static class ProxyEndpointText
{
    public static string HttpEndpointText(this ProxyOptions options) =>
        options.HttpEnabled ? $"{options.BindAddress}:{options.HttpPort}" : "关闭";

    public static string Socks5EndpointText(this ProxyOptions options) =>
        options.Socks5Enabled ? $"{options.BindAddress}:{options.Socks5Port}" : "关闭";
}
