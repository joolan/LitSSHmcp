using System.Text.Json.Serialization;

namespace LitSSHmcp.Core.Models;

public static class KubernetesEnvironmentKinds
{
    public const string SelfTest = "self-test";
    public const string Test = "test";
    public const string Staging = "staging";
    public const string Production = "production";
    public const string Cloud = "cloud";
    public const string Custom = "custom";

    public static readonly string[] All = { SelfTest, Test, Staging, Production, Cloud, Custom };

    public static string Label(string? kind) => Normalize(kind) switch
    {
        SelfTest => "自检/测试",
        Test => "测试",
        Staging => "预发布",
        Production => "生产",
        Cloud => "云上集群",
        _ => "自定义"
    };

    public static string Normalize(string? kind)
    {
        var normalized = kind?.Trim().ToLowerInvariant();
        return normalized is SelfTest or Test or Staging or Production or Cloud or Custom
            ? normalized
            : Custom;
    }

    public static bool IsHighRisk(string? kind)
        => Normalize(kind) == Production;
}

public static class KubernetesAccessModes
{
    public const string LocalKubectl = "local-kubectl";
    public const string RemoteSsh = "remote-ssh";

    public static readonly string[] All = { LocalKubectl, RemoteSsh };

    public static string Label(string? mode) => Normalize(mode) == RemoteSsh
        ? "通过 SSH 服务器执行 kubectl"
        : "本机 kubectl";

    public static string Normalize(string? mode)
    {
        var normalized = mode?.Trim().ToLowerInvariant();
        return normalized == RemoteSsh ? RemoteSsh : LocalKubectl;
    }
}

public static class KubernetesProviders
{
    public const string Generic = "generic";
    public const string K3s = "k3s";
    public const string Aliyun = "aliyun";
    public const string Aws = "aws";
    public const string Azure = "azure";
    public const string Custom = "custom";

    public static readonly string[] All = { Generic, K3s, Aliyun, Aws, Azure, Custom };

    public static string Label(string? provider) => Normalize(provider) switch
    {
        K3s => "K3s",
        Aliyun => "阿里云 ACK",
        Aws => "AWS EKS",
        Azure => "Azure AKS",
        Custom => "自定义",
        _ => "通用 Kubernetes"
    };

    public static string Normalize(string? provider)
    {
        var normalized = provider?.Trim().ToLowerInvariant();
        return normalized is K3s or Aliyun or Aws or Azure or Custom ? normalized : Generic;
    }
}

public sealed class KubernetesConfig
{
    public KubernetesConnectionConfig[] Connections { get; set; } = Array.Empty<KubernetesConnectionConfig>();
    public string DefaultConnectionId { get; set; } = string.Empty;
}

public sealed class KubernetesConnectionConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string EnvironmentKind { get; set; } = KubernetesEnvironmentKinds.Test;
    public string EnvironmentLabel { get; set; } = string.Empty;
    public string Provider { get; set; } = KubernetesProviders.Generic;
    public string AccessMode { get; set; } = KubernetesAccessModes.LocalKubectl;

    public string KubeconfigPath { get; set; } = string.Empty;

    public string KubeconfigContent { get; set; } = string.Empty;

    public string ContextName { get; set; } = string.Empty;
    public string DefaultNamespace { get; set; } = string.Empty;
    public string KubectlPath { get; set; } = "kubectl";
    public string RemoteSshServerId { get; set; } = string.Empty;
    public string RemoteKubeconfigPath { get; set; } = string.Empty;
    public string RemoteKubectlPath { get; set; } = "kubectl";
    public bool ReadOnly { get; set; } = true;
    public bool RequireConfirmation { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 30;
    public string Description { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsRemote => KubernetesAccessModes.Normalize(AccessMode) == KubernetesAccessModes.RemoteSsh;

    [JsonIgnore]
    public bool UsesKubeconfigContent => !IsRemote && !string.IsNullOrWhiteSpace(KubeconfigContent);

    [JsonIgnore]
    public bool IsHighRisk => KubernetesEnvironmentKinds.IsHighRisk(EnvironmentKind);

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? $"未命名 {KubernetesEnvironmentKinds.Label(EnvironmentKind)} 集群"
        : Name;

    [JsonIgnore]
    public string EnvironmentDisplay => string.IsNullOrWhiteSpace(EnvironmentLabel)
        ? KubernetesEnvironmentKinds.Label(EnvironmentKind)
        : EnvironmentLabel;

    [JsonIgnore]
    public string ProviderDisplay => KubernetesProviders.Label(Provider);

    [JsonIgnore]
    public string AccessDisplay => KubernetesAccessModes.Label(AccessMode);

    [JsonIgnore]
    public string ContextDisplay => string.IsNullOrWhiteSpace(ContextName) ? "当前 context" : ContextName;

    [JsonIgnore]
    public string NamespaceDisplay => string.IsNullOrWhiteSpace(DefaultNamespace) ? "全部/默认" : DefaultNamespace;
}
