namespace LitSSHmcp.Core.Models;

using System.Text.Json.Serialization;

public enum AuthType
{
    Password,
    KeyFile
}

public enum SudoType
{
    None,
    CurrentUser,
    RootUser,
    CustomUser,

    /// <summary>自动：先按"当前用户 sudo"(用配置的提权密码)执行，失败(未授权/无密码)再回退 <c>su - root</c>。
    /// 适配"登录账号不在 sudoers、但可以 su 到 root"或反之的环境。</summary>
    Auto
}

public class SshServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public AuthType AuthType { get; set; } = AuthType.Password;
    public string? Password { get; set; }
    public string? KeyFilePath { get; set; }
    public string? KeyFilePassphrase { get; set; }
    public string? Description { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastConnectedAt { get; set; }

    public SudoType SudoType { get; set; } = SudoType.None;
    public string? SudoUsername { get; set; }
    public string? SudoPassword { get; set; }

    /// <summary>
    /// 禁用开关：禁用后 ① 不出现在 MCP 的 ssh_list_servers；
    /// ② 所有按服务器标识解析的工具(ssh_*/docker_*/service_*/log_*/java_*/file/app 等)一律返回
    /// <c>server_disabled</c> 并拒绝执行；③ 资产拓扑中该服务器不可建链、点击连接被拦截；④ 拓扑自动发现跳过它。
    /// 默认 false（老 config.json 没有该字段时保持启用）。
    /// </summary>
    public bool Disabled { get; set; }

    [JsonIgnore]
    public string TagsText => Tags.Length == 0 ? string.Empty : string.Join(", ", Tags);
}