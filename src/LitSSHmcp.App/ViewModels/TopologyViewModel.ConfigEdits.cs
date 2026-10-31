using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Topology;

namespace LitSSHmcp.App.ViewModels;

public partial class TopologyViewModel
{
    private async Task<bool> TryAddRunsOnAsync(string id, string? oldParent, string newParent)
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var server = FindServerNode(config, newParent);
            if (server is null)
            {
                RejectDrag("未找到目标服务器");
                return false;
            }
            if (string.IsNullOrWhiteSpace(server.AssetKey))
            {
                RejectDrag($"服务器 {server.Name} 没有 AssetKey，无法作为运行环境");
                return false;
            }

            var nodeKey = NodeKey(id);
            var app = config.Applications.FirstOrDefault(a =>
                string.Equals(a.Id, nodeKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.AssetKey, nodeKey, StringComparison.OrdinalIgnoreCase));
            var datasource = app is null
                ? config.DataSources.FirstOrDefault(d =>
                    string.Equals(d.Id, nodeKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(d.AssetKey, nodeKey, StringComparison.OrdinalIgnoreCase))
                : null;

            if (app is null && datasource is null)
            {
                RejectDrag("只有应用或数据源可以拖入服务器运行环境");
                return false;
            }

            if (app is not null)
            {
                app.RuntimeKind = RuntimeEnvironmentKind.SshServer;
                app.RuntimeSshAssetKey = server.AssetKey;
                app.RuntimeDockerAssetKey = null;
                app.RuntimeContainerName = null;
                app.RuntimeComposeService = null;
            }
            else if (datasource is not null)
            {
                datasource.RuntimeKind = RuntimeEnvironmentKind.SshServer;
                datasource.RuntimeSshAssetKey = server.AssetKey;
                datasource.RuntimeDockerAssetKey = null;
                datasource.RuntimeContainerName = null;
                datasource.RuntimeComposeService = null;
            }

            var assetId = app?.Id ?? datasource!.Id;
            var errors = AssetRuntimeValidator.Validate(config).Issues
                .Where(i => i.AssetId == assetId && i.Severity == AssetRuntimeIssueSeverity.Error)
                .ToArray();
            if (errors.Length > 0)
            {
                RejectDrag(string.Join("；", errors.Take(2).Select(e => e.Message)));
                return false;
            }

            config.Relations = config.Relations
                .Where(r => !(r.From == id && r.Type == "runsOn"))
                .ToArray();
            await _configService.SaveConfigAsync(config);
            return true;
        }
        catch (Exception ex)
        {
            RejectDrag($"建立运行环境失败: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> TryRemoveRunsOnAsync(string id, string parent)
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var nodeKey = NodeKey(id);
            var app = config.Applications.FirstOrDefault(a =>
                string.Equals(a.Id, nodeKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.AssetKey, nodeKey, StringComparison.OrdinalIgnoreCase));
            var datasource = app is null
                ? config.DataSources.FirstOrDefault(d =>
                    string.Equals(d.Id, nodeKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(d.AssetKey, nodeKey, StringComparison.OrdinalIgnoreCase))
                : null;

            if (app is not null)
            {
                app.RuntimeKind = RuntimeEnvironmentKind.Standalone;
                app.RuntimeSshAssetKey = null;
                app.RuntimeDockerAssetKey = null;
                app.RuntimeContainerName = null;
                app.RuntimeComposeService = null;
            }
            else if (datasource is not null)
            {
                datasource.RuntimeKind = RuntimeEnvironmentKind.Standalone;
                datasource.RuntimeSshAssetKey = null;
                datasource.RuntimeDockerAssetKey = null;
                datasource.RuntimeContainerName = null;
                datasource.RuntimeComposeService = null;
            }
            else
            {
                RejectDrag("未找到该应用或数据源");
                return false;
            }

            config.Relations = config.Relations
                .Where(r => !(r.From == id && r.Type == "runsOn"))
                .ToArray();
            await _configService.SaveConfigAsync(config);
            return true;
        }
        catch (Exception ex)
        {
            RejectDrag($"解除运行环境失败: {ex.Message}");
            return false;
        }
    }

    private static SshServerConfig? FindServerNode(AppConfig config, string nodeId)
    {
        var key = NodeKey(nodeId);
        return config.Servers.FirstOrDefault(s =>
            string.Equals(s.Id, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.AssetKey, key, StringComparison.OrdinalIgnoreCase));
    }

    private static string NodeKey(string nodeId)
    {
        var index = nodeId.IndexOf(':');
        return index >= 0 ? nodeId[(index + 1)..] : nodeId;
    }
}
