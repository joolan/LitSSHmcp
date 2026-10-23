namespace LitSSHmcp.Core.Services.Security;

/// <summary>
/// 兼容入口：POSIX argv 拆分的唯一实现位于 <see cref="CommandLineSyntax.TrySplitPosix"/>。
/// 保留本类型是为了让既有调用方与测试不必迁移。
/// </summary>
public static class PosixCommandLine
{
    /// <summary>拆分命令行；引号未闭合、包含换行/NUL 或缺少命令时返回 false。</summary>
    public static bool TrySplit(string? commandLine, out IReadOnlyList<string> args, out string? error) =>
        CommandLineSyntax.TrySplitPosix(commandLine, out args, out error);
}
