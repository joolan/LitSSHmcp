using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace LitSSHmcp.McpServer.Services;

/// <summary>
/// 工具收敛过渡期的旧名别名策略（Phase A）：默认不注册；仅 <c>tools.legacyAliases=true</c>
/// 时注册旧工具类，避免旧客户端立即中断。新工具名不受影响。
/// </summary>
public static class LegacyToolPolicy
{
    private static readonly HashSet<string> LegacyNames = new(StringComparer.Ordinal)
    {
        "ssh_upload_file",
        "ssh_upload_files",
        "ssh_download_file",
        "ssh_download_files"
    };

    public static bool IsLegacy(string? name) => !string.IsNullOrEmpty(name) && LegacyNames.Contains(name);

    public static bool IsEnabled(AppConfig? config) => config?.Tools?.LegacyAliases == true;

    public static async Task<bool> IsEnabledAsync(IServiceProvider? services)
    {
        if (services?.GetService(typeof(IConfigService)) is not IConfigService configService)
            return false;
        try
        {
            return IsEnabled(await configService.LoadConfigAsync().ConfigureAwait(false));
        }
        catch
        {
            return false;
        }
    }
}
