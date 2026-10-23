using System.Text.Json;
using System.Text.Json.Nodes;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Storage;

/// <summary>
/// 配置三路合并（P2-6）：以加载时快照为基线，把本地修改合入磁盘上的最新配置。
/// 对象按字段递归合并；数组/标量若双方都改则报冲突，不做静默覆盖。
/// </summary>
internal static class ConfigMerge
{
    public static AppConfig Merge(AppConfig baseline, AppConfig local, AppConfig remote)
    {
        var b = JsonSerializer.SerializeToNode(baseline, AppConfigJson.Options) as JsonObject ?? new JsonObject();
        var l = JsonSerializer.SerializeToNode(local, AppConfigJson.Options) as JsonObject ?? new JsonObject();
        var r = JsonSerializer.SerializeToNode(remote, AppConfigJson.Options) as JsonObject ?? new JsonObject();

        var conflicts = new List<string>();
        var merged = MergeObject(b, l, r, string.Empty, conflicts);
        if (conflicts.Count > 0)
            throw new ConfigConflictException(conflicts);

        return JsonSerializer.Deserialize<AppConfig>(merged.ToJsonString(), AppConfigJson.Options)
               ?? new AppConfig();
    }

    private static JsonNode? MergeNode(JsonNode? baseline, JsonNode? local, JsonNode? remote,
        string path, List<string> conflicts)
    {
        if (JsonNode.DeepEquals(local, baseline))
            return remote?.DeepClone();
        if (JsonNode.DeepEquals(remote, baseline))
            return local?.DeepClone();
        if (JsonNode.DeepEquals(local, remote))
            return local?.DeepClone();

        if (baseline is JsonObject bo && local is JsonObject lo && remote is JsonObject ro)
            return MergeObject(bo, lo, ro, path, conflicts);

        conflicts.Add(string.IsNullOrEmpty(path) ? "$" : path);
        return remote?.DeepClone();
    }

    private static JsonObject MergeObject(JsonObject baseline, JsonObject local, JsonObject remote,
        string path, List<string> conflicts)
    {
        var result = new JsonObject();
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in baseline.Select(p => p.Key)
                     .Concat(local.Select(p => p.Key))
                     .Concat(remote.Select(p => p.Key)))
            names.Add(name);

        foreach (var name in names)
        {
            var hasBase = baseline.TryGetPropertyValue(name, out var baseValue);
            var hasLocal = local.TryGetPropertyValue(name, out var localValue);
            var hasRemote = remote.TryGetPropertyValue(name, out var remoteValue);

            if (!hasLocal && !hasRemote)
                continue;

            var childPath = string.IsNullOrEmpty(path) ? name : path + "." + name;

            if (!hasLocal)
            {
                // 本地删除了字段：远端没动则删除；远端也改了则冲突。
                if (!hasBase)
                    result[name] = remoteValue?.DeepClone();
                else if (!hasRemote)
                    continue;
                else if (JsonNode.DeepEquals(baseValue, remoteValue))
                    continue;
                else
                {
                    conflicts.Add(childPath);
                    result[name] = remoteValue?.DeepClone();
                }
                continue;
            }

            if (!hasRemote)
            {
                // 远端删除了字段：本地没动则删除；本地也改了则冲突。
                if (!hasBase)
                    result[name] = localValue?.DeepClone();
                else if (JsonNode.DeepEquals(baseValue, localValue))
                    continue;
                else
                {
                    conflicts.Add(childPath);
                    result[name] = localValue?.DeepClone();
                }
                continue;
            }

            if (!hasBase)
            {
                if (JsonNode.DeepEquals(localValue, remoteValue))
                    result[name] = localValue?.DeepClone();
                else
                {
                    conflicts.Add(childPath);
                    result[name] = localValue?.DeepClone();
                }
                continue;
            }

            var merged = MergeNode(baseValue, localValue, remoteValue, childPath, conflicts);
            if (merged is not null || localValue is null)
                result[name] = merged;
        }

        return result;
    }
}
