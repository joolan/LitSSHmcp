using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Agent;

/// <summary>根据用户首条诉求给出会话工具 profile 建议；只建议，不静默切换。</summary>
public static class ToolProfileSuggester
{
    private static readonly string[] ChangeKeywords =
    {
        "重启", "发布", "部署", "变更", "升级", "回滚", "扩容", "缩容", "补丁", "执行",
        "restart", "deploy", "rollout", "rollback", "upgrade", "patch", "apply"
    };

    private static readonly string[] DiagnoseKeywords =
    {
        "排查", "诊断", "报错", "异常", "失败", "为什么", "磁盘", "内存", "负载", "日志",
        "证书", "端口", "超时", "连不上", "不可用", "error", "fail", "timeout",
        "troubleshoot", "diagnose"
    };

    private static readonly string[] AssetKeywords =
    {
        "资产", "拓扑", "依赖", "影响面", "关系", "图谱", "asset", "topology",
        "dependency", "impact"
    };

    public static AgentToolProfile? Suggest(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var value = text.Trim();
        if (ContainsAny(value, ChangeKeywords))
            return AgentToolProfile.Change;
        if (ContainsAny(value, DiagnoseKeywords))
            return AgentToolProfile.Diagnose;
        if (ContainsAny(value, AssetKeywords))
            return AgentToolProfile.Assets;
        return null;
    }

    private static bool ContainsAny(string text, IEnumerable<string> keywords) =>
        keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
}
