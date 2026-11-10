using System.Text.Json;
using System.Text.Json.Nodes;

namespace LitSSHmcp.Core.Services.Snapshot;

/// <summary>
/// 两次快照的差异：账号/公钥/密码状态、端口、systemd 失败单元、Docker 容器、nginx 域名与配置 hash。
/// 只读纯函数，供 MCP ssh_snapshot_diff 与后续定时巡检复用。
/// </summary>
public static class SnapshotDiffService
{
    public static Dictionary<string, object?> Compare(string? fromDataJson, string? toDataJson,
        int maxItems = 100)
    {
        var fromRoot = TryParse(fromDataJson);
        var toRoot = TryParse(toDataJson);
        if (fromRoot is null || toRoot is null)
        {
            return new Dictionary<string, object?>
            {
                ["available"] = false,
                ["reason"] = fromRoot is null ? "from 快照不可解析" : "to 快照不可解析"
            };
        }

        var result = new Dictionary<string, object?>
        {
            ["available"] = true
        };

        result["accounts"] = CompareAccounts(SectionData(fromRoot, "accounts"), SectionData(toRoot, "accounts"), maxItems);
        result["ports"] = CompareKeys(
            ReadPorts(SectionData(fromRoot, "portmap")),
            ReadPorts(SectionData(toRoot, "portmap")), maxItems);
        result["services"] = CompareServices(SectionData(fromRoot, "systemd"), SectionData(toRoot, "systemd"), maxItems);
        result["docker"] = CompareContainers(
            ReadContainers(SectionData(fromRoot, "docker")),
            ReadContainers(SectionData(toRoot, "docker")), maxItems);
        result["nginx"] = CompareNginx(SectionData(fromRoot, "nginx_tls"), SectionData(toRoot, "nginx_tls"), maxItems);
        return result;
    }

    private static JsonObject? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static JsonObject? SectionData(JsonObject root, string section) =>
        root["sections"]?[section]?["data"] as JsonObject;

