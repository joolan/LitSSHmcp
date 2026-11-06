using System.Diagnostics;
using LitSSHmcp.App.Services;
using System.IO;
using System.Text;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services.Toolbox;

/// <summary>Hosts 文件读写、备份与 DNS 刷新。保存失败时尝试通过 UAC 提权复制。</summary>
public sealed class HostsEditorService
{
    private static readonly Encoding HostsEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public HostsEditorService(string? hostsPath = null, string? backupDir = null)
    {
        HostsPath = hostsPath ?? Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
        BackupDir = backupDir ?? Path.Combine(ConfigPaths.AppDataDir, "hosts-backups");
    }

    public string HostsPath { get; }
    public string BackupDir { get; }

    public async Task<string> ReadAsync()
    {
        if (!File.Exists(HostsPath))
            return string.Empty;
        return await File.ReadAllTextAsync(HostsPath, HostsEncoding);
    }

    public async Task<string?> BackupAsync()
    {
        if (!File.Exists(HostsPath))
            return null;

        Directory.CreateDirectory(BackupDir);
        var name = $"hosts-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
        var path = Path.Combine(BackupDir, name);
        await using var source = File.OpenRead(HostsPath);
        await using var target = File.Create(path);
        await source.CopyToAsync(target);

        foreach (var old in Directory.EnumerateFiles(BackupDir, "hosts-*.bak")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(20))
        {
            try { File.Delete(old); } catch { /* ignore */ }
        }
        return path;
    }

    public async Task<HostsSaveResult> SaveAsync(string content)
    {
        var backup = await BackupAsync();
        try
        {
            await File.WriteAllTextAsync(HostsPath, content, HostsEncoding);
            return new HostsSaveResult(true, "已保存 hosts。", backup);
        }
        catch (UnauthorizedAccessException)
        {
            return await SaveElevatedAsync(content, backup);
        }
        catch (Exception ex)
        {
            return new HostsSaveResult(false, "保存失败：" + ex.Message, backup);
        }
    }

    public async Task<bool> FlushDnsAsync()
    {
        try
        {
            using var process = NativeShell.Start(new ProcessStartInfo("ipconfig", "/flushdns")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
                return false;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<HostsSaveResult> SaveElevatedAsync(string content, string? backup)
    {
        string? temp = null;
        try
        {
            temp = Path.Combine(Path.GetTempPath(), "litssh-hosts-" + Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllTextAsync(temp, content, HostsEncoding);
            var arguments = $"/c copy /Y \"{temp}\" \"{HostsPath}\" >nul";
            using var process = NativeShell.Start(new ProcessStartInfo("cmd.exe", arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null)
                return new HostsSaveResult(false, "未能启动管理员保存进程。", backup);

            await process.WaitForExitAsync();
            return process.ExitCode == 0
                ? new HostsSaveResult(true, "已通过管理员权限保存 hosts。", backup)
                : new HostsSaveResult(false, "管理员保存进程返回失败。", backup);
        }
        catch (Exception ex)
        {
            return new HostsSaveResult(false, "管理员保存失败：" + ex.Message, backup);
        }
        finally
        {
            if (temp is not null)
            {
                try { File.Delete(temp); } catch { /* ignore */ }
            }
        }
    }
}

public sealed record HostsSaveResult(bool Success, string Message, string? BackupPath);
