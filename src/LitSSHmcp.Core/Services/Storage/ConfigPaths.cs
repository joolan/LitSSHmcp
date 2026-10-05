namespace LitSSHmcp.Core.Services.Storage;

/// <summary>
/// 统一的本机数据目录与文件路径（%APPDATA%\LitSSH）。
/// </summary>
public static class ConfigPaths
{
    public static string AppDataDir { get; } = Initialize();

    public static string ConfigFile => Path.Combine(AppDataDir, "config.json");

    public static string AuditDb => Path.Combine(AppDataDir, "audit.db");

    /// <summary>审计哈希链的本地签名密钥（DPAPI 保护）。</summary>
    public static string AuditKeyFile => AuditDb + ".key";

    /// <summary>服务器快照库（快照记录 + 快照事件，与审计库分离，便于独立备份/清理）。</summary>
    public static string SnapshotDb => Path.Combine(AppDataDir, "snapshots.db");

    /// <summary>AI 运维助手的会话/消息库（与审计库/快照库分离）。</summary>
    public static string AgentDb => Path.Combine(AppDataDir, "agent.db");

    /// <summary>带外审批（CLI/IPC）待决/决策文件目录。</summary>
    public static string ApprovalsDir => Path.Combine(AppDataDir, "approvals");

    /// <summary>资产拓扑可视化编辑器的布局（节点位置/尺寸、端点锚点）。</summary>
    public static string TopologyLayoutFile => Path.Combine(AppDataDir, "topology-layout.json");

    public static string KnownHostsFile => Path.Combine(AppDataDir, "known_hosts.json");

    public static string LogsDir => Path.Combine(AppDataDir, "logs");

    private static string Initialize()
    {
        // 允许用环境变量覆盖数据目录(便于测试隔离 / 便携部署)；默认 %APPDATA%\LitSSH。
        var overrideDir = Environment.GetEnvironmentVariable("LITSSH_DATA_DIR");
        var dir = !string.IsNullOrWhiteSpace(overrideDir)
            ? overrideDir!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LitSSH");

        Directory.CreateDirectory(dir);
        return dir;
    }
}
