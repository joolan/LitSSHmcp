using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.DnsServer;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services;

/// <summary>
/// DNS 配置与进程级宿主：App 启动时按配置自动启动，退出时统一停止。
/// 工具箱窗口与管理流程都通过该单例读写，避免多个窗口各自监听同一端口。
/// </summary>
public sealed class DnsHostService : IDisposable
{
    private readonly DnsStore _store;
    private readonly DnsServerHost _host;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _errorGate = new();
    private string? _lastError;

    public DnsHostService(string? storePath = null, DnsServerHost? host = null)
    {
        _store = new DnsStore(storePath);
        _host = host ?? new DnsServerHost();
    }

    public string ConfigPath => _store.Path;
    public DnsQueryLog Log => _host.Log;
    public DnsStatus Status => _host.Status;
    public int CacheCount => _host.CacheCount;

    public void ClearCache() => _host.ClearCache();

    public DnsCacheState GetCacheState(string name, ushort type) =>
        _host.GetCacheState(name, type);

    public Task<IReadOnlyList<DnsUpstreamProbeResult>> ProbeUpstreamsAsync(
        DnsOptions options, CancellationToken ct = default) =>
        _host.ProbeUpstreamsAsync(options, ct);

    public string? LastError
    {
        get { lock (_errorGate) return _lastError; }
    }

    public event EventHandler? StateChanged
    {
        add => _host.StateChanged += value;
        remove => _host.StateChanged -= value;
    }

    public async Task<DnsDocument> LoadAsync(CancellationToken ct = default) =>
        await _store.LoadAsync(ct).ConfigureAwait(false);

    public async Task SaveAsync(DnsDocument document, CancellationToken ct = default)
    {
        document.Normalize();
        ValidateDocument(document);
        await _store.SaveAsync(document, ct).ConfigureAwait(false);
    }

    /// <summary>保存并对运行中的 DNS 热更新；监听地址/端口变化会返回 RestartRequired。</summary>
    public async Task<DnsRuntimeUpdateResult> SaveAndApplyAsync(DnsDocument document,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            document.Normalize();
            ValidateDocument(document);
            await _store.SaveAsync(document, ct).ConfigureAwait(false);

            if (!_host.Status.Running)
                return new DnsRuntimeUpdateResult(false, false,
                    "设置已保存；DNS 未运行，下次启动生效。");

            var result = _host.UpdateRuntime(document.Options);
            SetError(null);
            await LocalAudit.LogAsync("dns_config_update", "热更新轻量 DNS 配置",
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

    /// <summary>保存并启动；配置不合法时抛出。</summary>
    public async Task StartAsync(DnsDocument document, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            document.Normalize();
            ValidateDocument(document);
            await _store.SaveAsync(document, ct).ConfigureAwait(false);
            await _host.StartAsync(document.Options, ct).ConfigureAwait(false);
            SetError(null);
            await LocalAudit.LogAsync("dns_start",
                $"UDP {document.Options.UdpEndpointText()} / TCP {document.Options.TcpEndpointText()}",
                CommandStatus.Executed,
                result: "轻量 DNS 已启动").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
            await LocalAudit.LogAsync("dns_start", "启动轻量 DNS",
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
            await LocalAudit.LogAsync("dns_stop", "停止轻量 DNS",
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

    private static void ValidateDocument(DnsDocument document)
    {
        var errors = document.Options.Validate();
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join("；", errors));
    }
}

internal static class DnsEndpointText
{
    public static string UdpEndpointText(this DnsOptions options) =>
        options.UdpEnabled ? $"{options.BindAddress}:{options.Port}" : "关闭";

    public static string TcpEndpointText(this DnsOptions options) =>
        options.TcpEnabled ? $"{options.BindAddress}:{options.Port}" : "关闭";
}
