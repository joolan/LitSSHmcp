using System.Text.Json.Serialization;

namespace LitSSHmcp.Core.Models;

/// <summary>
/// Jenkins 环境类型只是展示/风险提示标签，不限制连接数量，也不参与接口调用。
/// 支持测试、预发布、生产、第三方和自定义；同一类型可以有多个连接。
/// </summary>
public static class JenkinsEnvironmentKinds
{
    public const string Test = "test";
    public const string Staging = "staging";
    public const string Production = "production";
    public const string Partner = "partner";
    public const string Custom = "custom";

    public static readonly string[] All = { Test, Staging, Production, Partner, Custom };

    public static string Label(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        Test => "测试",
        Staging => "预发布",
        Production => "生产",
        Partner => "第三方",
        _ => "自定义"
    };

    public static string Normalize(string? kind)
    {
        var normalized = kind?.Trim().ToLowerInvariant();
        return normalized is Test or Staging or Production or Partner or Custom ? normalized : Custom;
    }
}

/// <summary>Jenkins 多连接配置。连接数量不设上限，环境类型只是标签。</summary>
public sealed class JenkinsConfig
{
    public JenkinsConnectionConfig[] Connections { get; set; } = Array.Empty<JenkinsConnectionConfig>();

    /// <summary>可选默认连接；为空时选择第一个启用连接。最后选择项由 UiPrefs 单独保存。</summary>
    public string DefaultConnectionId { get; set; } = string.Empty;
}

/// <summary>单个 Jenkins 连接。测试、生产、第三方等只是环境类型，不限制连接数量。</summary>
public sealed class JenkinsConnectionConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;

    /// <summary>test / staging / production / partner / custom；未知值按 custom 处理。</summary>
    public string EnvironmentKind { get; set; } = JenkinsEnvironmentKinds.Custom;

    /// <summary>可选展示名；例如“A 公司 Jenkins”“生产-北京”。为空时使用环境类型标签。</summary>
    public string EnvironmentLabel { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>可选：通过该 SSH 服务器建立动态 SOCKS5 隧道后再访问 Jenkins；空=直连。</summary>
    public string TunnelSshServerId { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    /// <summary>Jenkins API Token；config.json 落盘时由 ConfigService 使用 DPAPI 加密。</summary>
    public string ApiToken { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>只读连接不允许构建/停止/取消，适合生产或第三方 Jenkins 的巡检场景。</summary>
    public bool ReadOnly { get; set; }

    /// <summary>构建、停止、取消前是否二次确认；生产/第三方默认开启。</summary>
    public bool RequireConfirmation { get; set; } = true;

    public bool IgnoreTlsErrors { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public string Description { get; set; } = string.Empty;

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? string.IsNullOrWhiteSpace(BaseUrl) ? "未命名 Jenkins" : BaseUrl
        : Name;

    [JsonIgnore]
    public string EnvironmentDisplay => string.IsNullOrWhiteSpace(EnvironmentLabel)
        ? JenkinsEnvironmentKinds.Label(EnvironmentKind)
        : EnvironmentLabel;

    [JsonIgnore]
    public bool IsHighRisk => JenkinsEnvironmentKinds.Normalize(EnvironmentKind)
        is JenkinsEnvironmentKinds.Production or JenkinsEnvironmentKinds.Partner;
}
