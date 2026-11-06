using System.IO;

namespace LitSSHmcp.App.Services.Toolbox;

public static class PasswordVaultTempFiles
{
    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LitSSH", "password-vault-temp");

    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(DirectoryPath))
                return;
            foreach (var file in Directory.EnumerateFiles(DirectoryPath))
            {
                try { File.Delete(file); } catch { /* 被外部程序占用时跳过 */ }
            }
        }
        catch
        {
            // 临时目录清理失败不影响启动
        }
    }
}
