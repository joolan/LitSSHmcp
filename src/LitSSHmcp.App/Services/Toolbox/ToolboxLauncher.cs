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

    private ToolboxLaunchResult LaunchScript(ToolboxItem item)
    {
        var target = item.Target.Trim();
        if (string.IsNullOrWhiteSpace(target))
            return new ToolboxLaunchResult(false, "脚本内容为空。");

        var shell = string.Equals(item.ScriptShell, "powershell", StringComparison.OrdinalIgnoreCase)
            ? "powershell"
            : "cmd";
        var preview = $"{item.Name}\n\n脚本类型：{shell}\n目标：{target}\n参数：{item.Arguments}\n工作目录：{item.WorkingDirectory}";
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
                var command = isFile ? $"\"{target}\"" : target;
                var args = "/c " + command;
                if (!string.IsNullOrWhiteSpace(item.Arguments))
                    args += " " + item.Arguments;
                info = new ProcessStartInfo("cmd.exe", args);
            }

            info.UseShellExecute = true;
            info.CreateNoWindow = false;
            if (!string.IsNullOrWhiteSpace(item.WorkingDirectory) && Directory.Exists(item.WorkingDirectory))
                info.WorkingDirectory = item.WorkingDirectory;
            if (item.RunAsAdmin)
                info.Verb = "runas";

            NativeShell.Start(info);
            return new ToolboxLaunchResult(true, "已执行脚本。");
        }
        catch (Exception ex)
        {
            return new ToolboxLaunchResult(false, "执行脚本失败：" + ex.Message);
        }
    }
}
