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

    /// <summary>
    /// 已审核可信脚本的完整 SHA-256 列表。只跳过脚本内容扫描的硬拦截，仍强制人工审批；
    /// 哈希按去 BOM、CRLF/CR→LF 规范化后的 UTF-8 内容计算。
    /// </summary>
    public TrustedScriptHash[] TrustedScriptHashes { get; set; } = Array.Empty<TrustedScriptHash>();
}

/// <summary>一条可信脚本哈希：精确内容白名单，不按路径/文件名匹配。</summary>
public sealed class TrustedScriptHash
{
    public string Sha256 { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Note { get; set; }
    public DateTimeOffset? AddedAt { get; set; }
}
