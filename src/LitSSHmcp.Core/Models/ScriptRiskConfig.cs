namespace LitSSHmcp.Core.Models;

/// <summary>
/// 脚本风险扫描规则配置（security.scriptRules）：在命令过滤 Blocked/Sensitive 扫描之外，
/// 脚本专用内置规则可按需禁用，并可追加自定义正则。保存后按配置文件 mtime 热生效，无需重启。
/// </summary>
public class ScriptRiskConfig
{
    /// <summary>是否启用脚本专用内置规则（关闭后仅保留命令过滤扫描与自定义规则；命令过滤扫描不受此开关影响）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>追加的危险规则（正则，每行一条；命中=脚本硬拒绝不进审批）。</summary>
    public string[] ExtraBlockedPatterns { get; set; } = Array.Empty<string>();

    /// <summary>追加的可疑规则（正则，每行一条；命中=进审批并展示）。</summary>
    public string[] ExtraSensitivePatterns { get; set; } = Array.Empty<string>();

    /// <summary>要禁用的内置规则标签（精确匹配内置标签文本，如 "eval 动态执行"；留空=全部启用）。</summary>
    public string[] DisabledBuiltinRuleLabels { get; set; } = Array.Empty<string>();
}
