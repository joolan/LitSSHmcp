namespace LitSSHmcp.App.ViewModels;

public enum SlashCommandKind
{
    /// <summary>立即执行（如打开临时聊天面板）。</summary>
    Action,

    /// <summary>作为输入区的模式/参数 chip 保留，随下次发送生效。</summary>
    Mode
}

/// <summary>输入框斜杠命令（可扩展：临时聊天 / 清空上下文 / 压缩会话 / 选择技能…）。</summary>
public sealed record SlashCommand(string Name, string Description, SlashCommandKind Kind);

/// <summary>斜杠命令注册表与解析。</summary>
public static class SlashCommands
{
    public const string TempChat = "/临时聊天";

    public const string CompactSession = "/压缩会话";
    public const string ClearScreen = "/清空屏幕";
    public const string ClearContext = "/清空上下文";

    public static readonly IReadOnlyList<SlashCommand> All = new[]
    {
        new SlashCommand(TempChat, "打开独立「临时聊天」面板：不保存、无工具/技能、可切换模型", SlashCommandKind.Action),
        new SlashCommand(CompactSession, "立即压缩当前会话上下文（把较早轮次摘要后丢弃）", SlashCommandKind.Action),
        new SlashCommand(ClearScreen, "只清空屏幕显示（会话上下文与模型记忆保留）", SlashCommandKind.Action),
        new SlashCommand(ClearContext, "只重置模型上下文（保留屏幕与记录，不删除会话记录）", SlashCommandKind.Action),
    };

    /// <summary>输入是否正处于“正在输入斜杠命令”状态（以 / 开头且首个 token 内无空白）；是则返回该前缀，否则 null。</summary>
    public static string? CurrentToken(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return null;

        var text = input.TrimStart();
        if (text.Length == 0 || text[0] != '/')
            return null;

        return text.IndexOfAny(new[] { ' ', '\t', '\n', '\r' }) >= 0 ? null : text;
    }

    /// <summary>按前缀/包含匹配命令。</summary>
    public static IReadOnlyList<SlashCommand> Match(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return All;

        var list = All.Where(c =>
            c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            c.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        return list;
    }

    /// <summary>输入去除首尾空白后是否恰好等于某个命令（用于回车直接执行）。</summary>
    public static SlashCommand? FindExact(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        return All.FirstOrDefault(c => string.Equals(c.Name, text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>解析整行输入为「命令 + 参数」（命令名取首个空白前的 token，大小写不敏感）。</summary>
    public static (SlashCommand? Command, string Args) Parse(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text) || text[0] != '/')
            return (null, string.Empty);

        var sp = text.IndexOfAny(new[] { ' ', '\t', '\n', '\r' });
        var name = sp < 0 ? text : text[..sp];
        var args = sp < 0 ? string.Empty : text[(sp + 1)..].Trim();
        var cmd = All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        return (cmd, args);
    }
}
