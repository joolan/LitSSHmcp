using System.Text.Json;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

public sealed record AssetProfileView(
    string Id,
    string Name,
    string? Host,
    int? Port,
    string? AssetKey);

public partial class TopologyViewModel
{
    internal static string[] NodeProfileIds(TopologyNode node)
    {
        if (node.Info is null || !node.Info.TryGetValue("profileIds", out var value))
            return Array.Empty<string>();

        return value switch
        {
            string[] values => values,
            IEnumerable<string> values => values.ToArray(),
            JsonElement { ValueKind: JsonValueKind.Array } element => element
                .EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? string.Empty)
                .Where(x => x.Length > 0)
                .ToArray(),
            _ => Array.Empty<string>()
        };
    }

    internal static IEnumerable<string> ProfileLayoutIds(TopologyNode node)
    {
        var prefix = node.Type switch
        {
            "ssh" => AssetNode.SshPrefix,
            "datasource" => AssetNode.DsPrefix,
            "application" => AssetNode.AppPrefix,
            _ => string.Empty
        };

        foreach (var profileId in NodeProfileIds(node))
        {
            if (prefix.Length > 0)
                yield return prefix + profileId;
            yield return profileId;
        }
    }

    internal static int? InfoInt(TopologyNode node, string key)
    {
        if (node.Info is null || !node.Info.TryGetValue(key, out var value))
            return null;

        return value switch
        {
            int number => number,
            long number => (int)number,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var parsed) => parsed,
            _ => null
        };
    }

    public GraphItemVm? SelectedItemOrNull => _selectedId == null ? null : FindItem(_selectedId);

    public IReadOnlyList<AssetProfileView> GetProfiles(string nodeId)
    {
        var node = _graph?.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node?.Info is null || !node.Info.TryGetValue("profiles", out var raw))
            return Array.Empty<AssetProfileView>();

        var result = new List<AssetProfileView>();
        if (raw is System.Collections.IEnumerable enumerable && raw is not string)
        {
            foreach (var item in enumerable)
            {
                if (item is IDictionary<string, object?> values)
                    AddProfile(result, values);
                else if (item is IDictionary<string, object> plain)
                    AddProfile(result, plain.ToDictionary(kv => kv.Key, kv => (object?)kv.Value));
            }
        }
        else if (raw is JsonElement { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var values = new Dictionary<string, object?>();
                foreach (var property in item.EnumerateObject())
                    values[property.Name] = property.Value.ToString();
                AddProfile(result, values);
            }
        }

        return result;
    }

    public void SetNodeAssetKey(string nodeId, string? assetKey)
    {
        var profileIds = GetProfiles(nodeId).Select(p => p.Id).ToArray();
        if (profileIds.Length == 0)
            return;

        ApplyAssetKey(nodeId, profileIds, assetKey);
    }

    public void SplitProfile(string nodeId, string profileId, string? assetKey)
    {
        ApplyAssetKey(nodeId, new[] { profileId }, string.IsNullOrWhiteSpace(assetKey) ? profileId : assetKey);
    }

    private void ApplyAssetKey(string nodeId, IReadOnlyCollection<string> profileIds, string? assetKey) =>
        AsyncOperation.Run("设置资产标识", async () =>
        {
            try
            {
                var ids = profileIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var key = string.IsNullOrWhiteSpace(assetKey) ? null : assetKey.Trim();
                var config = await _configService.LoadConfigAsync();

                foreach (var server in config.Servers)
                {
                    if (ids.Contains(server.Id))
                        server.AssetKey = key;
                }
                foreach (var datasource in config.DataSources)
                {
                    if (ids.Contains(datasource.Id))
                        datasource.AssetKey = key;
                }
                foreach (var application in config.Applications)
                {
                    if (ids.Contains(application.Id))
                        application.AssetKey = key;
                }

                await _configService.SaveConfigAsync(config);
                StatusMessage = string.IsNullOrWhiteSpace(key)
                    ? $"已清除 {nodeId} 的资产标识"
                    : $"已设置 {nodeId} 的资产标识为 {key}";
                Load();
            }
            catch (Exception ex)
            {
                StatusMessage = $"设置资产标识失败: {ex.Message}";
            }
        });

    private static void AddProfile(List<AssetProfileView> result, IDictionary<string, object?> values)
    {
        var id = Text(values, "id");
        if (string.IsNullOrWhiteSpace(id))
            return;

        result.Add(new AssetProfileView(
            id,
            Text(values, "name"),
            Text(values, "host"),
            Int(values, "port"),
            Text(values, "assetKey")));
    }

    private static string Text(IDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;

    private static int? Int(IDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
            return null;
        if (value is int number)
            return number;
        if (value is JsonElement { ValueKind: JsonValueKind.Number } element && element.TryGetInt32(out var parsed))
            return parsed;
        return int.TryParse(value.ToString(), out var text) ? text : null;
    }

    public string SelectedNodeName => SelectedItemOrNull switch
    {
        GraphNodeVm node when !string.IsNullOrWhiteSpace(node.Label) => node.Label,
        GraphChipVm chip when !string.IsNullOrWhiteSpace(chip.Label) => chip.Label,
        GraphBoxVm box when !string.IsNullOrWhiteSpace(box.Label) => box.Label,
        { } item => item.Id,
        _ => string.Empty
    };

    public string SelectedNodeType => SelectedItemOrNull switch
    {
        GraphNodeVm node => node.Type,
        GraphChipVm chip => chip.Type,
        GraphBoxVm => "server",
        _ => string.Empty
    };

    public string SelectedNodeTypeText => SelectedNodeType.ToLowerInvariant() switch
    {
        "server" => "SSH 服务器",
        "app" => "应用",
        "db" or "database" => "数据库",
        "service" => "服务",
        "container" => "容器",
        "deploy" => "部署",
        "domain" => "域名",
        "port" => "端口",
        "cert" => "证书",
        "system" => "系统",
        "mq" => "消息队列",
        _ => string.IsNullOrWhiteSpace(SelectedNodeType) ? "资产" : SelectedNodeType
    };

    public string SelectedNodeDetail => SelectedItemOrNull?.Tooltip ?? string.Empty;
    public string SelectedNodeId => _selectedId ?? string.Empty;
    public bool IsEmptyGraph => _allItems.Length == 0;

    public void SelectNode(string? id)
    {
        _selectedId = id;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedItemOrNull));
        OnPropertyChanged(nameof(SelectedNodeName));
        OnPropertyChanged(nameof(SelectedNodeType));
        OnPropertyChanged(nameof(SelectedNodeTypeText));
        OnPropertyChanged(nameof(SelectedNodeDetail));
        OnPropertyChanged(nameof(SelectedNodeId));
        UpdateSelectionRect();
        ApplyImpactHighlight(id);
        _ = RefreshEvidenceAsync(id);
    }
}
