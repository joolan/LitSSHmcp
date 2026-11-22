using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Win32;

namespace LitSSHmcp.App.Services.Toolbox;

public enum EnvironmentVariableScope
{
    User,
    System
}

public sealed class EnvironmentVariableEntry
{
    public EnvironmentVariableScope Scope { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public RegistryValueKind Kind { get; set; } = RegistryValueKind.String;

    public string TypeText => Kind == RegistryValueKind.ExpandString ? "可展开" : "字符串";
    public bool IsPath => EnvironmentVariableRules.IsPath(Name);
    public bool IsCritical => EnvironmentVariableRules.IsCritical(Name);

    public EnvironmentVariableEntry Clone() => new()
    {
        Scope = Scope,
        Name = Name,
        Value = Value,
        Kind = Kind
    };
}

public sealed record EnvironmentVariableChange(
    EnvironmentVariableScope Scope,
    string Name,
    string? Value,
    RegistryValueKind Kind,
    bool Delete);

public sealed record EnvironmentVariableApplyResult(
    bool Success,
    string Message,
    IReadOnlyList<string> ChangedNames,
    string? BackupPath = null);

public sealed class EnvironmentVariableBackup
{
    public EnvironmentVariableScope Scope { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<EnvironmentVariableEntry> Values { get; set; } = new();
}

public sealed class EnvironmentVariableApplyPayload
{
    public List<EnvironmentVariableChange> Changes { get; set; } = new();
}

public sealed record EnvironmentVariableElevatedResult(bool Success, string Message);

public static class EnvironmentVariableRules
{
    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Path", "SystemRoot", "windir", "ComSpec", "TEMP", "TMP",
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "PATHEXT", "OS",
        "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "PSModulePath"
    };

    public static bool IsPath(string name) =>
        string.Equals(name, "Path", StringComparison.OrdinalIgnoreCase);

    public static bool IsCritical(string name) => CriticalNames.Contains(name);

    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Length <= 255 &&
        (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    public static string? ValidateChange(EnvironmentVariableChange change)
    {
        if (!IsValidName(change.Name))
            return $"环境变量名“{change.Name}”不合法。";
        if (change.Kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
            return "只支持字符串和可展开字符串类型。";
        if (change.Delete)
        {
            if (IsCritical(change.Name))
                return $"“{change.Name}”是关键系统变量，禁止删除。";
            return null;
        }

        var value = change.Value ?? string.Empty;
        if (value.Length > 32767)
            return $"环境变量“{change.Name}”的值过长。";
        if (IsCritical(change.Name) && string.IsNullOrWhiteSpace(value))
            return $"“{change.Name}”是关键系统变量，不能设置为空。";
        if (IsPath(change.Name))
        {
            var segments = value.Split(';');
            if (segments.Length == 0 || segments.All(string.IsNullOrWhiteSpace))
                return "Path 不能为空。";
            if (segments.Any(string.IsNullOrWhiteSpace))
                return "Path 中存在空路径段；空路径段等价于当前目录，请删除多余的分号。";
        }
        return null;
    }

    public static string? ValidateEntries(IEnumerable<EnvironmentVariableEntry> entries)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!names.Add(entry.Name))
                return $"环境变量名“{entry.Name}”重复（不区分大小写）。";
            var error = ValidateChange(new EnvironmentVariableChange(entry.Scope,
                entry.Name, entry.Value, entry.Kind, Delete: false));
            if (error is not null)
                return error;
        }
        return null;
    }
}

/// <summary>
/// 用户/系统环境变量读写、备份恢复、WM_SETTINGCHANGE 广播与 UAC 提权应用。
/// 不记录变量值，只把变量名与操作写入审计。
/// </summary>
public sealed class EnvironmentVariableService
{
    private const string UserKeyPath = "Environment";
    private const string SystemKeyPath =
        @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly ISecretProtector _protector;
    private readonly string _backupDir;

    public EnvironmentVariableService(string? backupDir = null, ISecretProtector? protector = null)
    {
        _backupDir = backupDir ?? Path.Combine(ConfigPaths.AppDataDir, "env-var-backups");
        _protector = protector ?? new DpapiSecretProtector();
    }

