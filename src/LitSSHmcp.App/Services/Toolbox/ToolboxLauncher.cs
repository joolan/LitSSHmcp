using System.Diagnostics;
using LitSSHmcp.App.Services;
using System.IO;
using System.Text;

namespace LitSSHmcp.App.Services.Toolbox;

public sealed record ToolboxLaunchResult(bool Success, string Message);

/// <summary>工具箱启动器：程序、文件、文件夹、URL 与脚本片段。</summary>
public sealed class ToolboxLauncher
{
    private readonly IUserDialogService _dialogs;

    public ToolboxLauncher(IUserDialogService dialogs) => _dialogs = dialogs;

    public Task<ToolboxLaunchResult> LaunchAsync(ToolboxItem item)
    {
        if (item.Type == ToolboxItemType.Builtin)
            return Task.FromResult(new ToolboxLaunchResult(false, "内置工具由工具箱界面打开。"));

        if (item.Type == ToolboxItemType.Url)
            return Task.FromResult(LaunchUrl(item));

        if (item.Type == ToolboxItemType.Script)
            return Task.FromResult(LaunchScript(item));

        return Task.FromResult(LaunchPath(item));
    }

    private ToolboxLaunchResult LaunchUrl(ToolboxItem item)
    {
        if (!Uri.TryCreate(item.Target, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "mailto"))
            return new ToolboxLaunchResult(false, "URL 无效，仅支持 http / https / mailto。");

        try
        {
            NativeShell.Start(new ProcessStartInfo
            {
                FileName = item.Target,
                UseShellExecute = true
            });
            return new ToolboxLaunchResult(true, "已打开 URL。");
        }
        catch (Exception ex)
        {
            return new ToolboxLaunchResult(false, "打开 URL 失败：" + ex.Message);
        }
    }

    private ToolboxLaunchResult LaunchPath(ToolboxItem item)
    {
        var target = item.Target.Trim();
        if (string.IsNullOrWhiteSpace(target))
            return new ToolboxLaunchResult(false, "目标路径为空。");

        if (item.Type is ToolboxItemType.Application or ToolboxItemType.File &&
            !File.Exists(target))
            return new ToolboxLaunchResult(false, "目标文件不存在：" + target);

        if (item.Type == ToolboxItemType.Folder && !Directory.Exists(target))
            return new ToolboxLaunchResult(false, "目标文件夹不存在：" + target);

        var environment = GetEnvironmentVariables(item);
        if (environment.Count > 0)
            return LaunchPathWithEnvironment(item, target, environment);

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = target,
                Arguments = item.Arguments ?? string.Empty,
                UseShellExecute = true
            };
            if (!string.IsNullOrWhiteSpace(item.WorkingDirectory) && Directory.Exists(item.WorkingDirectory))
                info.WorkingDirectory = item.WorkingDirectory;
            if (item.RunAsAdmin)
                info.Verb = "runas";

