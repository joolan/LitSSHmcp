namespace LitSSHmcp.McpServer.Services;

/// <summary>快照请求参数解析：同步 ssh_snapshot_refresh 与多机 fleet_snapshot_refresh 共用。</summary>
internal static class SnapshotRequest
{
    public static IReadOnlyCollection<string>? ParseSections(string? profile, string? sections)
    {
        if (!string.IsNullOrWhiteSpace(sections))
        {
            var list = sections.Split(new[] { ',', '，', ' ', ';' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return list.Length > 0 ? list : null;
        }

        return (profile ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "quick" => new[] { "resource", "portmap", "systemd", "security" },
            _ => null
        };
    }

    public static bool IsFull(string? detail) =>
        string.Equals(detail?.Trim(), "full", StringComparison.OrdinalIgnoreCase);
}
