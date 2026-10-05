using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Core.Services.Security;

/// <summary>
/// 提供当前生效的安全配置（命令过滤 / SQL 过滤 / 文件传输）。
/// 实现按配置文件最后写入时间缓存，配置文件变更后自动生效，无需重启。
/// </summary>
public interface ISecurityOptionsProvider
{
    /// <summary>全局 MCP 开关：false 时拒绝所有工具调用。</summary>
    bool Enabled { get; }

    CommandFilterConfig CommandFilter { get; }
    SqlFilterConfig SqlFilter { get; }
    FileTransferConfig FileTransfer { get; }
    SshHostKeyConfig SshHostKey { get; }
    DiscoveryConfig Discovery { get; }
    LimitsConfig Limits { get; }
    AuditConfig Audit { get; }
    ApprovalConfig Approval { get; }
    ConnectionPoolConfig ConnectionPool { get; }
    void Invalidate();
}

public sealed class SecurityOptionsProvider : ISecurityOptionsProvider
{
    private readonly string _configPath;
    private readonly object _gate = new();

    private DateTime _lastWriteUtc = DateTime.MinValue;
    private bool _enabled = true;
    private CommandFilterConfig _commandFilter = new();
    private SqlFilterConfig _sqlFilter = new();
    private FileTransferConfig _fileTransfer = new();
    private SshHostKeyConfig _sshHostKey = new();
    private DiscoveryConfig _discovery = new();
    private LimitsConfig _limits = new();
    private AuditConfig _audit = new();
    private ApprovalConfig _approval = new();
    private ConnectionPoolConfig _connectionPool = new();

    public SecurityOptionsProvider() : this(ConfigPaths.ConfigFile)
    {
    }

    public SecurityOptionsProvider(string configPath)
    {
        _configPath = configPath;
        Reload(CurrentWriteTime());
    }

    public CommandFilterConfig CommandFilter
    {
        get { EnsureFresh(); return _commandFilter; }
    }

    public bool Enabled
    {
        get { EnsureFresh(); return _enabled; }
    }

    public SqlFilterConfig SqlFilter
    {
        get { EnsureFresh(); return _sqlFilter; }
    }

    public FileTransferConfig FileTransfer
    {
        get { EnsureFresh(); return _fileTransfer; }
    }

    public SshHostKeyConfig SshHostKey
    {
        get { EnsureFresh(); return _sshHostKey; }
    }

    public DiscoveryConfig Discovery
    {
        get { EnsureFresh(); return _discovery; }
    }

    public LimitsConfig Limits
    {
        get { EnsureFresh(); return _limits; }
    }

    public AuditConfig Audit
    {
        get { EnsureFresh(); return _audit; }
    }

    public ApprovalConfig Approval
    {
        get { EnsureFresh(); return _approval; }
    }

    public ConnectionPoolConfig ConnectionPool
    {
        get { EnsureFresh(); return _connectionPool; }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _lastWriteUtc = DateTime.MinValue;
        }

        EnsureFresh();
    }

    private DateTime CurrentWriteTime()
    {
        try
        {
            return File.Exists(_configPath) ? File.GetLastWriteTimeUtc(_configPath) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private void EnsureFresh()
    {
        lock (_gate)
        {
            var last = CurrentWriteTime();
            if (last == _lastWriteUtc)
                return;

            ReloadUnlocked(last);
        }
    }

    private void Reload(DateTime lastWriteUtc)
    {
        lock (_gate)
        {
            ReloadUnlocked(lastWriteUtc);
        }
    }

    private void ReloadUnlocked(DateTime lastWriteUtc)
    {
        if (!File.Exists(_configPath))
        {
            // 文件尚不存在(全新安装/首次读取): 使用类内默认值，并记录当前时间戳避免反复尝试。
            _lastWriteUtc = lastWriteUtc;
            return;
        }

        AppConfig? config;
        try
        {
            var json = File.ReadAllText(_configPath);
            config = JsonSerializer.Deserialize<AppConfig>(json, AppConfigJson.Options);
        }
        catch
        {
            // 读取/解析失败(文件损坏，或恰好读到写入中的临时状态): 保留上一次有效值，
            // 且**不更新 _lastWriteUtc** —— 下次访问会重试，避免把"暂时读不到"误当成"已生效"。
            return;
        }

        if (config is null)
            return;

        _enabled = config.Security?.Enabled ?? true;
        _commandFilter = config.Security?.CommandFilter ?? new CommandFilterConfig();
        _sqlFilter = config.Security?.SqlFilter ?? new SqlFilterConfig();
        _fileTransfer = config.Security?.FileTransfer ?? new FileTransferConfig();
        _sshHostKey = config.Security?.SshHostKey ?? new SshHostKeyConfig();
        _discovery = config.Security?.Discovery ?? new DiscoveryConfig();
        _limits = config.Security?.Limits ?? new LimitsConfig();
        _audit = config.Security?.Audit ?? new AuditConfig();
        _approval = config.Security?.Approval ?? new ApprovalConfig();
        _connectionPool = config.ConnectionPool ?? new ConnectionPoolConfig();

        _lastWriteUtc = lastWriteUtc;
    }
}
