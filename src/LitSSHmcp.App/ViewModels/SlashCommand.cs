namespace LitSSHmcp.App.ViewModels;

public enum SlashCommandKind
{
    /// <summary>立即执行（如打开临时聊天面板）。</summary>
    Action,

    /// <summary>作为输入区的模式/参数 chip 保留，随下次发送生效。</summary>
    Mode,

    /// <summary>用户自定义提示词模板：选中后把 <see cref="SlashCommand.Prompt"/> 填入输入框（不自动发送）。</summary>
    Template
}

/// <summary>输入框斜杠命令（内置动作 + 用户自定义提示词模板）。</summary>
public sealed record SlashCommand(string Name, string Description, SlashCommandKind Kind, string? Prompt = null);

/// <summary>斜杠建议面板里的一行：命令、分组标题、或可切换的技能项（codex 风格内联展开）。</summary>
public sealed class SlashSuggestion
{
    /// <summary>命令/技能名或分组标题文本。</summary>
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>命令项（执行）。</summary>
    public SlashCommand? Command { get; init; }

    /// <summary>技能项 Id（切换激活）。</summary>
    public string? SkillId { get; init; }

    /// <summary>分组标题行（不可选）。</summary>
    public bool IsHeader { get; init; }

    /// <summary>技能项当前是否已激活（显示 ✓）。</summary>
    public bool IsSelected { get; init; }

    public bool IsSkill => SkillId is not null;
    public bool ShowGlyph => IsSkill;
    public string Glyph => IsSelected ? "✓" : "　";
}


/// <summary>斜杠命令注册表与解析（内置命令 + 运行时合并的用户模板）。</summary>
public static class SlashCommands
{
    public const string TempChat = "/临时聊天";
    public const string Skills = "/技能";

    public const string CompactSession = "/压缩会话";
    public const string ClearScreen = "/清空屏幕";
    public const string ClearContext = "/清空上下文";

    /// <summary>内置命令。</summary>
    public static readonly IReadOnlyList<SlashCommand> BuiltIn = new[]
    {
        new SlashCommand(TempChat, "打开独立「临时聊天」面板：不保存、无工具/技能、可切换模型", SlashCommandKind.Action),
        new SlashCommand(Skills, "选择本次会话激活的运维技能（未选择时只注入技能索引）", SlashCommandKind.Action),
        new SlashCommand(CompactSession, "立即压缩当前会话上下文（把较早轮次摘要后丢弃）", SlashCommandKind.Action),
        new SlashCommand(ClearScreen, "只清空屏幕显示（会话上下文与模型记忆保留）", SlashCommandKind.Action),
        new SlashCommand(ClearContext, "只重置模型上下文（保留屏幕与记录，不删除会话记录）", SlashCommandKind.Action),
    };

    /// <summary>向后兼容：仅内置命令集合。</summary>
    public static IReadOnlyList<SlashCommand> All => BuiltIn;

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

    /// <summary>按前缀/包含匹配命令（默认内置集合）。</summary>
    public static IReadOnlyList<SlashCommand> Match(string prefix) => Match(BuiltIn, prefix);

    public static IReadOnlyList<SlashCommand> Match(IReadOnlyList<SlashCommand> commands, string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return commands;

        return commands.Where(c =>
            c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            c.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>输入去除首尾空白后是否恰好等于某个命令（用于回车直接执行）。</summary>
    public static SlashCommand? FindExact(string? input) => FindExact(BuiltIn, input);

    public static SlashCommand? FindExact(IReadOnlyList<SlashCommand> commands, string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        return commands.FirstOrDefault(c => string.Equals(c.Name, text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>解析整行输入为「命令 + 参数」（命令名取首个空白前的 token，大小写不敏感）。</summary>
    public static (SlashCommand? Command, string Args) Parse(string? input) => Parse(BuiltIn, input);

    public static (SlashCommand? Command, string Args) Parse(IReadOnlyList<SlashCommand> commands, string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text) || text[0] != '/')
            return (null, string.Empty);

        var sp = text.IndexOfAny(new[] { ' ', '\t', '\n', '\r' });
        var name = sp < 0 ? text : text[..sp];
        var args = sp < 0 ? string.Empty : text[(sp + 1)..].Trim();
        var cmd = commands.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        return (cmd, args);
    }
}
