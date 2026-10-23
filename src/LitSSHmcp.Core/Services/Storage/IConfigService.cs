using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;

namespace LitSSHmcp.Core.Services.Storage;

public interface IConfigService
{
    Task<AppConfig> LoadConfigAsync();
    Task SaveConfigAsync(AppConfig config);
    string GetConfigPath();
}

/// <summary>
/// 安全入口使用的严格配置读取：配置文件存在但无法读取/解析/迁移时抛 <see cref="ConfigLoadException"/>，
/// 而不是静默回退默认配置，避免损坏配置被当作“未设置应用锁”而绕过。
/// </summary>
public interface IStrictConfigService
{
    Task<AppConfig> LoadStrictAsync();
}

/// <summary>配置文件存在但无法读取、解析或迁移。</summary>
public sealed class ConfigLoadException : Exception
{
    public string ConfigPath { get; }

    public ConfigLoadException(string message, string configPath, Exception? inner = null)
        : base(message, inner)
    {
        ConfigPath = configPath;
    }
}

/// <summary>并发保存时同一字段被多个窗口/进程修改，拒绝静默覆盖。</summary>
public sealed class ConfigConflictException : Exception
{
    public ConfigConflictException(IReadOnlyList<string> conflictPaths)
        : base("配置已被其他窗口/进程修改，且存在同字段冲突；请重新加载后再保存。")
    {
        ConflictPaths = conflictPaths;
    }

    public IReadOnlyList<string> ConflictPaths { get; }
}

/// <summary>
/// 配置冲突通知（Core 不依赖 UI）：保存遇到同字段冲突时触发，App 订阅后统一弹窗提示重新加载。
/// 通知失败不影响原始异常继续抛给调用方。
/// </summary>
public static class ConfigConflictNotifier
{
    public static event Action<ConfigConflictException>? ConflictDetected;

    internal static void Notify(ConfigConflictException ex)
    {
        try { ConflictDetected?.Invoke(ex); } catch { /* 通知失败不掩盖保存冲突 */ }
    }
}

public class ConfigService : IConfigService, IStrictConfigService
{
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private static readonly ConditionalWeakTable<AppConfig, ConfigSnapshot> Snapshots = new();
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    private readonly string _configPath;
    private readonly ISecretProtector _protector;

    private sealed class ConfigSnapshot(AppConfig config, string fingerprint)
    {
        public AppConfig Config { get; } = config;
        public string Fingerprint { get; } = fingerprint;
    }

    public ConfigService() : this(ConfigPaths.ConfigFile, new DpapiSecretProtector())
    {
    }

    public ConfigService(string configPath, ISecretProtector protector)
    {
        _configPath = configPath;
        _protector = protector;
    }

    public string GetConfigPath() => _configPath;

    public Task<AppConfig> LoadConfigAsync() => LoadConfigCoreAsync(failOnCorrupt: false);

    public Task<AppConfig> LoadStrictAsync() => LoadConfigCoreAsync(failOnCorrupt: true);

