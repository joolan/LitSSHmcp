using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Toolbox;

namespace LitSSHmcp.App.ViewModels;

public sealed class ToolboxQuickLaunchViewModel : INotifyPropertyChanged
{
    private readonly ToolboxService _service;
    private readonly IUserDialogService _dialogs;
    private readonly List<ToolboxItemViewModel> _all = new();

    public ToolboxQuickLaunchViewModel(ToolboxService service, IUserDialogService dialogs)
    {
        _service = service;
        _dialogs = dialogs;
    }

    public event Action<string>? BuiltinRequested;

    public ObservableCollection<ToolboxItemViewModel> Results { get; } = new();

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    private ToolboxItemViewModel? _selectedItem;
    public ToolboxItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set { _selectedItem = value; OnPropertyChanged(); }
    }

    private string _statusMessage = "输入名称搜索，Enter 启动，Esc 关闭";
    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    public async Task LoadAsync()
    {
        await _service.InitializeAsync();
        _all.Clear();
        foreach (var item in _service.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.CreatedAt))
        {
            var vm = new ToolboxItemViewModel(item);
            _all.Add(vm);
            _ = vm.LoadIconAsync();
        }

        ApplyFilter();
    }

    public async Task<bool> LaunchAsync(ToolboxItemViewModel? item)
    {
        if (item is null)
            return false;

        if (item.Model.Type == ToolboxItemType.Builtin)
        {
            if (!string.IsNullOrWhiteSpace(item.Model.BuiltinId))
                BuiltinRequested?.Invoke(item.Model.BuiltinId);
            return true;
        }

        var result = await _service.LaunchAsync(item.Model);
        StatusMessage = result.Message;
        if (!result.Success)
            _dialogs.Warn(result.Message, "工具箱");
        return result.Success;
    }

    private void ApplyFilter()
    {
        var keyword = _searchText.Trim();
        IEnumerable<ToolboxItemViewModel> query = _all;
        if (keyword.Length > 0)
        {
            query = query.Where(i =>
                i.Name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
                i.Model.Description.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
                i.Model.Target.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
                i.Model.Group.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
                i.Model.Tags.Contains(keyword, StringComparison.CurrentCultureIgnoreCase));
        }

        var list = query.ToList();
        Results.Clear();
        foreach (var item in list)
            Results.Add(item);

        SelectedItem = Results.FirstOrDefault();
        StatusMessage = Results.Count == 0
            ? "没有匹配的工具。"
            : $"{Results.Count} 个结果 · Enter 启动 · Esc 关闭";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
