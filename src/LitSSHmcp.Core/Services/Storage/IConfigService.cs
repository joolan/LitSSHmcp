using System.Text.Json;
using LitSSHmcp.Core.Models;

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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ConfigService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "LitSSH");
        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, "config.json");
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
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            return config ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public async Task SaveConfigAsync(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        await File.WriteAllTextAsync(_configPath, json);
    }

    private AppConfig CreateDefaultConfig()
    {
        return new AppConfig
        {
            Servers = Array.Empty<SshServerConfig>(),
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
                        "curl | sh"
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
                        "docker rm",
                        "docker stop"
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