using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public class NodeOption
{
    public string NodeId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Display => $"{NodeId} ({Label})";
    public override string ToString() => Display;
}

public class RelationRow
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string NoteDisplay => string.IsNullOrEmpty(Note) ? string.Empty : $"  [{Note}]";
    public string Display => $"{From}  -->  {To}  ({Type})";
}

public class TopologyManageViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService;
    private string _fromText = string.Empty;
    private string _toText = string.Empty;
    private string _relationTypeText = "canAccess";
    private string _relationNote = string.Empty;
    private string _statusMessage = string.Empty;
    private RelationRow? _selectedRelation;

    public TopologyManageViewModel(IConfigService configService)
    {
        _configService = configService;

        RefreshCommand = new RelayCommand(_ => Load());
        AddRelationCommand = new RelayCommand(_ => AddRelation(),
            _ => !string.IsNullOrWhiteSpace(FromText) && !string.IsNullOrWhiteSpace(ToText));
        UpdateRelationCommand = new RelayCommand(_ => UpdateRelation(),
            _ => SelectedRelation != null && !string.IsNullOrWhiteSpace(FromText) && !string.IsNullOrWhiteSpace(ToText));
        DeleteRelationCommand = new RelayCommand(_ => DeleteRelation(), _ => SelectedRelation != null);

        Load();
    }

    public ObservableCollection<RelationRow> Relations { get; } = new();
    public ObservableCollection<NodeOption> NodeOptions { get; } = new();
    public string[] RelationTypes { get; } = { "runsOn", "connectsTo", "canAccess", "relatedTo" };

    public string FromText
    {
        get => _fromText;
        set { _fromText = value; OnPropertyChanged(); RaiseCanExecuteChanged(); }
    }

    public string ToText
    {
        get => _toText;
        set { _toText = value; OnPropertyChanged(); RaiseCanExecuteChanged(); }
    }

    public string RelationTypeText
    {
        get => _relationTypeText;
        set { _relationTypeText = value; OnPropertyChanged(); }
    }

    public string RelationNote
    {
        get => _relationNote;
        set { _relationNote = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public RelationRow? SelectedRelation
    {
        get => _selectedRelation;
        set
        {
            _selectedRelation = value;
            OnPropertyChanged();
            RaiseCanExecuteChanged();

            if (value != null)
            {
                FromText = value.From;
                ToText = value.To;
                RelationTypeText = value.Type;
                RelationNote = value.Note;
            }
        }
    }

    public ICommand RefreshCommand { get; }
    public ICommand AddRelationCommand { get; }
    public ICommand UpdateRelationCommand { get; }
    public ICommand DeleteRelationCommand { get; }

    private void RaiseCanExecuteChanged()
    {
        (AddRelationCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (UpdateRelationCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteRelationCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private async void Load()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();

            Relations.Clear();
            foreach (var rel in config.Relations)
                Relations.Add(new RelationRow { From = rel.From, To = rel.To, Type = rel.Type, Note = rel.Note ?? string.Empty });

            NodeOptions.Clear();
            foreach (var server in config.Servers)
                NodeOptions.Add(new NodeOption { NodeId = AssetNode.Ssh(server.Id), Label = server.Name });
            foreach (var app in config.Applications)
                NodeOptions.Add(new NodeOption { NodeId = AssetNode.App(app.Id), Label = app.Name });
            foreach (var ds in config.DataSources)
                NodeOptions.Add(new NodeOption { NodeId = AssetNode.Ds(ds.Id), Label = ds.Name });

            StatusMessage = $"已加载 {Relations.Count} 条关系; runsOn 表示'运行在'(应用/数据库→服务器), connectsTo 表示'连接到'(应用→数据库), canAccess 表示'可访问'(服务器→数据库)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败: {ex.Message}";
        }
    }

    private async void AddRelation()
    {
        try
        {
            var from = ParseNode(FromText);
            var to = ParseNode(ToText);
            if (from == null || to == null)
            {
                ShowRejection("节点格式错误, 需以 ssh:/ds:/app: 开头 (可从下拉选择)");
                return;
            }

            var type = string.IsNullOrWhiteSpace(RelationTypeText) ? "relatedTo" : RelationTypeText.Trim();
            var config = await _configService.LoadConfigAsync();
            if (!RelationRules.TryValidate(from, to, type, config.Relations, out var ruleError))
            {
                ShowRejection($"关系不合法: {ruleError}");
                return;
            }

            if (config.Relations.Any(r => r.From == from && r.To == to && r.Type == type))
            {
                ShowRejection("关系已存在");
                return;
            }

            config.Relations = config.Relations.Append(new RelationConfig
            {
                From = from,
                To = to,
                Type = type,
                Note = string.IsNullOrWhiteSpace(RelationNote) ? null : RelationNote.Trim()
            }).ToArray();

            await _configService.SaveConfigAsync(config);
            StatusMessage = $"已添加关系: {from} --> {to} ({type})";
            ClearEditor();
            Load();
        }
        catch (Exception ex)
        {
            StatusMessage = $"添加失败: {ex.Message}";
        }
    }

    private async void UpdateRelation()
    {
        if (SelectedRelation == null) return;

        try
        {
            var from = ParseNode(FromText);
            var to = ParseNode(ToText);
            if (from == null || to == null)
            {
                ShowRejection("节点格式错误, 需以 ssh:/ds:/app: 开头 (可从下拉选择)");
                return;
            }

            var type = string.IsNullOrWhiteSpace(RelationTypeText) ? "relatedTo" : RelationTypeText.Trim();
            var config = await _configService.LoadConfigAsync();
            var index = Array.FindIndex(config.Relations, r =>
                r.From == SelectedRelation.From && r.To == SelectedRelation.To && r.Type == SelectedRelation.Type);

            if (index < 0)
            {
                ShowRejection("原关系不存在, 请刷新");
                return;
            }

            var others = config.Relations.Where((_, i) => i != index).ToArray();
            if (!RelationRules.TryValidate(from, to, type, others, out var ruleError))
            {
                ShowRejection($"关系不合法: {ruleError}");
                return;
            }

            for (var i = 0; i < config.Relations.Length; i++)
            {
                if (i == index) continue;
                var r = config.Relations[i];
                if (r.From == from && r.To == to && r.Type == type)
                {
                    ShowRejection("目标关系已存在, 无需更新");
                    return;
                }
            }

            config.Relations[index] = new RelationConfig
            {
                From = from,
                To = to,
                Type = type,
                Note = string.IsNullOrWhiteSpace(RelationNote) ? null : RelationNote.Trim()
            };

            await _configService.SaveConfigAsync(config);
            StatusMessage = $"已更新关系: {from} --> {to} ({type})";
            ClearEditor();
            Load();
        }
        catch (Exception ex)
        {
            StatusMessage = $"更新失败: {ex.Message}";
        }
    }

    private async void DeleteRelation()
    {
        if (SelectedRelation == null) return;

        try
        {
            var config = await _configService.LoadConfigAsync();
            config.Relations = config.Relations
                .Where(r => !(r.From == SelectedRelation.From && r.To == SelectedRelation.To && r.Type == SelectedRelation.Type))
                .ToArray();
            await _configService.SaveConfigAsync(config);

            StatusMessage = $"已删除关系: {SelectedRelation.From} --> {SelectedRelation.To}";
            ClearEditor();
            Load();
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败: {ex.Message}";
        }
    }

    private void ClearEditor()
    {
        SelectedRelation = null;
        FromText = string.Empty;
        ToText = string.Empty;
        RelationNote = string.Empty;
    }

    /// <summary>被拒绝时弹窗提示（并同步状态栏），避免用户感知不到。</summary>
    private void ShowRejection(string message)
    {
        StatusMessage = message;
        MessageBox.Show(message, "拓扑关系", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string? ParseNode(string text)
    {
        var value = text?.Trim() ?? string.Empty;
        if (value.StartsWith(AssetNode.SshPrefix, StringComparison.Ordinal) ||
            value.StartsWith(AssetNode.DsPrefix, StringComparison.Ordinal) ||
            value.StartsWith(AssetNode.AppPrefix, StringComparison.Ordinal))
        {
            var labelIndex = value.IndexOf(" (", StringComparison.Ordinal);
            if (labelIndex > 0) value = value[..labelIndex];
            return value;
        }
        return null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
