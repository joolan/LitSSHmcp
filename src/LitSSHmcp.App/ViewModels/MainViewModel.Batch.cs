using System.Windows.Input;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    private readonly List<SshServerConfig> _selectedServers = new();

    public int SelectedServerCount => _selectedServers.Count;
    public bool HasSelectedServers => SelectedServerCount > 0;
    private string[]? SelectedServerIdsOrNull =>
        _selectedServers.Count > 0 ? _selectedServers.Select(s => s.Id).ToArray() : null;

    public void SetSelectedServers(IEnumerable<SshServerConfig> servers)
    {
        _selectedServers.Clear();
        foreach (var server in servers.DistinctBy(s => s.Id))
            _selectedServers.Add(server);

        OnPropertyChanged(nameof(SelectedServerCount));
        OnPropertyChanged(nameof(HasSelectedServers));
        (_batchDisableCommand)?.RaiseCanExecuteChanged();
        (_batchEnableCommand)?.RaiseCanExecuteChanged();
        (_batchDeleteCommand)?.RaiseCanExecuteChanged();
        (_batchTestCommand)?.RaiseCanExecuteChanged();
    }

    private RelayCommand? _batchDisableCommand;
    public ICommand BatchDisableServersCommand =>
        _batchDisableCommand ??= new RelayCommand(_ => BatchSetDisabled(true), _ => HasSelectedServers);

    private RelayCommand? _batchEnableCommand;
    public ICommand BatchEnableServersCommand =>
        _batchEnableCommand ??= new RelayCommand(_ => BatchSetDisabled(false), _ => HasSelectedServers);

    private RelayCommand? _batchDeleteCommand;
    public ICommand BatchDeleteServersCommand =>
        _batchDeleteCommand ??= new RelayCommand(_ => BatchDeleteServers(), _ => HasSelectedServers);

    private RelayCommand? _batchTestCommand;
    public ICommand BatchTestServersCommand =>
        _batchTestCommand ??= new RelayCommand(_ => BatchTestServers(), _ => HasSelectedServers);

    private void BatchSetDisabled(bool disabled) => AsyncOperation.Run("批量更新服务器", async () =>
    {
        if (_selectedServers.Count == 0) return;
        try
        {
            var ids = _selectedServers.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var config = await _configService.LoadConfigAsync();
            var changed = 0;
            foreach (var server in config.Servers.Where(s => ids.Contains(s.Id)))
            {
                if (server.Disabled == disabled) continue;
                server.Disabled = disabled;
                changed++;
            }

            await _configService.SaveConfigAsync(config);
            StatusMessage = disabled
                ? $"已禁用 {changed} 台服务器"
                : $"已启用 {changed} 台服务器";
            LoadServers();
        }
        catch (Exception ex)
        {
            StatusMessage = $"批量更新失败: {ex.Message}";
        }
    });

    private void BatchDeleteServers() => AsyncOperation.Run("批量删除服务器", async () =>
    {
        if (_selectedServers.Count == 0) return;
        try
        {
            var config = await _configService.LoadConfigAsync();
            var ids = _selectedServers.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var targets = config.Servers.Where(s => ids.Contains(s.Id)).ToArray();
            if (targets.Length == 0) return;

            var nodeIds = targets.Select(s => AssetNode.Ssh(s.Id)).ToHashSet(StringComparer.Ordinal);
            var references = config.Relations
                .Where(r => nodeIds.Contains(r.From) || nodeIds.Contains(r.To))
                .ToArray();

            var message = $"确定删除选中的 {targets.Length} 台服务器吗？\n\n"
                + string.Join("\n", targets.Take(10).Select(s => "• " + s.Name))
                + (targets.Length > 10 ? $"\n… 等 {targets.Length} 台" : string.Empty);
            if (references.Length > 0)
                message += $"\n\n有 {references.Length} 条关系记录引用它们，将一并删除。";
            if (!_dialogs.Confirm(message, "批量删除服务器", defaultNo: true))
                return;

            config.Servers = config.Servers.Where(s => !ids.Contains(s.Id)).ToArray();
            config.Relations = config.Relations
                .Where(r => !nodeIds.Contains(r.From) && !nodeIds.Contains(r.To))
                .ToArray();
            await _configService.SaveConfigAsync(config);
            foreach (var nodeId in nodeIds)
                await CleanupDiscoveredEdgesAsync(nodeId);

            StatusMessage = $"已删除 {targets.Length} 台服务器";
            SetSelectedServers(Array.Empty<SshServerConfig>());
            LoadServers();
        }
        catch (Exception ex)
        {
            StatusMessage = $"批量删除失败: {ex.Message}";
        }
    });

    private void BatchTestServers() => AsyncOperation.Run("批量测试服务器", async () =>
    {
        if (_selectedServers.Count == 0) return;
        var success = 0;
        var failed = 0;
        var index = 0;
        foreach (var server in _selectedServers.ToArray())
        {
            index++;
            StatusMessage = $"正在测试 {server.Name} ({index}/{_selectedServers.Count})…";
            try
            {
                var result = await _sshService.ProbeConnectionAsync(server);
                if (result.Success) success++;
                else failed++;
            }
            catch
            {
                failed++;
            }
        }

        StatusMessage = $"批量测试完成：成功 {success} 台，失败 {failed} 台";
        _dialogs.Info(StatusMessage, "批量测试");
    });
}
