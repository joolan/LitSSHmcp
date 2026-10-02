namespace LitSSHmcp.Core.Services.Security;

/// <summary>
/// POSIX shell 单引号转义。单引号内除单引号本身外所有字符均为字面量，
/// 因此可把任意不可信输入安全地嵌入远端命令行，杜绝 <c>; | &amp; $( ) ` 换行</c> 等元字符注入。
/// </summary>
public static class ShellQuote
{
    /// <summary>把 <paramref name="value"/> 包成一个 shell 单引号字面量。</summary>
    public static string Single(string? value)
    {
        var s = value ?? string.Empty;
        return "'" + s.Replace("'", "'\\''") + "'";
    }

    /// <summary>把多个值各自转义后用单个空格连接（用于拼接命令行参数列表）。</summary>
    public static string Join(IEnumerable<string?> values) =>
        string.Join(" ", values.Select(Single));

    /// <summary>校验字符串是否可安全直接嵌入 shell（无任何元字符）。</summary>
    public static bool IsPlain(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c)) continue;
            if (c is '/' or '.' or '_' or '-' or ':' or '=') continue;
            return false;
        }
        return true;
    }
}
