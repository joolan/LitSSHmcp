using System.IO;
using System.Text;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Scheduling;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.App.ViewModels;

public sealed partial class ScheduledTasksViewModel
{
    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        var previousId = SelectedTask?.Id;
        StatusMessage = "正在读取计划任务…";
        try
        {
            var direct = await _ssh.ExecuteCommandAsync(_server, ScheduledTasksProbeCommands.Direct,
                timeoutSeconds: 60);
            if (!direct.Success && string.IsNullOrWhiteSpace(direct.Output))
            {
                StatusMessage = "读取失败：" + (string.IsNullOrWhiteSpace(direct.Error) ? "无输出" : direct.Error);
                return;
            }

            var includeElevated = _data?.SudoSucceeded == true;
            string? sudoOutput = null;
            var sudoError = string.Empty;
            if (includeElevated)
            {
                var elevated = await _ssh.ExecuteWithSudoAsync(_server, ScheduledTasksProbeCommands.Sudo);
                if (elevated.Success)
                {
                    sudoOutput = elevated.Output;
                }
                else
                {
                    sudoError = string.IsNullOrWhiteSpace(elevated.Error) ? elevated.Output : elevated.Error;
                }
            }

            var data = ScheduledTasksProbeParser.Parse(direct.Output, sudoOutput);
            UpdateHeader(data, sudoError, includeElevated);
            ApplyData(data, previousId);
            StatusMessage = $"共 {Tasks.Count} 项 · 更新于 {DateTime.Now:HH:mm:ss}" +
                            (data.Warnings.Count > 0 ? $" · {data.Warnings.Count} 条提示" : string.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = "读取失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoadElevatedAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (_server.SudoType == SudoType.None)
        {
            _dialogs.Warn("当前服务器未配置提权，无法读取 root/其他用户任务。", "计划任务");
            return;
        }

        IsBusy = true;
        var previousId = SelectedTask?.Id;
        StatusMessage = "正在提权读取 root/其他用户任务…";
        try
        {
            var direct = await _ssh.ExecuteCommandAsync(_server, ScheduledTasksProbeCommands.Direct,
                timeoutSeconds: 60);
            var elevated = await _ssh.ExecuteWithSudoAsync(_server, ScheduledTasksProbeCommands.Sudo);
            var sudoError = elevated.Success
                ? string.Empty
                : string.IsNullOrWhiteSpace(elevated.Error) ? elevated.Output : elevated.Error;
            var data = ScheduledTasksProbeParser.Parse(direct.Output,
                elevated.Success ? elevated.Output : null);
            UpdateHeader(data, sudoError, sudoAttempted: true);
            ApplyData(data, previousId);
            StatusMessage = elevated.Success
                ? $"已提权加载：{data.ElevatedUser} · 共 {Tasks.Count} 项 · 更新于 {DateTime.Now:HH:mm:ss}"
                : "提权失败：" + (string.IsNullOrWhiteSpace(sudoError) ? "无输出" : sudoError.Trim());
        }
        catch (Exception ex)
        {
            StatusMessage = "提权读取失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyData(ScheduledTasksProbeData data, string? previousId)
    {
        _data = data;
        Tasks.Clear();
        foreach (var task in data.BuildTasks())
        {
            Tasks.Add(task);
        }

        TasksView.Refresh();
        SelectedTask = Tasks.FirstOrDefault(t => string.Equals(t.Id, previousId, StringComparison.Ordinal))
                       ?? Tasks.FirstOrDefault();
    }

    private void UpdateHeader(ScheduledTasksProbeData data, string sudoError, bool sudoAttempted)
    {
        var os = string.IsNullOrWhiteSpace(data.OsName) ? data.Meta.GetValueOrDefault("uname", "Linux") : data.OsName;
        var host = string.IsNullOrWhiteSpace(data.HostName) ? _server.Host : data.HostName;
        var kernel = string.IsNullOrWhiteSpace(data.Kernel) ? string.Empty : $" · 内核 {data.Kernel}";
        ServerInfoText = $"{os} · {host}{kernel} · 当前用户 {data.CurrentUser}";

        var cron = data.CronState switch
        {
            "active" => "cron 运行中",
            "inactive" => "cron 未运行",
            "" => "cron 状态未知",
            _ => $"cron {data.CronState}"
        };
        var crontab = string.IsNullOrWhiteSpace(data.CrontabPath) ? "未安装 crontab" : "crontab 可用";
        var systemd = string.IsNullOrWhiteSpace(data.SystemctlPath) ? "无 systemd" : "systemd 可用";
        CronServiceText = $"{cron} · {crontab} · {systemd}";

        SudoText = _server.SudoType == SudoType.None
            ? string.Equals(data.CurrentUser, "root", StringComparison.Ordinal)
                ? "当前为 root 用户，可直接管理 root/系统任务"
                : "未配置提权：只能管理当前用户任务"
            : !sudoAttempted
                ? "尚未加载 root/其他用户任务，可点击“加载提权任务”"
                : data.SudoSucceeded
                    ? $"已提权：{data.ElevatedUser}，root/其他用户任务已加载"
                    : "提权失败：" + (string.IsNullOrWhiteSpace(sudoError) ? "无法读取 root/其他用户任务" : sudoError.Trim());

        OnPropertyChanged(nameof(ServerInfoText));
        OnPropertyChanged(nameof(CronServiceText));
        OnPropertyChanged(nameof(SudoText));
    }

    private async Task<CommandResult> ExecuteAsync(string command, int timeoutSeconds = 60, CancellationToken ct = default)
    {
        var direct = await _ssh.ExecuteCommandAsync(_server, command, ct, timeoutSeconds: timeoutSeconds);
        if (direct.Success || _server.SudoType == SudoType.None || !LooksLikePermissionDenied(direct))
        {
            return direct;
        }

        return await _ssh.ExecuteWithSudoAsync(_server, command, ct);
    }

    private async Task<CommandResult> ReadRemoteTextAsync(string command, int timeoutSeconds = 30)
    {
        var result = await ExecuteAsync(command, timeoutSeconds);
        if (!result.Success && IsNoCrontabMessage(result.Output + "\n" + result.Error))
        {
            return new CommandResult { Success = true, ExitCode = 0 };
        }

        return result;
    }

    private async Task<(bool Success, string Text, string Error)> ReadFileAsync(string path)
    {
        var quoted = ShellQuote.Single(path);
        var result = await ReadRemoteTextAsync($"cat -- {quoted} 2>&1");
        return (result.Success, result.Output, string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
    }

    private async Task<bool> WriteFileAsync(string path, string text)
    {
        var temp = await UploadTempTextAsync(text);
        if (temp is null)
        {
            return false;
        }

        try
        {
            var command = $"cat -- {ShellQuote.Single(temp)} > {ShellQuote.Single(path)}";
            var result = await ExecuteAsync(command, 60);
            return result.Success;
        }
        finally
        {
            await DeleteRemoteTempAsync(temp);
        }
    }

    private async Task<bool> WriteCrontabAsync(string owner, string text)
    {
        var temp = await UploadTempTextAsync(text);
        if (temp is null)
        {
            return false;
        }

        try
        {
            var command = IsCurrentUser(owner)
                ? $"crontab {ShellQuote.Single(temp)} 2>&1"
                : $"crontab -u {ShellQuote.Single(owner)} {ShellQuote.Single(temp)} 2>&1";
            var result = IsCurrentUser(owner)
                ? await ExecuteAsync(command, 60)
                : await _ssh.ExecuteWithSudoAsync(_server, command);
            return result.Success;
        }
        finally
        {
            await DeleteRemoteTempAsync(temp);
        }
    }

    private async Task<(bool Success, string Text, string Error)> ReadCrontabAsync(string owner)
    {
        var command = "crontab -l 2>&1";
        var result = IsCurrentUser(owner)
            ? await ReadRemoteTextAsync(command, 30)
            : await _ssh.ExecuteWithSudoAsync(_server, $"crontab -u {ShellQuote.Single(owner)} -l 2>&1");
        if (!result.Success && IsNoCrontabMessage(result.Output + "\n" + result.Error))
        {
            return (true, string.Empty, string.Empty);
        }

        return (result.Success, result.Output, string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
    }

    private async Task<string?> UploadTempTextAsync(string text)
    {
        var local = Path.Combine(Path.GetTempPath(), "litssh-sched-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(local, text, new UTF8Encoding(false));
            var remote = "/tmp/litssh-sched-" + Guid.NewGuid().ToString("N") + ".tmp";
            var result = await _ssh.UploadFileAsync(_server, local, remote);
            return result.Success ? remote : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { File.Delete(local); } catch { }
        }
    }

    private async Task DeleteRemoteTempAsync(string remote)
    {
        try
        {
            await _ssh.ExecuteCommandAsync(_server, $"rm -f -- {ShellQuote.Single(remote)}");
        }
        catch
        {
        }
    }

    private bool IsCurrentUser(string owner)
        => string.Equals(owner, _data?.CurrentUser, StringComparison.Ordinal);

    private static bool LooksLikePermissionDenied(CommandResult result)
    {
        var text = (result.Error + "\n" + result.Output).ToLowerInvariant();
        return text.Contains("permission denied", StringComparison.Ordinal) ||
               text.Contains("operation not permitted", StringComparison.Ordinal) ||
               text.Contains("must be root", StringComparison.Ordinal) ||
               text.Contains("not in the sudoers", StringComparison.Ordinal) ||
               text.Contains("authentication is required", StringComparison.Ordinal) ||
               text.Contains("a password is required", StringComparison.Ordinal);
    }

    private static bool IsNoCrontabMessage(string text)
        => text.Contains("no crontab for", StringComparison.OrdinalIgnoreCase);
}