    private static Dictionary<string, object?> CompareAccounts(JsonObject? from, JsonObject? to, int maxItems)
    {
        var fromUsers = ReadUsers(from);
        var toUsers = ReadUsers(to);
        var added = toUsers.Keys.Where(k => !fromUsers.ContainsKey(k)).OrderBy(k => k).Take(maxItems).ToList();
        var removed = fromUsers.Keys.Where(k => !toUsers.ContainsKey(k)).OrderBy(k => k).Take(maxItems).ToList();

        var passwordChanged = new List<Dictionary<string, object?>>();
        var riskChanged = new List<Dictionary<string, object?>>();
        foreach (var name in toUsers.Keys.Intersect(fromUsers.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var before = fromUsers[name];
            var after = toUsers[name];
            var beforePassword = (string?)before["passwordStatus"];
            var afterPassword = (string?)after["passwordStatus"];
            if (!string.Equals(beforePassword, afterPassword, StringComparison.Ordinal))
                passwordChanged.Add(new Dictionary<string, object?>
                {
                    ["user"] = name,
                    ["from"] = beforePassword,
                    ["to"] = afterPassword
                });
            var beforeRisk = (string?)before["riskLevel"];
            var afterRisk = (string?)after["riskLevel"];
            if (!string.Equals(beforeRisk, afterRisk, StringComparison.Ordinal))
                riskChanged.Add(new Dictionary<string, object?>
                {
                    ["user"] = name,
                    ["from"] = beforeRisk,
                    ["to"] = afterRisk
                });
        }

        var fromKeys = ReadKeyFingerprints(from);
        var toKeys = ReadKeyFingerprints(to);
        var addedKeys = toKeys.Keys.Where(k => !fromKeys.ContainsKey(k)).Take(maxItems).ToList();
        var removedKeys = fromKeys.Keys.Where(k => !toKeys.ContainsKey(k)).Take(maxItems).ToList();

        return new Dictionary<string, object?>
        {
            ["addedUsers"] = added,
            ["removedUsers"] = removed,
            ["passwordStatusChanged"] = passwordChanged.Take(maxItems).ToList(),
            ["riskChanged"] = riskChanged.Take(maxItems).ToList(),
            ["addedAuthorizedKeys"] = addedKeys,
            ["removedAuthorizedKeys"] = removedKeys
        };
    }

    private static Dictionary<string, JsonObject> ReadUsers(JsonObject? data)
    {
        var users = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (data?["users"] is not JsonArray arr)
            return users;
        foreach (var node in arr)
        {
            if (node is not JsonObject user)
                continue;
            var name = (string?)user["name"];
            if (!string.IsNullOrWhiteSpace(name))
                users[name] = user;
        }
        return users;
    }

    private static Dictionary<string, string> ReadKeyFingerprints(JsonObject? data)
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data?["authorizedKeys"] is not JsonArray arr)
            return keys;
        foreach (var node in arr)
        {
            if (node is not JsonObject file)
                continue;
            var user = (string?)file["user"] ?? "unknown";
            if (file["keys"] is not JsonArray keyArr)
                continue;
            foreach (var keyNode in keyArr)
            {
                if (keyNode is not JsonObject key)
                    continue;
                var fingerprint = (string?)key["fingerprint"];
                if (string.IsNullOrWhiteSpace(fingerprint))
                    continue;
                keys[$"{user}|{fingerprint}"] = fingerprint;
            }
        }
        return keys;
    }

    private static Dictionary<string, string> ReadPorts(JsonObject? data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data?["listeners"] is not JsonArray arr)
            return result;
        foreach (var node in arr)
        {
            if (node is not JsonObject listener)
                continue;
            var proto = (string?)listener["proto"] ?? "?";
            var bind = (string?)listener["bind"] ?? "*";
            var port = listener["port"]?.ToString() ?? "?";
            var process = (string?)listener["process"] ?? "?";
            var key = $"{proto}|{bind}|{port}|{process}";
            result[key] = $"{proto} {bind}:{port} ({process})";
        }
        return result;
    }

    private static Dictionary<string, string> ReadContainers(JsonObject? data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data?["containers"] is not JsonArray arr)
            return result;
        foreach (var node in arr)
        {
            if (node is not JsonObject container)
                continue;
            var name = (string?)container["name"] ?? (string?)container["id"];
            if (string.IsNullOrWhiteSpace(name))
                continue;
            result[name] = (string?)container["state"] ?? "?";
        }
        return result;
    }

    private static Dictionary<string, object?> CompareContainers(
        Dictionary<string, string> before, Dictionary<string, string> after, int maxItems)
    {
        var stateChanged = new List<Dictionary<string, object?>>();
        foreach (var name in before.Keys.Intersect(after.Keys, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.Equals(before[name], after[name], StringComparison.OrdinalIgnoreCase))
                stateChanged.Add(new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["from"] = before[name],
                    ["to"] = after[name]
                });
        }
        return new Dictionary<string, object?>
        {
            ["added"] = after.Keys.Where(k => !before.ContainsKey(k)).Take(maxItems)
                .Select(k => $"{k} ({after[k]})").ToList(),
            ["removed"] = before.Keys.Where(k => !after.ContainsKey(k)).Take(maxItems)
                .Select(k => $"{k} ({before[k]})").ToList(),
            ["stateChanged"] = stateChanged.Take(maxItems).ToList()
        };
    }

    private static Dictionary<string, object?> CompareKeys(
        Dictionary<string, string> before, Dictionary<string, string> after, int maxItems) => new()
    {
        ["added"] = after.Keys.Where(k => !before.ContainsKey(k)).Take(maxItems)
            .Select(k => after[k]).ToList(),
        ["removed"] = before.Keys.Where(k => !after.ContainsKey(k)).Take(maxItems)
            .Select(k => before[k]).ToList()
    };

    private static Dictionary<string, object?> CompareServices(JsonObject? from, JsonObject? to, int maxItems)
    {
        var before = ReadUnits(from);
        var after = ReadUnits(to);
        var newlyFailed = new List<string>();
        var recovered = new List<string>();
        foreach (var (unit, state) in after)
        {
            if (state.Equals("failed", StringComparison.OrdinalIgnoreCase))
            {
                before.TryGetValue(unit, out var oldState);
                if (!string.Equals(oldState, "failed", StringComparison.OrdinalIgnoreCase))
                    newlyFailed.Add(unit);
            }
        }
        foreach (var (unit, state) in before)
        {
            if (state.Equals("failed", StringComparison.OrdinalIgnoreCase) &&
                after.TryGetValue(unit, out var newState) &&
                !newState.Equals("failed", StringComparison.OrdinalIgnoreCase))
                recovered.Add(unit);
        }
        return new Dictionary<string, object?>
        {
            ["newlyFailed"] = newlyFailed.OrderBy(u => u).Take(maxItems).ToList(),
            ["recovered"] = recovered.OrderBy(u => u).Take(maxItems).ToList()
        };
    }

    private static Dictionary<string, string> ReadUnits(JsonObject? data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data?["units"] is not JsonArray arr)
            return result;
        foreach (var node in arr)
        {
            if (node is not JsonObject unit)
                continue;
            var name = (string?)unit["unit"];
            if (string.IsNullOrWhiteSpace(name))
                continue;
            result[name] = (string?)unit["active"] ?? "?";
        }
        return result;
    }

    private static Dictionary<string, object?> CompareNginx(JsonObject? from, JsonObject? to, int maxItems)
    {
        var beforeDomains = ReadStrings(from?["domains"], "domain");
        var afterDomains = ReadStrings(to?["domains"], "domain");
        var fromHash = (string?)from?["configHash"];
        var toHash = (string?)to?["configHash"];
        return new Dictionary<string, object?>
        {
            ["configChanged"] = !string.Equals(fromHash, toHash, StringComparison.Ordinal),
            ["fromConfigHash"] = fromHash,
            ["toConfigHash"] = toHash,
            ["addedDomains"] = afterDomains.Where(d => !beforeDomains.Contains(d)).OrderBy(d => d).Take(maxItems).ToList(),
            ["removedDomains"] = beforeDomains.Where(d => !afterDomains.Contains(d)).OrderBy(d => d).Take(maxItems).ToList()
        };
    }

    private static HashSet<string> ReadStrings(JsonNode? node, string property)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (node is not JsonArray arr)
            return result;
        foreach (var item in arr)
        {
            var value = item is JsonObject obj ? (string?)obj[property] : (string?)item;
            if (!string.IsNullOrWhiteSpace(value))
                result.Add(value);
        }
        return result;
    }
}
