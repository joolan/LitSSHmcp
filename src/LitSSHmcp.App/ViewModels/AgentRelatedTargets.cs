using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;

namespace LitSSHmcp.App.ViewModels;

public enum AgentRelatedTargetKind
{
    Server,
    DataSource,
    Document
}

/// <summary>本轮工具轨迹中可继续操作的关联目标。</summary>
public sealed class AgentRelatedTarget : INotifyPropertyChanged
{
    public required AgentRelatedTargetKind Kind { get; init; }
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Subtitle { get; init; } = string.Empty;
    public string Glyph { get; init; } = string.Empty;
    public SshServerConfig? Server { get; init; }
    public DataSourceConfig? DataSource { get; init; }
    public string? DocumentPath { get; init; }
    public string? DocumentRelativePath { get; init; }

    private int _calls = 1;
    public int Calls
    {
        get => _calls;
        set
        {
            if (_calls == value)
                return;
            _calls = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Calls)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CallsText)));
        }
    }

    public string CallsText => Calls > 1 ? $"×{Calls}" : string.Empty;
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>从工具名与参数中提取本轮关联的服务器、数据源和 ops_doc_* 产出。</summary>
public static class AgentRelatedTargetExtractor
{
    public static IReadOnlyList<AgentRelatedTarget> Extract(
        string toolName,
        string? argumentsJson,
        AppConfig config,
        string workspaceRoot,
        string sharedRoot,
        bool sessionMode,
        bool succeeded)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return Array.Empty<AgentRelatedTarget>();

        JsonDocument document;
        try { document = JsonDocument.Parse(argumentsJson); }
        catch (JsonException) { return Array.Empty<AgentRelatedTarget>(); }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Array.Empty<AgentRelatedTarget>();

            if (string.Equals(toolName, "ops_doc_write", StringComparison.OrdinalIgnoreCase))
            {
                if (!succeeded)
                    return Array.Empty<AgentRelatedTarget>();
                return ExtractDocument(root, workspaceRoot, sharedRoot, sessionMode);
            }

            var targets = new List<AgentRelatedTarget>(2);
            foreach (var value in ReadValues(root, "serverId", split: false))
            {
                var server = ResolveServer(config, value);
                if (server is not null)
                    targets.Add(ServerTarget(server));
            }
            foreach (var value in ReadValues(root, "serverIds", split: true))
            {
                var server = ResolveServer(config, value);
                if (server is not null)
                    targets.Add(ServerTarget(server));
            }
            foreach (var value in ReadValues(root, "datasourceId", split: false))
            {
                var datasource = ResolveDataSource(config, value);
                if (datasource is not null)
                    targets.Add(DataSourceTarget(datasource));
            }

            foreach (var key in new[] { "assetId", "from", "to", "targetAssetId" })
            {
                foreach (var value in ReadValues(root, key, split: false))
                {
                    if (value.StartsWith("ssh:", StringComparison.OrdinalIgnoreCase))
                    {
                        var server = ResolveServer(config, value);
                        if (server is not null)
                            targets.Add(ServerTarget(server));
                    }
                    else if (value.StartsWith("ds:", StringComparison.OrdinalIgnoreCase))
                    {
                        var datasource = ResolveDataSource(config, value);
                        if (datasource is not null)
                            targets.Add(DataSourceTarget(datasource));
                    }
                }
            }

            return targets;
        }
    }

    private static IReadOnlyList<AgentRelatedTarget> ExtractDocument(
        JsonElement root, string workspaceRoot, string sharedRoot, bool sessionMode)
    {
        var path = ReadString(root, "path");
        if (string.IsNullOrWhiteSpace(path))
            path = sessionMode ? "shared/OPS_ASSETS.md" : "OPS_ASSETS.md";

        var normalized = path.Trim().Replace('\\', '/');
        var useShared = sessionMode &&
                        normalized.StartsWith("shared/", StringComparison.OrdinalIgnoreCase);
        if (useShared)
            normalized = normalized["shared/".Length..];

        var targetRoot = useShared || !sessionMode ? sharedRoot : workspaceRoot;
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(targetRoot, normalized));
        }
        catch
        {
            return Array.Empty<AgentRelatedTarget>();
        }

        if (!PathGuard.IsUnder(targetRoot, full) || full.Length == 0)
            return Array.Empty<AgentRelatedTarget>();

        var relative = useShared
            ? "shared/" + Path.GetRelativePath(sharedRoot, full).Replace('\\', '/')
            : Path.GetRelativePath(targetRoot, full).Replace('\\', '/');
        var name = Path.GetFileName(full);
        return new[]
        {
            new AgentRelatedTarget
            {
                Kind = AgentRelatedTargetKind.Document,
                Id = full,
                Title = string.IsNullOrWhiteSpace(name) ? relative : name,
                Subtitle = relative,
                Glyph = "📝",
                DocumentPath = full,
                DocumentRelativePath = relative
            }
        };
    }

    private static AgentRelatedTarget ServerTarget(SshServerConfig server) => new()
    {
        Kind = AgentRelatedTargetKind.Server,
        Id = server.Id,
        Title = string.IsNullOrWhiteSpace(server.Name) ? server.Id : server.Name,
        Subtitle = $"{server.Host}:{server.Port}",
        Glyph = "🖥",
        Server = server
    };

    private static AgentRelatedTarget DataSourceTarget(DataSourceConfig datasource) => new()
    {
        Kind = AgentRelatedTargetKind.DataSource,
        Id = datasource.Id,
        Title = string.IsNullOrWhiteSpace(datasource.Name) ? datasource.Id : datasource.Name,
        Subtitle = $"{datasource.Type} · {datasource.Host}:{datasource.Port}",
        Glyph = "🗄",
        DataSource = datasource
    };

    private static SshServerConfig? ResolveServer(AppConfig config, string value)
    {
        var id = StripPrefix(value, "ssh:");
        return config.Servers.FirstOrDefault(s =>
            EqualsAny(s.Id, id, value) ||
            EqualsAny(s.Name, id, value) ||
            EqualsAny(s.Host, id, value));
    }

    private static DataSourceConfig? ResolveDataSource(AppConfig config, string value)
    {
        var id = StripPrefix(value, "ds:");
        return config.DataSources.FirstOrDefault(d =>
            EqualsAny(d.Id, id, value) ||
            EqualsAny(d.Name, id, value) ||
            EqualsAny(d.Host, id, value));
    }

    private static string StripPrefix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..] : value;

    private static bool EqualsAny(string? candidate, string a, string b) =>
        !string.IsNullOrWhiteSpace(candidate) &&
        (string.Equals(candidate, a, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(candidate, b, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> ReadValues(JsonElement root, string key, bool split)
    {
        var value = ReadString(root, key);
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        if (!split)
        {
            yield return value.Trim();
            yield break;
        }

        foreach (var item in value.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' },
                     StringSplitOptions.RemoveEmptyEntries))
            yield return item.Trim();
    }

    private static string? ReadString(JsonElement root, string key)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                continue;
            return property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : property.Value.ToString();
        }
        return null;
    }
}
