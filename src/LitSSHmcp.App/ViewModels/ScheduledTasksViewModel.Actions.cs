using System.Globalization;
using System.Text;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Scheduling;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.App.ViewModels;

public sealed partial class ScheduledTasksViewModel
{
    private async Task TogglePauseAsync()
    {
        var task = SelectedTask;
        if (task is null || IsActionBusy)
        {
            return;
        }

        var pause = !task.IsPaused;
        var action = pause ? "暂停" : "启用";
        var confirm = _dialogs.Confirm(
            $"确认{action}该计划任务？\n\n来源：{task.SourceDisplay}\n用户：{task.Owner}\n" +
            $"计划：{task.ScheduleDescription}\n命令：{task.Command}\n\n{PauseResumeHint}",
            "计划任务",
            warning: pause);
        if (!confirm)
        {
            return;
        }

        IsActionBusy = true;
        StatusMessage = $"{action}中…";
        try
        {
            var result = task.Kind switch
            {
                ScheduledTaskKind.SystemdTimer => await ToggleSystemdAsync(task, pause),
                ScheduledTaskKind.CronDirectory => await ToggleCronDirectoryAsync(task, pause),
                _ => await ToggleCronAsync(task, pause)
            };

            StatusMessage = result.Success
                ? $"{action}成功：{task.SourceDisplay}"
                : $"{action}失败：{result.Message}";
            if (result.Success)
            {
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"{action}失败：{ex.Message}";
        }
        finally
        {
            IsActionBusy = false;
        }
    }

    private async Task<(bool Success, string Message)> ToggleCronAsync(ScheduledTaskInfo task, bool pause)
    {
        var read = task.Kind == ScheduledTaskKind.UserCrontab
            ? await ReadCrontabAsync(task.Owner)
            : await ReadFileAsync(task.SourcePath);
        if (!read.Success)
        {
            return (false, read.Error);
        }

        string updated;
        try
        {
            updated = CrontabParser.SetPaused(read.Text, task.LineNumber - 1, task.RawLine, pause);
        }
        catch (InvalidOperationException ex)
        {
            return (false, ex.Message);
        }

        var backup = WriteLocalTextBackup(task.SourcePath, read.Text);
        var written = task.Kind == ScheduledTaskKind.UserCrontab
            ? await WriteCrontabAsync(task.Owner, updated)
            : await WriteFileAsync(task.SourcePath, updated);
        if (!written)
        {
            return (false, "写入服务器失败");
        }

        var verify = task.Kind == ScheduledTaskKind.UserCrontab
            ? await ReadCrontabAsync(task.Owner)
            : await ReadFileAsync(task.SourcePath);
        if (!verify.Success)
        {
            return (false, "写入后读取失败：" + verify.Error);
        }

        var entries = CrontabParser.Parse(verify.Text,
            task.Kind != ScheduledTaskKind.UserCrontab, task.Owner);
        var entry = entries.FirstOrDefault(e => e.LineIndex == task.LineNumber - 1);
        if (entry is null || entry.IsPaused != pause)
        {
            return (false, "写入后校验失败，文件可能被其他程序修改");
        }

        await AuditAsync(pause ? "scheduled_task_pause" : "scheduled_task_resume",
            task.RawLine, true,
            $"来源 {task.SourceDisplay}" + (backup.Length > 0 ? $"；备份 {backup}" : string.Empty),
            task.SourcePath);
        return (true, "ok");
    }

    private async Task<(bool Success, string Message)> ToggleCronDirectoryAsync(ScheduledTaskInfo task, bool pause)
    {
        var command = $"chmod {(pause ? "-x" : "+x")} -- {ShellQuote.Single(task.SourcePath)} 2>&1";
        var result = await ExecuteAsync(command, 30);
        if (!result.Success)
        {
            return (false, string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
        }

        var check = await ExecuteAsync($"test -x {ShellQuote.Single(task.SourcePath)}; echo $?", 30);
        var isExecutable = check.Output.Trim().EndsWith('0');
        if (isExecutable == pause)
        {
            return (false, "文件权限校验失败");
        }

        await AuditAsync(pause ? "scheduled_task_pause" : "scheduled_task_resume",
            command, true, task.SourcePath, task.SourcePath);
        return (true, "ok");
    }

    private async Task<(bool Success, string Message)> ToggleSystemdAsync(ScheduledTaskInfo task, bool pause)
    {
        var unit = task.UnitName;
        if (string.IsNullOrWhiteSpace(unit))
        {
            return (false, "缺少 systemd 单元名");
        }

        if (!pause && string.Equals(task.UnitFileState, "masked", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteAsync($"systemctl unmask {ShellQuote.Single(unit)} 2>&1", 30);
        }

        var command = pause
            ? $"systemctl disable --now {ShellQuote.Single(unit)} 2>&1"
            : $"systemctl enable --now {ShellQuote.Single(unit)} 2>&1";
        var result = await ExecuteAsync(command, 60);
        if (!result.Success)
        {
            return (false, string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
        }

        await AuditAsync(pause ? "scheduled_task_pause" : "scheduled_task_resume",
            command, true, unit, task.FragmentPath);
        return (true, "ok");
    }

    private async Task RunNowAsync()
    {
        var task = SelectedTask;
        if (task is null || IsActionBusy)
        {
            return;
        }

        var target = task.IsSystemd
            ? $"{task.ActivatedUnit ?? task.UnitName}（systemd service）"
            : task.Command;
        var confirm = _dialogs.Confirm(
            $"确认立即执行该计划任务？\n\n来源：{task.SourceDisplay}\n用户：{task.Owner}\n命令：{target}\n\n" +
            "手动执行环境与 cron 不完全一致，请确认不会造成意外影响。",
            "立即执行计划任务",
            warning: true);
        if (!confirm)
        {
            return;
        }

        IsActionBusy = true;
        StatusMessage = "执行中…";
        try
        {
            CommandResult result;
            if (task.IsSystemd)
            {
                var unit = string.IsNullOrWhiteSpace(task.ActivatedUnit) ? task.UnitName : task.ActivatedUnit;
                if (string.IsNullOrWhiteSpace(unit))
                {
                    StatusMessage = "执行失败：缺少 systemd 单元名";
                    return;
                }

                result = await ExecuteAsync($"systemctl start {ShellQuote.Single(unit)} 2>&1", 300);
            }
            else
            {
                result = await RunCronCommandAsync(task);
            }

            var message = FormatRunOutput(result);
            ShowResultRequested?.Invoke(
                "计划任务执行结果",
                result.Success ? "执行完成" : "执行失败",
                message,
                !result.Success);
            await AuditAsync("scheduled_task_run", target, result.Success, Truncate(message, 4000));
            StatusMessage = result.Success ? "执行完成" : "执行失败";
        }
        catch (Exception ex)
        {
            StatusMessage = "执行失败：" + ex.Message;
        }
        finally
        {
            IsActionBusy = false;
        }
    }

    private async Task<CommandResult> RunCronCommandAsync(ScheduledTaskInfo task)
    {
        var inner = $"cd \"$HOME\" 2>/dev/null || cd /; " +
                    "env -i HOME=\"$HOME\" LOGNAME=\"$(id -un)\" USER=\"$(id -un)\" SHELL=/bin/sh PATH=/usr/bin:/bin " +
                    $"/bin/sh -c {ShellQuote.Single(task.Command)}";

        if (IsCurrentUser(task.Owner))
        {
            return await _ssh.ExecuteCommandAsync(_server, inner, timeoutSeconds: 300);
        }

        if (_server.SudoType == SudoType.None)
        {
            return new CommandResult
            {
                Success = false,
                Error = $"当前未配置提权，无法以用户 {task.Owner} 执行"
            };
        }

        var su = $"su -s /bin/sh -c {ShellQuote.Single(inner)} {ShellQuote.Single(task.Owner)}";
        return await _ssh.ExecuteWithSudoAsync(_server, su);
    }

    public async Task<string?> BuildAiPromptAsync()
    {
        var task = SelectedTask;
        if (task is null)
        {
            return null;
        }

        if (task.ScriptPath is not null && _scriptContent is null)
        {
            await LoadSelectedScriptAsync(force: true);
        }

        if (!ReferenceEquals(task, SelectedTask))
        {
            return null;
        }

        var scriptContent = _scriptContent;
        var scriptMeta = ScriptMetaText;
        var scriptInterpreter = ScriptInterpreterText;
        var scriptIsBinary = _scriptIsBinary;

        var data = _data;
        var meta = data?.Meta;
        var builder = new StringBuilder();
        builder.AppendLine("请分析以下 Linux 服务器上的计划任务，目标是评估其实际功能与风险。允许并鼓励使用只读方式追溯依赖：可以调用只读 MCP 工具、执行只读命令、读取被引用的脚本/配置/日志，检查外部程序路径、权限、网络地址、端口、DNS/HTTP 只读探测等；但不要修改文件、权限、crontab 或 systemd 单元，不要重启/停止服务，不要执行脚本中的业务写操作；对网络目标只做只读探测，不提交写请求。");
        builder.AppendLine();
        builder.AppendLine("## 服务器信息");
        builder.AppendLine($"- 名称：{_server.Name}");
        builder.AppendLine($"- serverId：{_server.Id}");
        builder.AppendLine($"- 地址：{_server.Host}:{_server.Port}");
        builder.AppendLine($"- 登录用户：{_server.Username}");
        builder.AppendLine($"- 提权状态：{SudoText}");
        if (meta is not null)
        {
            builder.AppendLine($"- 主机名：{Value(meta, "hostname")}");
            builder.AppendLine($"- 操作系统：{Value(meta, "os")}");
            builder.AppendLine($"- 内核：{Value(meta, "kernel")}");
            builder.AppendLine($"- 服务器时间：{Value(meta, "date")}");
            builder.AppendLine($"- cron 状态：{Value(meta, "cron_state")}");
            builder.AppendLine($"- crontab：{Value(meta, "crontab")}");
            builder.AppendLine($"- systemctl：{Value(meta, "systemctl")}");
        }

        builder.AppendLine();
        builder.AppendLine("## 计划任务");
        builder.AppendLine($"- 类型：{task.KindText}");
        builder.AppendLine($"- 来源：{task.SourceDisplay}");
        builder.AppendLine($"- 运行用户：{task.Owner}");
        builder.AppendLine($"- 状态：{task.StateText}");
        builder.AppendLine($"- 原始表达式：{task.ScheduleText}");
        builder.AppendLine($"- 中文解释：{task.ScheduleDescription}");
        if (!string.IsNullOrWhiteSpace(task.RawLine))
        {
            builder.AppendLine($"- 原始行：{task.RawLine}");
        }
        if (!string.IsNullOrWhiteSpace(task.NextRunText))
        {
            builder.AppendLine($"- 下次执行：{task.NextRunText}");
        }

        if (!string.IsNullOrWhiteSpace(task.LastRunText))
        {
            builder.AppendLine($"- 上次执行：{task.LastRunText}");
        }

        if (task.IsSystemd)
        {
            builder.AppendLine($"- Timer：{task.UnitName}");
            builder.AppendLine($"- 触发服务：{task.ActivatedUnit}");
            builder.AppendLine($"- ActiveState：{task.ActiveState}");
            builder.AppendLine($"- UnitFileState：{task.UnitFileState}");
            builder.AppendLine($"- 片段文件：{task.FragmentPath}");
        }

        builder.AppendLine($"- 命令：{task.Command}");
        if (task.ScriptPath is not null)
        {
            builder.AppendLine($"- 脚本路径：{task.ScriptPath}");
            builder.AppendLine($"- 解释器：{scriptInterpreter}");
            builder.AppendLine($"- 文件信息：{scriptMeta}");
            if (!scriptIsBinary && !string.IsNullOrWhiteSpace(scriptContent))
            {
                builder.AppendLine();
                builder.AppendLine("脚本内容：");
                builder.AppendLine("```" + LanguageTag(scriptInterpreter));
                builder.AppendLine(TruncateLines(scriptContent, 400, 50000));
                builder.AppendLine("```");
            }
            else if (scriptIsBinary)
            {
                builder.AppendLine("- 脚本为二进制文件，未包含内容。");
            }
            else
            {
                builder.AppendLine($"- 脚本读取失败：{scriptMeta}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("请重点分析：");
        builder.AppendLine("1. 该任务实际执行时间和频率是否符合预期（注意 cron 的“日/星期满足其一”语义、时区、夏令时）。");
        builder.AppendLine("2. 脚本/命令是否引用其他脚本、外部程序、配置文件、环境变量、网络地址/API、数据库等依赖；这些依赖是否存在，路径、权限、解释器、端口和网络可达性是否正确。必要时继续只读追溯被引用文件，检查进程、监听端口、连接状态和相关日志。");
        builder.AppendLine("3. 脚本/命令是否存在 PATH、工作目录、权限、日志、并发重叠、失败重试、输出未重定向等风险。");
        builder.AppendLine("4. 指出可能导致任务不执行或执行异常的具体原因，并给出只读排查命令。");
        builder.AppendLine("5. 给出最小化修改建议（修改 crontab 行或脚本），但不要直接执行修改。");
        builder.AppendLine("6. 如果现有信息不足，请明确列出还需要哪些只读信息，并在允许范围内直接调用只读工具继续排查，不要仅根据当前片段下结论。");
        return builder.ToString();
    }

    public async Task<bool> SendPromptToAgentAsync(string prompt, bool sendImmediately)
    {
        if (_sendToAgent is null)
        {
            _dialogs.Warn("AI 运维助手未配置，无法发送分析请求。", "AI 分析");
            return false;
        }

        return await _sendToAgent(prompt, sendImmediately);
    }

    private async Task AuditAsync(
        string tool,
        string command,
        bool success,
        string result,
        string? filePath = null,
        long? fileSize = null)
    {
        if (_audit is null)
        {
            return;
        }

        try
        {
            await _audit.LogCommandAsync(new CommandAuditLog
            {
                ServerId = _server.Id,
                ServerName = _server.Name,
                Tool = tool,
                Command = command,
                Status = success ? CommandStatus.Executed : CommandStatus.Failed,
                Result = result,
                FilePath = filePath,
                FileSize = fileSize,
                Category = AuditCategory.Exec
            });
        }
        catch
        {
        }
    }

    private static string FormatRunOutput(CommandResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"退出码：{result.ExitCode}");
        builder.AppendLine($"耗时：{result.Duration.TotalSeconds:0.0} 秒");
        if (!string.IsNullOrWhiteSpace(result.Output))
        {
            builder.AppendLine();
            builder.AppendLine("标准输出：");
            builder.AppendLine(Truncate(result.Output, 20000));
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            builder.AppendLine();
            builder.AppendLine("标准错误：");
            builder.AppendLine(Truncate(result.Error, 20000));
        }

        if (string.IsNullOrWhiteSpace(result.Output) && string.IsNullOrWhiteSpace(result.Error))
        {
            builder.AppendLine();
            builder.AppendLine("（无输出）");
        }

        return builder.ToString();
    }

    private static string Truncate(string text, int maxChars)
        => text.Length <= maxChars ? text : text[..maxChars] + "\n…（已截断）";

    private static string TruncateLines(string text, int maxLines, int maxChars)
    {
        var lines = text.Split('\n');
        var result = lines.Length <= maxLines ? text : string.Join('\n', lines.Take(maxLines));
        if (result.Length > maxChars)
        {
            result = result[..maxChars];
        }

        return lines.Length > maxLines ? result + $"\n…（已截断，共 {lines.Length} 行）" : result;
    }

    private static string LanguageTag(string interpreter) => interpreter.ToLowerInvariant() switch
    {
        var value when value.Contains("python", StringComparison.Ordinal) => "python",
        var value when value.Contains("php", StringComparison.Ordinal) => "php",
        var value when value.Contains("ruby", StringComparison.Ordinal) => "ruby",
        var value when value.Contains("perl", StringComparison.Ordinal) => "perl",
        var value when value.Contains("node", StringComparison.Ordinal) => "javascript",
        var value when value.Contains("pwsh", StringComparison.Ordinal) => "powershell",
        _ => "bash"
    };

    private static string Value(Dictionary<string, string> meta, string key)
        => meta.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : "未知";
}
