using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

// 工具分组的可视化开关。分组与工具的对应关系与 docs/TOOLS.md「工具分组」保持一致;
// 结果写入 config.json 的 tools.enabledGroups, 需重启 MCP 服务器(或 AI 客户端重连)后生效。
public class ToolGroupsViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService = new ConfigService();
    private string _statusMessage = string.Empty;

    public ObservableCollection<ToolGroupItem> Groups { get; } = new();

    public ToolGroupsViewModel()
    {
        Add(ToolGroups.Ssh, "SSH 服务器", "ssh_list_servers / ssh_get_server_status / ssh_test_connection");
        Add(ToolGroups.Command, "命令执行", "ssh_execute_command / ssh_get_command_history / ssh_execute_sudo / ssh_get_sudo_status");
        Add(ToolGroups.FileTransfer, "文件传输", "ssh_upload_file / ssh_download_file / ssh_list_files");
        Add(ToolGroups.Datasource, "数据源", "datasource_list / datasource_test_connection / datasource_get_sql_history");
        Add(ToolGroups.Mysql, "MySQL", "mysql_query / mysql_execute / mysql_explain / mysql_diagnostics");
        Add(ToolGroups.Postgres, "PostgreSQL", "postgres_query / postgres_execute / postgres_explain / postgres_diagnostics");
        Add(ToolGroups.Redis, "Redis", "redis_read / redis_execute / redis_diagnostics");
        Add(ToolGroups.Topology, "拓扑", "topology_get_overview / topology_get_dependencies / topology_discover");
        Add(ToolGroups.Guide, "指南 / 自检", "mcp_usage_guide / mcp_self_check");

        foreach (var group in Groups)
            group.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Warning));

        Load();
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    /// <summary>启用 mysql/redis 却未启用 datasource 时，AI 将拿不到 datasourceId。</summary>
    public string Warning =>
        !IsEnabled(ToolGroups.Datasource) && (IsEnabled(ToolGroups.Mysql) || IsEnabled(ToolGroups.Redis))
            ? "注意: 未启用「数据源」分组时, AI 无法用 datasource_list 获取 datasourceId"
            : string.Empty;

    private void Add(string key, string label, string tools) => Groups.Add(new ToolGroupItem(key, label, tools));

    private bool IsEnabled(string key) => Groups.FirstOrDefault(g => g.Key == key)?.IsEnabled ?? false;

    public async void Load()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var enabled = ToolGroups.ResolveEnabled(config.Tools);
            foreach (var group in Groups)
                group.IsEnabled = enabled.Contains(group.Key);

            StatusMessage = "已加载当前工具分组 (修改后需重启 MCP 服务器生效)";
            OnPropertyChanged(nameof(Warning));
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败: {ex.Message}";
        }
    }

    public async void Save()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var enabledKeys = Groups.Where(g => g.IsEnabled).Select(g => g.Key).ToArray();
            config.Tools.EnabledGroups = enabledKeys.Length == 0
                ? new[] { ToolGroups.NoneKeyword }
                : enabledKeys;

            await _configService.SaveConfigAsync(config);

            StatusMessage = enabledKeys.Length == 0
                ? "已保存: 未启用任何分组 (AI 将看不到任何工具)"
                : $"已保存 {enabledKeys.Length}/{Groups.Count} 个分组 (重启 MCP 服务器后生效)";
            OnPropertyChanged(nameof(Warning));
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败: {ex.Message}";
        }
    }

    public void EnableAll()
    {
        foreach (var group in Groups) group.IsEnabled = true;
    }

    public void DisableAll()
    {
        foreach (var group in Groups) group.IsEnabled = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public class ToolGroupItem : INotifyPropertyChanged
{
    private bool _isEnabled;

    public ToolGroupItem(string key, string label, string tools)
    {
        Key = key;
        Label = label;
        Tools = tools;
    }

    public string Key { get; }
    public string Label { get; }
    public string Tools { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
