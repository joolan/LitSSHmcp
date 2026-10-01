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
        await File.WriteAllTextAsync(_configPath, json);
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
    }

    private bool ContainsPlaintextSecrets(AppConfig config)
    {
        foreach (var server in config.Servers)
        {
            if (HasPlaintext(server.Password)) return true;
            if (HasPlaintext(server.KeyFilePassphrase)) return true;
            if (HasPlaintext(server.SudoPassword)) return true;
        }

        return config.DataSources.Any(ds => HasPlaintext(ds.Password));
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
                CommandFilter = new CommandFilterConfig
                {
                    BlockedCommands = new[]
                    {
                        "rm -rf /",
                        "mkfs",
                        "dd if=/dev/zero",
                        ":(){ :|:& };:",
                        "chmod -R 777 /",
                        "wget | sh",
                        "curl | sh",
                        // Docker 高危：清库/清卷/清网络/删服务/退出集群/特权或挂根目录运行
                        "docker system prune",
                        "docker volume prune",
                        "docker network prune",
                        "docker volume rm",
                        "docker service rm",
                        "docker swarm leave",
                        "docker run --privileged",
                        "docker run -v /"
                    },
                    SensitiveCommands = new[]
                    {
                        "rm ",
                        "chmod",
                        "chown",
                        "systemctl stop",
                        "systemctl restart",
                        "reboot",
                        "shutdown",
                        "kill",
                        "pkill",
                        "apt remove",
                        "yum remove",
                        // Docker 写/运维操作（需桌面确认；只读的 docker ps/logs/inspect/stats 不受限）
                        "docker rm",
                        "docker rmi",
                        "docker kill",
                        "docker stop",
                        "docker restart",
                        "docker run",
                        "docker exec",
                        "docker cp",
                        "docker compose down",
                        "docker compose rm"
                    },
                    SensitivePatterns = new[]
                    {
                        "\\brm\\b",
                        "\\bchmod\\b",
                        "\\bchown\\b",
                        "\\breboot\\b",
                        "\\bshutdown\\b",
                        "\\bkill\\b"
                    }
                },
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