    private async Task<AppConfig> LoadConfigCoreAsync(bool failOnCorrupt)
    {
        if (!File.Exists(_configPath))
        {
            var defaultConfig = CreateDefaultConfig();
            await SaveConfigAsync(defaultConfig);
            Remember(config: defaultConfig, persisted: defaultConfig);
            return defaultConfig;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_configPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, AppConfigJson.Options) ?? new AppConfig();

            var needsMigration = ContainsPlaintextSecrets(config);
            DecryptSecrets(config);

            if (ConfigMigrator.Migrate(config) || needsMigration)
                await SaveConfigAsync(config);

            Remember(config: config, persisted: config);
            ServerConfigCache.Update(config.Servers);
            return config;
        }
        catch (Exception ex) when (failOnCorrupt)
        {
            throw new ConfigLoadException(
                $"配置文件损坏或无法读取：{_configPath}。请修复或从备份恢复后重试。", _configPath, ex);
        }
        catch
        {
            return new AppConfig();
        }
    }

    public async Task SaveConfigAsync(AppConfig config)
    {
        await SaveGate.WaitAsync();
        try
        {
            await using var fileLock = await AcquireFileLockAsync();
            var current = await TryReadConfigForMergeAsync();
            var merged = config;

            if (current is not null &&
                Snapshots.TryGetValue(config, out var snapshot) &&
                !string.Equals(snapshot.Fingerprint, Fingerprint(current), StringComparison.Ordinal))
            {
                // 其他窗口/进程已写入新配置：把本地修改三路合并到最新内容；
                // 同一字段双方都改则抛 ConfigConflictException，禁止静默覆盖。
                try
                {
                    merged = ConfigMerge.Merge(snapshot.Config, config, current);
                }
                catch (ConfigConflictException ex)
                {
                    ConfigConflictNotifier.Notify(ex);
                    throw;
                }
            }

            // 深拷贝后加密，避免密文污染调用方内存中的配置对象
            var clone = Clone(merged);
            EncryptSecrets(clone);
            var json = JsonSerializer.Serialize(clone, AppConfigJson.Options);

            // 原子写：先写同目录临时文件，再整体替换目标文件。
            // 若不原子，MCP 侧按 mtime 热加载时可能读到"写了一半"的 JSON（截断/空文件），
            // 导致这一次修改被当成损坏而丢弃，表现为"在界面改了但 MCP 没生效"。
            await WriteAtomicAsync(json);

            // 刷新跳板机解析用的服务器快照（明文配置，连接工厂同步读取）。
            ServerConfigCache.Update(merged.Servers);
            Remember(config, merged);
        }
        finally
        {
            SaveGate.Release();
        }
    }

    private async Task<FileStream> AcquireFileLockAsync()
    {
        var lockPath = _configPath + ".lock";
        var dir = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
        }
    }

    private async Task<AppConfig?> TryReadConfigForMergeAsync()
    {
        if (!File.Exists(_configPath))
            return null;
        try
        {
            var json = await File.ReadAllTextAsync(_configPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, AppConfigJson.Options);
            if (config is null)
                return null;
            DecryptSecrets(config);
            ConfigMigrator.Migrate(config);
            return config;
        }
        catch
        {
            return null;
        }
    }

    private static AppConfig Clone(AppConfig config)
        => JsonSerializer.Deserialize<AppConfig>(
               JsonSerializer.Serialize(config, AppConfigJson.Options), AppConfigJson.Options)
           ?? new AppConfig();

    private static string Fingerprint(AppConfig config)
        => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config, AppConfigJson.Options))));

    private static void Remember(AppConfig config, AppConfig persisted)
    {
        var snapshot = Clone(persisted);
        Snapshots.AddOrUpdate(config, new ConfigSnapshot(snapshot, Fingerprint(snapshot)));
    }

    private async Task WriteAtomicAsync(string json)
    {
        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = _configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, json);
            // 同卷替换在 Windows 上近似原子，读者要么看到旧文件、要么看到新文件。
            File.Move(tmp, _configPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 清理临时文件失败不影响主流程 */ }
            throw;
        }
    }

    private void EncryptSecrets(AppConfig config)
    {
        foreach (var server in config.Servers)
        {
            server.Password = _protector.Protect(server.Password);
            server.KeyFilePassphrase = _protector.Protect(server.KeyFilePassphrase);
            server.SudoPassword = _protector.Protect(server.SudoPassword);
        }

        foreach (var ds in config.DataSources)
            ds.Password = _protector.Protect(ds.Password);

        foreach (var rd in config.RemoteDesktops ?? Array.Empty<RemoteDesktopConfig>())
            rd.Password = _protector.Protect(rd.Password);

        foreach (var provider in config.Agent?.Providers ?? Array.Empty<AgentProviderGroupConfig>())
            provider.ApiKey = _protector.Protect(provider.ApiKey);

        if (config.Agent?.Memory is not null)
            config.Agent.Memory.ApiKey = _protector.Protect(config.Agent.Memory.ApiKey);

        foreach (var server in config.Agent?.McpServers ?? Array.Empty<AgentMcpServerConfig>())
        {
            if (server is null) continue;
            server.Env = ProtectLineValues(server.Env, '=');
            server.Headers = ProtectLineValues(server.Headers, ':');
        }

        foreach (var action in (config.Triggers ?? Array.Empty<TriggerConfig>()).SelectMany(t => t?.Actions ?? Array.Empty<TriggerAction>()))
            action.Secret = _protector.Protect(action.Secret);

        if (config.AppLock is not null)
        {
            config.AppLock.PasswordHash = _protector.Protect(config.AppLock.PasswordHash);
            config.AppLock.Salt = _protector.Protect(config.AppLock.Salt);
        }

        if (config.Web?.Https is not null)
            config.Web.Https.CertificatePassword =
                _protector.Protect(config.Web.Https.CertificatePassword) ?? string.Empty;
    }

    private void DecryptSecrets(AppConfig config)
    {
        foreach (var server in config.Servers)
        {
            server.Password = _protector.Unprotect(server.Password);
            server.KeyFilePassphrase = _protector.Unprotect(server.KeyFilePassphrase);
            server.SudoPassword = _protector.Unprotect(server.SudoPassword);
        }

        foreach (var ds in config.DataSources)
            ds.Password = _protector.Unprotect(ds.Password);

        foreach (var rd in config.RemoteDesktops ?? Array.Empty<RemoteDesktopConfig>())
            rd.Password = _protector.Unprotect(rd.Password);

        foreach (var provider in config.Agent?.Providers ?? Array.Empty<AgentProviderGroupConfig>())
            provider.ApiKey = _protector.Unprotect(provider.ApiKey);

        if (config.Agent?.Memory is not null)
            config.Agent.Memory.ApiKey = _protector.Unprotect(config.Agent.Memory.ApiKey);

        foreach (var server in config.Agent?.McpServers ?? Array.Empty<AgentMcpServerConfig>())
        {
            if (server is null) continue;
            server.Env = UnprotectLineValues(server.Env, '=');
            server.Headers = UnprotectLineValues(server.Headers, ':');
        }

        foreach (var action in (config.Triggers ?? Array.Empty<TriggerConfig>()).SelectMany(t => t?.Actions ?? Array.Empty<TriggerAction>()))
            action.Secret = _protector.Unprotect(action.Secret);

        if (config.AppLock is not null)
        {
            config.AppLock.PasswordHash = _protector.Unprotect(config.AppLock.PasswordHash);
            config.AppLock.Salt = _protector.Unprotect(config.AppLock.Salt);
        }

        if (config.Web?.Https is not null)
            config.Web.Https.CertificatePassword =
                _protector.Unprotect(config.Web.Https.CertificatePassword) ?? string.Empty;
    }

    private bool ContainsPlaintextSecrets(AppConfig config)
    {
        foreach (var server in config.Servers)
        {
            if (HasPlaintext(server.Password)) return true;
            if (HasPlaintext(server.KeyFilePassphrase)) return true;
            if (HasPlaintext(server.SudoPassword)) return true;
        }

        foreach (var mcp in config.Agent?.McpServers ?? Array.Empty<AgentMcpServerConfig>())
        {
            if (mcp is null) continue;
            if (HasPlaintextLine(mcp.Env, '=')) return true;
            if (HasPlaintextLine(mcp.Headers, ':')) return true;
        }

        return config.DataSources.Any(ds => HasPlaintext(ds.Password)) ||
               (config.RemoteDesktops ?? Array.Empty<RemoteDesktopConfig>()).Any(rd => HasPlaintext(rd.Password)) ||
               (config.Agent?.Providers ?? Array.Empty<AgentProviderGroupConfig>()).Any(p => HasPlaintext(p.ApiKey)) ||
               (config.Agent?.Memory is not null && HasPlaintext(config.Agent.Memory.ApiKey)) ||
               (config.Web?.Https is not null && HasPlaintext(config.Web.Https.CertificatePassword)) ||
               (config.Triggers ?? Array.Empty<TriggerConfig>()).Any(t => (t?.Actions ?? Array.Empty<TriggerAction>()).Any(a => HasPlaintext(a.Secret)));
    }

    private bool HasPlaintext(string? value) => !string.IsNullOrEmpty(value) && !_protector.IsProtected(value);

    /// <summary>行内"分隔符之后的值"是否为明文（未加 enc: 前缀）。</summary>
    private bool HasPlaintextLine(string[]? lines, char separator)
    {
        foreach (var line in lines ?? Array.Empty<string>())
        {
            var i = line?.IndexOf(separator) ?? -1;
            if (i < 0) continue;
            var value = line![(i + 1)..].Trim();
            if (value.Length > 0 && !_protector.IsProtected(value))
                return true;
        }
        return false;
    }

    /// <summary>把行内分隔符之后的值加 DPAPI 保护（如 <c>KEY=VALUE</c> / <c>Name: Value</c>）。</summary>
    private string[] ProtectLineValues(string[]? lines, char separator)
        => TransformLineValues(lines, separator, _protector.Protect);

    private string[] UnprotectLineValues(string[]? lines, char separator)
        => TransformLineValues(lines, separator, _protector.Unprotect);

    private static string[] TransformLineValues(string[]? lines, char separator, Func<string, string?> transform)
    {
        var result = new List<string>();
        foreach (var line in lines ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var i = line.IndexOf(separator);
            if (i < 0)
            {
                result.Add(line);
                continue;
            }
            var value = line[(i + 1)..].Trim();
            var changed = value.Length > 0 ? transform(value) : null;
            if (string.IsNullOrEmpty(changed) || changed == value)
            {
                result.Add(line);
                continue;
            }
            // 冒号后补一个空格保持 "Name: value" 习惯；等号后不加（KEY=VALUE）
            result.Add(line[..(i + 1)] + (separator == ':' ? " " : string.Empty) + changed);
        }
        return result.ToArray();
    }

    private AppConfig CreateDefaultConfig()
    {
        return new AppConfig
        {
            SchemaVersion = AppConfig.CurrentSchemaVersion,
            Servers = Array.Empty<SshServerConfig>(),
            DataSources = Array.Empty<DataSourceConfig>(),
            Applications = Array.Empty<ApplicationConfig>(),
            Relations = Array.Empty<RelationConfig>(),
            Security = new SecurityConfig
            {
                // 内置默认规则见 CommandFilterConfig 的类内默认值（含 Docker 高危/敏感规则）。
                CommandFilter = new CommandFilterConfig(),
                SqlFilter = new SqlFilterConfig(),
                FileTransfer = new FileTransferConfig
                {
                    Enabled = true,
                    AllowedLocalPaths = new[]
                    {
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                    },
                    AllowedRemotePaths = new[] { "/home", "/tmp", "/var/log" },
                    MaxFileSizeBytes = 100 * 1024 * 1024,
                    RequireApproval = true
                }
            }
        };
    }
}
