using System.Diagnostics;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Trigger;

/// <summary>
/// git 提交/推送动作：在触发目标（本机或远程服务器）上执行 status → add → commit → push。
/// 无变更时跳过提交并视为成功。
/// </summary>
public static class GitAction
{
    public static async Task<TriggerActionResult> RunAsync(TriggerActionContext ctx, CancellationToken ct = default)
    {
        var action = ctx.Action;
        var sw = Stopwatch.StartNew();
        var result = new TriggerActionResult { Type = TriggerActionType.GitCommitPush };

        var message = TriggerTemplate.Render(action.GitMessageTemplate, ctx.Vars);
        if (string.IsNullOrWhiteSpace(message))
            message = $"触发事件「{ctx.Trigger.Name}」自动提交";

        // 1) 检测变更
        var status = await ExecAsync(ctx, "status", ct);
        if (!status.Success)
            return Finish(result, sw, Fail("git status", status));

        if (string.IsNullOrWhiteSpace(status.Output))
        {
            result.Success = true;
            result.Output = "工作区无变更，跳过提交";
            return Finish(result, sw, null);
        }

        // 2) git add -A
        if (action.GitCommitAll)
        {
            var add = await ExecAsync(ctx, "add", ct, "-A");
            if (!add.Success)
                return Finish(result, sw, Fail("git add", add));
        }

        // 3) commit
        var commit = await ExecAsync(ctx, "commit", ct, "-m", message);
        if (!commit.Success)
        {
            // 竞态：status 期间看到变更，但 add 后已无可提交内容（仅未暂存且未启用 add、或被忽略）→ 视为跳过
            if (IsNothingToCommit(commit))
            {
                result.Success = true;
                result.Output = "工作区无变更，跳过提交";
                return Finish(result, sw, null);
            }
            return Finish(result, sw, Fail("git commit", commit));
        }

        var summary = FirstLine(commit.Output);
        if (action.GitOperation == TriggerGitOperation.CommitAndPush)
        {
            // 4) push
            var push = action.GitPushBranch.Trim().Length > 0
                ? await ExecAsync(ctx, "push", ct, action.GitPushRemote, action.GitPushBranch)
                : await ExecAsync(ctx, "push", ct, action.GitPushRemote);
            if (!push.Success)
                return Finish(result, sw, Fail("git push", push));

            summary = string.IsNullOrWhiteSpace(push.Output)
                ? summary + "；已推送"
                : summary + "；" + FirstLine(push.Output);
        }

        result.Success = true;
        result.Output = summary;
        return Finish(result, sw, null);
    }

    private static TriggerActionResult Finish(TriggerActionResult result, Stopwatch sw, string? error)
    {
        result.Duration = sw.Elapsed;
        if (error is not null)
            result.Error = error;
        return result;
    }

    private static string Fail(string step, (bool Success, string Output, string Error, int? ExitCode, bool LocalGitMissing) r)
    {
        var detail = string.IsNullOrWhiteSpace(r.Error) ? r.Output
            : string.IsNullOrWhiteSpace(r.Output) ? r.Error
            : r.Output + " | " + r.Error;
        var msg = $"{step} 失败: {detail}";
        if (r.LocalGitMissing)
            msg += "（本机未找到 git，请安装并加入 PATH）";
        return msg.Trim();
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n').FirstOrDefault()?.Trim().TrimEnd('\r') ?? string.Empty;
        return line.Length > 300 ? line[..300] + "…" : line;
    }

    private static async Task<(bool Success, string Output, string Error, int? ExitCode, bool LocalGitMissing)> ExecAsync(
        TriggerActionContext ctx, string sub, CancellationToken ct, params string?[] args)
    {
        var action = ctx.Action;
        var strArgs = new List<string> { "-C", action.GitDir, sub };
        foreach (var a in args)
        {
            if (a is not null)
                strArgs.Add(a);
        }

        if (ctx.Server is null)
        {
            var r = await LocalProcessRunner.RunAsync("git", strArgs, timeoutSeconds: 300, ct: ct);
            var missing = !r.Success && LooksMissing(r.Error);
            return (r.Success, r.Output, r.Error, r.ExitCode, missing);
        }

        var cmd = string.Join(" ", strArgs.Select(ShellQuote));
        var remote = await ctx.Ssh!.ExecuteCommandAsync(ctx.Server, cmd, ct, timeoutSeconds: 300);
        return (remote.Success, remote.Output, remote.Error, remote.ExitCode, false);
    }

    private static bool LooksMissing(string error)
        => error.Contains("cannot find", StringComparison.OrdinalIgnoreCase) ||
           error.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
           error.Contains("找不到", StringComparison.Ordinal) ||
           error.Contains("系统找不到", StringComparison.Ordinal);

    private static bool IsNothingToCommit((bool Success, string Output, string Error, int? ExitCode, bool LocalGitMissing) r)
    {
        var text = r.Output + "\n" + r.Error;
        return text.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("nothing added to commit", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("无文件要提交", StringComparison.Ordinal) ||
               text.Contains("没有要提交", StringComparison.Ordinal);
    }

    /// <summary>单引号包裹（POSIX shell），内部单引号转义为 '\''。</summary>
    public static string ShellQuote(string value)
        => "'" + value.Replace("'", "'\\''") + "'";
}
