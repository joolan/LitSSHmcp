namespace LitSSHmcp.App.Services.Toolbox;

public enum ToolboxItemType
{
    Application,
    File,
    Folder,
    Url,
    Script,
    Builtin
}

/// <summary>工具启动时注入到子进程的环境变量；值在 toolbox.json 中由 DPAPI 加密。</summary>
public sealed class ToolboxEnvironmentVariable
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    public ToolboxEnvironmentVariable Clone() => new() { Name = Name, Value = Value };

    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Length <= 128 &&
        (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}

public sealed class ToolboxItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ToolboxItemType Type { get; set; } = ToolboxItemType.Application;
    public string Target { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string ScriptShell { get; set; } = "cmd";
    public bool RunAsAdmin { get; set; }
    public List<ToolboxEnvironmentVariable> EnvironmentVariables { get; set; } = new();
    public bool IsPinned { get; set; }
    public int SortOrder { get; set; }
    public string Group { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
    public string? BuiltinId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastUsedAt { get; set; }
    public int UsageCount { get; set; }

    public ToolboxItem Clone()
    {
        var clone = (ToolboxItem)MemberwiseClone();
        clone.EnvironmentVariables = EnvironmentVariables is null
            ? new List<ToolboxEnvironmentVariable>()
            : EnvironmentVariables.Select(variable => variable.Clone()).ToList();
        return clone;
    }
}

public sealed class ToolboxDocument
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<ToolboxItem> Items { get; set; } = new();
}

public static class ToolboxTypeText
{
    public static string For(ToolboxItemType type) => type switch
    {
        ToolboxItemType.Application => "程序",
        ToolboxItemType.File => "文件",
        ToolboxItemType.Folder => "文件夹",
        ToolboxItemType.Url => "网址",
        ToolboxItemType.Script => "脚本",
        ToolboxItemType.Builtin => "内置工具",
        _ => "未知"
    };
}