            NativeShell.Start(info);
            return new ToolboxLaunchResult(true, "已启动。");
        }
        catch (Exception ex)
        {
            return new ToolboxLaunchResult(false, "启动失败：" + ex.Message);
        }
    }

    private ToolboxLaunchResult LaunchPathWithEnvironment(ToolboxItem item, string target,
        IReadOnlyList<ToolboxEnvironmentVariable> environment)
    {
        if (item.Type != ToolboxItemType.Application)
            return new ToolboxLaunchResult(false, "只有“程序”和“脚本”支持环境变量。");
        if (item.RunAsAdmin)
            return new ToolboxLaunchResult(false,
                "“以管理员身份运行”暂不支持按工具注入环境变量；请关闭管理员运行，或删除环境变量。");
        if (!TryValidateEnvironment(environment, out var error))
            return new ToolboxLaunchResult(false, error);

        var extension = Path.GetExtension(target).ToLowerInvariant();
        ProcessStartInfo info;
        if (extension is ".exe" or ".com")
        {
            info = new ProcessStartInfo(target)
            {
                Arguments = item.Arguments ?? string.Empty,
                UseShellExecute = false,
                CreateNoWindow = false
            };
        }
        else if (extension is ".bat" or ".cmd")
        {
            var args = "/c " + Quote(target);
            if (!string.IsNullOrWhiteSpace(item.Arguments))
                args += " " + item.Arguments;
            info = new ProcessStartInfo("cmd.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = false
            };
        }
        else if (extension == ".ps1")
        {
            var args = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(target);
            if (!string.IsNullOrWhiteSpace(item.Arguments))
                args += " " + item.Arguments;
            info = new ProcessStartInfo("powershell.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = false
            };
        }
        else
        {
            return new ToolboxLaunchResult(false,
                $"目标类型“{extension}”暂不支持环境变量；请使用 .exe / .bat / .cmd / .ps1，或关闭环境变量。");
        }

        if (!string.IsNullOrWhiteSpace(item.WorkingDirectory) && Directory.Exists(item.WorkingDirectory))
            info.WorkingDirectory = item.WorkingDirectory;
        ApplyEnvironment(info, environment);

        try
        {
            NativeShell.Start(info);
            return new ToolboxLaunchResult(true, "已启动。");
        }
        catch (Exception ex)
        {
            return new ToolboxLaunchResult(false, "启动失败：" + ex.Message);
        }
    }

    private ToolboxLaunchResult LaunchScript(ToolboxItem item)
    {
        var target = item.Target.Trim();
        if (string.IsNullOrWhiteSpace(target))
            return new ToolboxLaunchResult(false, "脚本内容为空。");

        var environment = GetEnvironmentVariables(item);
        if (item.RunAsAdmin && environment.Count > 0)
            return new ToolboxLaunchResult(false,
                "“以管理员身份运行”暂不支持按工具注入环境变量；请关闭管理员运行，或删除环境变量。");
        if (!TryValidateEnvironment(environment, out var error))
            return new ToolboxLaunchResult(false, error);

        var shell = string.Equals(item.ScriptShell, "powershell", StringComparison.OrdinalIgnoreCase)
            ? "powershell"
            : "cmd";
        var envPreview = environment.Count == 0
            ? "无"
            : string.Join(", ", environment.Select(variable => variable.Name.Trim()));
        var preview = $"{item.Name}\n\n脚本类型：{shell}\n目标：{target}\n参数：{item.Arguments}\n" +
                      $"工作目录：{item.WorkingDirectory}\n环境变量：{envPreview}（值已隐藏）";
        if (!_dialogs.Confirm(
                "即将执行脚本，请确认内容安全：\n\n" + preview,
                "执行脚本",
                warning: true,
                defaultNo: true))
            return new ToolboxLaunchResult(false, "已取消执行。");

        try
        {
            var isFile = File.Exists(target);
            ProcessStartInfo info;
            if (shell == "powershell")
            {
                if (isFile)
                {
                    var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{target}\"";
                    if (!string.IsNullOrWhiteSpace(item.Arguments))
                        args += " " + item.Arguments;
                    info = new ProcessStartInfo("powershell.exe", args);
                }
                else
                {
                    var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(target));
                    info = new ProcessStartInfo("powershell.exe",
                        $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}");
                }
            }
            else
            {
                var command = isFile ? Quote(target) : target;
                var args = "/c " + command;
                if (!string.IsNullOrWhiteSpace(item.Arguments))
                    args += " " + item.Arguments;
                info = new ProcessStartInfo("cmd.exe", args);
            }

            // 无环境变量时保留原有 ShellExecute 行为；有环境变量时必须用 CreateProcess。
            info.UseShellExecute = environment.Count == 0;
            info.CreateNoWindow = false;
            if (!string.IsNullOrWhiteSpace(item.WorkingDirectory) && Directory.Exists(item.WorkingDirectory))
                info.WorkingDirectory = item.WorkingDirectory;
            if (item.RunAsAdmin)
                info.Verb = "runas";
            if (environment.Count > 0)
                ApplyEnvironment(info, environment);

            NativeShell.Start(info);
            return new ToolboxLaunchResult(true, "已执行脚本。");
        }
        catch (Exception ex)
        {
            return new ToolboxLaunchResult(false, "执行脚本失败：" + ex.Message);
        }
    }

    private static List<ToolboxEnvironmentVariable> GetEnvironmentVariables(ToolboxItem item) =>
        (item.EnvironmentVariables ?? new List<ToolboxEnvironmentVariable>())
            .Where(variable => !string.IsNullOrWhiteSpace(variable.Name))
            .ToList();

    private static void ApplyEnvironment(ProcessStartInfo info,
        IReadOnlyList<ToolboxEnvironmentVariable> environment)
    {
        foreach (var variable in environment)
            info.Environment[variable.Name.Trim()] = variable.Value ?? string.Empty;
    }

    private static bool TryValidateEnvironment(
        IReadOnlyList<ToolboxEnvironmentVariable> environment, out string error)
    {
        error = string.Empty;
        if (environment.Count > 128)
        {
            error = "环境变量数量不能超过 128 个。";
            return false;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in environment)
        {
            var name = variable.Name?.Trim() ?? string.Empty;
            if (!ToolboxEnvironmentVariable.IsValidName(name))
            {
                error = $"环境变量名“{name}”不合法；请使用字母、数字、下划线，且不能以数字开头。";
                return false;
            }
            if (!names.Add(name))
            {
                error = $"环境变量名“{name}”重复（不区分大小写）。";
                return false;
            }
            if ((variable.Value?.Length ?? 0) > 16 * 1024)
            {
                error = $"环境变量“{name}”的值超过 16 KB。";
                return false;
            }
        }
        return true;
    }

    private static string Quote(string value) => "\"" + value + "\"";
}
