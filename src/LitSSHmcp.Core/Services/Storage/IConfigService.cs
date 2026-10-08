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

public class ConfigService : IConfigService
{
    private readonly string _configPath;
    private readonly ISecretProtector _protector;

    public ConfigService() : this(ConfigPaths.ConfigFile, new DpapiSecretProtector())
    {
    }

    public ConfigService(string configPath, ISecretProtector protector)
    {
        _configPath = configPath;
        _protector = protector;
    }

    public string GetConfigPath() => _configPath;

    public async Task<AppConfig> LoadConfigAsync()
    {
        if (!File.Exists(_configPath))
        {
            var defaultConfig = CreateDefaultConfig();
            await SaveConfigAsync(defaultConfig);
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

            return config;
        }
        catch
        {
            return new AppConfig();
        }
    }

    public async Task SaveConfigAsync(AppConfig config)
    {
        // 深拷贝后加密，避免密文污染调用方内存中的配置对象
        var clone = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config, AppConfigJson.Options), AppConfigJson.Options)
                    ?? new AppConfig();
        EncryptSecrets(clone);
        var json = JsonSerializer.Serialize(clone, AppConfigJson.Options);

        // 原子写：先写同目录临时文件，再整体替换目标文件。
        // 若不原子，MCP 侧按 mtime 热加载时可能读到"写了一半"的 JSON（截断/空文件），
        // 导致这一次修改被当成损坏而丢弃，表现为"在界面改了但 MCP 没生效"。
        await WriteAtomicAsync(json);
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

        foreach (var provider in config.Agent?.Providers ?? Array.Empty<AgentProviderGroupConfig>())
            provider.ApiKey = _protector.Protect(provider.ApiKey);

        if (config.Agent?.Memory is not null)
            config.Agent.Memory.ApiKey = _protector.Protect(config.Agent.Memory.ApiKey);
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

        foreach (var provider in config.Agent?.Providers ?? Array.Empty<AgentProviderGroupConfig>())
            provider.ApiKey = _protector.Unprotect(provider.ApiKey);

        if (config.Agent?.Memory is not null)
            config.Agent.Memory.ApiKey = _protector.Unprotect(config.Agent.Memory.ApiKey);
    }

    private bool ContainsPlaintextSecrets(AppConfig config)
    {
        foreach (var server in config.Servers)
        {
            if (HasPlaintext(server.Password)) return true;
            if (HasPlaintext(server.KeyFilePassphrase)) return true;
            if (HasPlaintext(server.SudoPassword)) return true;
        }

        return config.DataSources.Any(ds => HasPlaintext(ds.Password)) ||
               (config.Agent?.Providers ?? Array.Empty<AgentProviderGroupConfig>()).Any(p => HasPlaintext(p.ApiKey)) ||
               (config.Agent?.Memory is not null && HasPlaintext(config.Agent.Memory.ApiKey));
    }

    private bool HasPlaintext(string? value) => !string.IsNullOrEmpty(value) && !_protector.IsProtected(value);

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
