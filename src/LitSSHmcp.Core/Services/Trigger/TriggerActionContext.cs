using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.Core.Services.Trigger;

/// <summary>单个动作执行所需的上下文（由 TriggerRunner 组装）。</summary>
public sealed class TriggerActionContext
{
    public required TriggerConfig Trigger { get; init; }

    public required TriggerAction Action { get; init; }

    /// <summary>执行该步前已渲染好的模板变量（trigger/result/error/steps/notice 等）。</summary>
    public required IReadOnlyDictionary<string, string> Vars { get; init; }

    /// <summary>目标服务器；null = 本机。</summary>
    public SshServerConfig? Server { get; init; }

    /// <summary>Server 非空时必填。</summary>
    public ISshService? Ssh { get; init; }

    /// <summary>当前应用配置（数据库备份动作需要据此解析数据源与限流设置）。</summary>
    public AppConfig? Config { get; init; }
}