    public string BackupDir => _backupDir;
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public IReadOnlyList<EnvironmentVariableEntry> Read(EnvironmentVariableScope scope)
    {
        using var key = OpenKey(scope, writable: false);
        if (key is null)
            return Array.Empty<EnvironmentVariableEntry>();

        var result = new List<EnvironmentVariableEntry>();
        foreach (var name in key.GetValueNames())
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;
            try
            {
                var kind = key.GetValueKind(name);
                if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
                    continue;
                var value = key.GetValue(name, string.Empty,
                    RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
                result.Add(new EnvironmentVariableEntry
                {
                    Scope = scope,
                    Name = name,
                    Value = value,
                    Kind = kind
                });
            }
            catch
            {
                // 单个值不可读时跳过
            }
        }
        return result.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<EnvironmentVariableApplyResult> ApplyAsync(EnvironmentVariableScope scope,
        IReadOnlyList<EnvironmentVariableChange> changes, CancellationToken ct = default)
    {
        if (changes.Count == 0)
            return new EnvironmentVariableApplyResult(true, "没有需要保存的变更。", Array.Empty<string>());

        foreach (var change in changes)
        {
            var error = EnvironmentVariableRules.ValidateChange(change);
            if (error is not null)
                return new EnvironmentVariableApplyResult(false, error,
                    changes.Select(item => item.Name).ToArray());
        }

        var backupPath = await WriteBackupAsync(scope, ct).ConfigureAwait(false);
        EnvironmentVariableApplyResult result;
        if (scope == EnvironmentVariableScope.System && !IsElevated)
            result = await ApplySystemElevatedAsync(changes, backupPath, ct).ConfigureAwait(false);
        else
        {
            try
            {
                ApplyToRegistry(scope, changes);
                BroadcastEnvironmentChange();
                ApplyToCurrentProcess(changes);
                result = new EnvironmentVariableApplyResult(true,
                    "已保存并通知系统。新启动的程序会使用新值；已运行的程序/终端/服务需要重启。",
                    changes.Select(item => item.Name).ToArray(), backupPath);
            }
            catch (Exception ex)
            {
                result = new EnvironmentVariableApplyResult(false,
                    "写入环境变量失败：" + ex.Message,
                    changes.Select(item => item.Name).ToArray(), backupPath);
            }
        }

        await AuditChangesAsync(scope, changes, result).ConfigureAwait(false);
        return result;
    }

    public async Task<EnvironmentVariableApplyResult> CreateBackupAsync(
        EnvironmentVariableScope scope, CancellationToken ct = default)
    {
        var path = await WriteBackupAsync(scope, ct).ConfigureAwait(false);
        await LocalAudit.LogAsync("env_var_backup",
            $"scope={scope}", CommandStatus.Executed,
            result: path is null ? "备份失败" : "已备份环境变量").ConfigureAwait(false);
        return path is null
            ? new EnvironmentVariableApplyResult(false, "备份失败。", Array.Empty<string>())
            : new EnvironmentVariableApplyResult(true, "已备份当前环境变量。", Array.Empty<string>(), path);
    }

    public async Task<EnvironmentVariableApplyResult> RestoreLatestAsync(
        EnvironmentVariableScope scope, CancellationToken ct = default)
    {
        var backup = await ReadLatestBackupAsync(scope, ct).ConfigureAwait(false);
        if (backup is null)
            return new EnvironmentVariableApplyResult(false, "没有可恢复的备份。", Array.Empty<string>());

        var current = Read(scope);
        var backupNames = backup.Values.Select(entry => entry.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changes = new List<EnvironmentVariableChange>();
        foreach (var entry in backup.Values)
        {
            changes.Add(new EnvironmentVariableChange(scope, entry.Name, entry.Value, entry.Kind, false));
        }
        foreach (var entry in current.Where(entry => !backupNames.Contains(entry.Name)))
        {
            if (EnvironmentVariableRules.IsCritical(entry.Name))
                continue;
            changes.Add(new EnvironmentVariableChange(scope, entry.Name, null,
                RegistryValueKind.String, true));
        }

        var result = await ApplyAsync(scope, changes, ct).ConfigureAwait(false);
        await LocalAudit.LogAsync("env_var_restore",
            $"scope={scope}", result.Success ? CommandStatus.Executed : CommandStatus.Failed,
            result: result.Message).ConfigureAwait(false);
        return result;
    }

    public async Task<bool> UnlockSystemAsync(CancellationToken ct = default)
    {
        if (IsElevated)
            return true;

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            return false;

        try
        {
            using var process = NativeShell.Start(new ProcessStartInfo(executable,
                "--environment-elevation-check")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null)
                return false;
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static bool BroadcastEnvironmentChange()
    {
        try
        {
            SendMessageTimeout(new IntPtr(0xffff), WmSettingChange, IntPtr.Zero,
                "Environment", SmtoAbortIfHung, 5000, out _);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>供提权助手使用：直接写 HKLM 并广播，不备份、不审计。</summary>
    public static EnvironmentVariableElevatedResult ApplySystemChangesDirect(
        IReadOnlyList<EnvironmentVariableChange> changes)
    {
        try
        {
            foreach (var change in changes)
            {
                var error = EnvironmentVariableRules.ValidateChange(change);
                if (error is not null)
                    return new EnvironmentVariableElevatedResult(false, error);
            }
            ApplyToRegistry(EnvironmentVariableScope.System, changes);
            BroadcastEnvironmentChange();
            return new EnvironmentVariableElevatedResult(true, "系统环境变量已更新。");
        }
        catch (Exception ex)
        {
            return new EnvironmentVariableElevatedResult(false, "写入系统环境变量失败：" + ex.Message);
        }
    }

    public static string? ReadElevatedPayload(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var protector = new DpapiSecretProtector();
            return protector.Unprotect(text);
        }
        catch
        {
            return null;
        }
    }

    private async Task<EnvironmentVariableApplyResult> ApplySystemElevatedAsync(
        IReadOnlyList<EnvironmentVariableChange> changes, string? backupPath, CancellationToken ct)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            return new EnvironmentVariableApplyResult(false, "无法定位 App 可执行文件。",
                changes.Select(item => item.Name).ToArray(), backupPath);

        var temp = Path.Combine(Path.GetTempPath(),
            "litssh-env-" + Guid.NewGuid().ToString("N") + ".tmp");
        var resultPath = temp + ".result.json";
        try
        {
            var payload = JsonSerializer.Serialize(new EnvironmentVariableApplyPayload
            {
                Changes = changes.ToList()
            }, JsonOptions);
            await File.WriteAllTextAsync(temp, _protector.Protect(payload) ?? string.Empty, ct)
                .ConfigureAwait(false);

            using var process = NativeShell.Start(new ProcessStartInfo(executable,
                $"--apply-environment \"{temp}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null)
                return new EnvironmentVariableApplyResult(false, "无法启动管理员进程。",
                    changes.Select(item => item.Name).ToArray(), backupPath);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            EnvironmentVariableElevatedResult? elevatedResult = null;
            if (File.Exists(resultPath))
            {
                try
                {
                    var resultJson = await File.ReadAllTextAsync(resultPath, ct).ConfigureAwait(false);
                    elevatedResult = JsonSerializer.Deserialize<EnvironmentVariableElevatedResult>(
                        resultJson, JsonOptions);
                }
                catch
                {
                    // 结果文件损坏时退回退出码判断
                }
            }

            if (elevatedResult is { Success: false })
                return new EnvironmentVariableApplyResult(false, elevatedResult.Message,
                    changes.Select(item => item.Name).ToArray(), backupPath);
            if (process.ExitCode != 0 && elevatedResult is null)
                return new EnvironmentVariableApplyResult(false, "管理员进程返回失败。",
                    changes.Select(item => item.Name).ToArray(), backupPath);

            var message = elevatedResult?.Message ??
                          "系统环境变量已更新。新启动的程序会使用新值；已运行的程序/服务需要重启。";
            ApplyToCurrentProcess(changes);
            BroadcastEnvironmentChange();
            return new EnvironmentVariableApplyResult(true, message,
                changes.Select(item => item.Name).ToArray(), backupPath);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new EnvironmentVariableApplyResult(false, "用户取消了管理员授权。",
                changes.Select(item => item.Name).ToArray(), backupPath);
        }
        catch (OperationCanceledException)
        {
            return new EnvironmentVariableApplyResult(false, "操作已取消。",
                changes.Select(item => item.Name).ToArray(), backupPath);
        }
        catch (Exception ex)
        {
            return new EnvironmentVariableApplyResult(false,
                "应用系统环境变量失败：" + ex.Message,
                changes.Select(item => item.Name).ToArray(), backupPath);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* ignore */ }
            try { File.Delete(resultPath); } catch { /* ignore */ }
        }
    }

    private async Task<string?> WriteBackupAsync(EnvironmentVariableScope scope,
        CancellationToken ct)
    {
        try
        {
            var backup = new EnvironmentVariableBackup
            {
                Scope = scope,
                CreatedAt = DateTimeOffset.Now,
                Values = Read(scope).Select(entry => entry.Clone()).ToList()
            };
            Directory.CreateDirectory(_backupDir);
            var name = $"{scope.ToString().ToLowerInvariant()}-" +
                       $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.envbak";
            var path = Path.Combine(_backupDir, name);
            var json = JsonSerializer.Serialize(backup, JsonOptions);
            await File.WriteAllTextAsync(path, _protector.Protect(json) ?? string.Empty, ct)
                .ConfigureAwait(false);
            foreach (var old in Directory.EnumerateFiles(_backupDir,
                         $"{scope.ToString().ToLowerInvariant()}-*.envbak")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(20))
            {
                try { File.Delete(old); } catch { /* ignore */ }
            }
            return path;
        }
        catch
        {
            return null;
        }
    }

    private async Task<EnvironmentVariableBackup?> ReadLatestBackupAsync(
        EnvironmentVariableScope scope, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_backupDir);
            var path = Directory.EnumerateFiles(_backupDir,
                    $"{scope.ToString().ToLowerInvariant()}-*.envbak")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (path is null)
                return null;
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var json = _protector.Unprotect(text);
            if (string.IsNullOrWhiteSpace(json))
                return null;
            return JsonSerializer.Deserialize<EnvironmentVariableBackup>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static RegistryKey? OpenKey(EnvironmentVariableScope scope, bool writable)
    {
        if (scope == EnvironmentVariableScope.User)
        {
            return writable
                ? Registry.CurrentUser.CreateSubKey(UserKeyPath, writable: true)
                : Registry.CurrentUser.OpenSubKey(UserKeyPath, writable: false);
        }

        return Registry.LocalMachine.OpenSubKey(SystemKeyPath, writable);
    }

    private static void ApplyToRegistry(EnvironmentVariableScope scope,
        IReadOnlyList<EnvironmentVariableChange> changes)
    {
        using var key = OpenKey(scope, writable: true)
            ?? throw new InvalidOperationException("无法打开环境变量注册表项。");
        foreach (var change in changes)
        {
            if (change.Delete)
            {
                key.DeleteValue(change.Name, throwOnMissingValue: false);
                continue;
            }
            key.SetValue(change.Name, change.Value ?? string.Empty,
                change.Kind == RegistryValueKind.ExpandString
                    ? RegistryValueKind.ExpandString
                    : RegistryValueKind.String);
        }
    }

    private static void ApplyToCurrentProcess(IReadOnlyList<EnvironmentVariableChange> changes)
    {
        foreach (var change in changes)
        {
            try
            {
                var value = change.Delete
                    ? null
                    : change.Kind == RegistryValueKind.ExpandString
                        ? Environment.ExpandEnvironmentVariables(change.Value ?? string.Empty)
                        : change.Value;
                Environment.SetEnvironmentVariable(change.Name, value,
                    EnvironmentVariableTarget.Process);
            }
            catch
            {
                // 单个变量更新失败不影响注册表已经写入
            }
        }
    }

    private static async Task AuditChangesAsync(EnvironmentVariableScope scope,
        IReadOnlyList<EnvironmentVariableChange> changes, EnvironmentVariableApplyResult result)
    {
        foreach (var change in changes)
        {
            var action = change.Delete ? "delete" : "set";
            await LocalAudit.LogAsync("env_var_" + action,
                $"scope={scope}; name={change.Name}",
                result.Success ? CommandStatus.Executed : CommandStatus.Failed,
                result: result.Success ? "环境变量已更新" : result.Message).ConfigureAwait(false);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam,
        string lParam, uint flags, uint timeout, out IntPtr result);
}
