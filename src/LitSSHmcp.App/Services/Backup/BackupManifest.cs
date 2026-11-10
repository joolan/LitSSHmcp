namespace LitSSHmcp.App.Services.Backup;

public sealed class BackupManifest
{
    public string PlanId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>生成该备份的 App 版本（写入文件名与 manifest，便于恢复时识别）。</summary>
    public string AppVersion { get; set; } = string.Empty;
    public string DataDir { get; set; } = string.Empty;
    public bool Encrypted { get; set; }
    public string Warning { get; set; } = string.Empty;
    public List<BackupManifestItem> Items { get; set; } = new();
}

public sealed class BackupManifestItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public long Size { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSelected { get; set; } = true;
}
