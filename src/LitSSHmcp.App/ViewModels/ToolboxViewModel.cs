using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Toolbox;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.ViewModels;

public sealed class ToolboxViewModel : INotifyPropertyChanged
{
    private readonly ToolboxService _service;
    private readonly IUserDialogService _dialogs;
    private readonly List<ToolboxItemViewModel> _all = new();
    private readonly Dictionary<Guid, ToolboxItemViewModel> _cache = new();
    private bool _initialized;
    private bool _busy;
    private bool _isListView;

    public ToolboxViewModel(ToolboxService service, IUserDialogService dialogs)
    {
        _service = service;
        _dialogs = dialogs;
        Filters = new[] { "全部", "常用", "程序", "文件", "网络", "脚本", "内置工具" };
        Groups.Add("全部分组");
        _selectedGroup = "全部分组";
        _isListView = string.Equals(UiPrefs.GetString("toolbox.view", "card"), "list",
            StringComparison.OrdinalIgnoreCase);
        LaunchCommand = new RelayCommand(p => _ = LaunchAsync(p as ToolboxItemViewModel));
        PinCommand = new RelayCommand(p => _ = PinAsync(p as ToolboxItemViewModel));
        DeleteCommand = new RelayCommand(p => _ = DeleteAsync(p as ToolboxItemViewModel));
        RefreshCommand = new RelayCommand(_ => _ = LoadAsync());
    }

    public event Action? OpenWindowRequested;
    public event Action<string>? BuiltinRequested;

    public ToolboxService Service => _service;
    public ObservableCollection<ToolboxItemViewModel> Items { get; } = new();
    public ObservableCollection<string> Groups { get; } = new();
    public IReadOnlyList<string> Filters { get; }

    public bool IsCardView => !_isListView;
    public bool IsListView => _isListView;

    public void SetListView(bool value)
    {
        if (_isListView == value)
            return;
        _isListView = value;
        UiPrefs.SetString("toolbox.view", value ? "list" : "card");
        OnPropertyChanged(nameof(IsCardView));
        OnPropertyChanged(nameof(IsListView));
    }
    public ICommand LaunchCommand { get; }
    public ICommand PinCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand RefreshCommand { get; }

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

    private string _selectedFilter = "全部";
    public string SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (_selectedFilter == value) return;
            _selectedFilter = value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    private string _selectedGroup = "全部分组";
    public string SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (_selectedGroup == value) return;
            _selectedGroup = value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    private string _statusMessage = "本地工具启动器 · Ctrl+Alt+Space 快速启动 · 支持拖入程序、文件、文件夹、URL 或脚本";
    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    public bool Busy
    {
        get => _busy;
        private set { _busy = value; OnPropertyChanged(); }
    }

    public bool HasItems => Items.Count > 0;

    public async Task LoadAsync()
    {
        if (Busy)
            return;
        Busy = true;
        try
        {
            await _service.InitializeAsync();
            _initialized = true;
            ReloadFromService();
        }
        finally
        {
            Busy = false;
        }
    }

    public void RequestOpenWindow() => OpenWindowRequested?.Invoke();

    public async Task<int> AddFromPathsAsync(IEnumerable<string> paths)
    {
        await EnsureInitializedAsync();
        var added = _service.AddFromPaths(paths);
        if (added > 0)
        {
            ReloadFromService();
            StatusMessage = $"已添加 {added} 个工具。";
        }
        return added;
    }

    public async Task AddItemAsync(ToolboxItem item)
    {
        await EnsureInitializedAsync();
        await _service.AddAsync(item);
        ReloadFromService();
        StatusMessage = "已添加工具：" + item.Name;
    }

    public async Task UpdateItemAsync(ToolboxItem item)
    {
        await _service.UpdateAsync(item);
        ReloadFromService();
        StatusMessage = "已更新工具：" + item.Name;
    }

    public async Task SetGroupAsync(ToolboxItemViewModel? vm, string group)
    {
        if (vm is null)
            return;
        vm.Model.Group = group.Trim();
        await _service.UpdateAsync(vm.Model);
        ReloadFromService();
        StatusMessage = string.IsNullOrWhiteSpace(vm.Model.Group)
            ? "已清除分组：" + vm.Name
            : $"已将「{vm.Name}」设置到分组「{vm.Model.Group}」";
    }

    public async Task MoveBeforeAsync(ToolboxItemViewModel source, ToolboxItemViewModel target)
        => await MoveRelativeAsync(source, target, after: false);

    public async Task MoveRelativeAsync(ToolboxItemViewModel source, ToolboxItemViewModel target, bool after)
    {
        await _service.MoveRelativeAsync(source.Model.Id, target.Model.Id, after);
        ReloadFromService();
        StatusMessage = $"已调整顺序：{source.Name}";
    }

    public async Task DeleteAsync(ToolboxItemViewModel? vm)
    {
        if (vm is null || !vm.CanEdit)
            return;
        if (!_dialogs.Confirm($"确定删除工具「{vm.Name}」？", "删除工具", warning: false))
            return;

        await _service.RemoveAsync(vm.Model);
        ReloadFromService();
        StatusMessage = "已删除工具：" + vm.Name;
    }

    public async Task LaunchAsync(ToolboxItemViewModel? vm)
    {
        if (vm is null)
            return;

        if (vm.Model.Type == ToolboxItemType.Builtin)
        {
            if (!string.IsNullOrWhiteSpace(vm.Model.BuiltinId))
                BuiltinRequested?.Invoke(vm.Model.BuiltinId);
            return;
        }

        var result = await _service.LaunchAsync(vm.Model);
        StatusMessage = result.Message;
        if (!result.Success)
            _dialogs.Warn(result.Message, "工具箱");
        else
            ApplyFilter();
    }

    private async Task PinAsync(ToolboxItemViewModel? vm)
    {
        if (vm is null)
            return;
        await _service.TogglePinAsync(vm.Model);
        ReloadFromService();
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized)
            return;
        await LoadAsync();
    }

    private void ReloadFromService()
    {
        var currentIds = _service.Items.Select(i => i.Id).ToHashSet();
        foreach (var stale in _cache.Keys.Where(id => !currentIds.Contains(id)).ToList())
            _cache.Remove(stale);

        _all.Clear();
        foreach (var item in _service.Items)
        {
            if (_cache.TryGetValue(item.Id, out var vm) && ReferenceEquals(vm.Model, item))
            {
                _all.Add(vm);
                continue;
            }

            vm = new ToolboxItemViewModel(item);
            _cache[item.Id] = vm;
            _all.Add(vm);
            _ = vm.LoadIconAsync();
        }

        RefreshGroups();
        ApplyFilter();
    }

    private void RefreshGroups()
    {
        var current = _all
            .Select(i => i.Model.Group)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        Groups.Clear();
        Groups.Add("全部分组");
        foreach (var group in current)
            Groups.Add(group);

        if (!Groups.Contains(_selectedGroup))
            SelectedGroup = "全部分组";
        else
            OnPropertyChanged(nameof(SelectedGroup));
    }

    private void ApplyFilter()
    {
        IEnumerable<ToolboxItemViewModel> query = _all;

        if (!string.Equals(_selectedGroup, "全部分组", StringComparison.Ordinal))
            query = query.Where(i => string.Equals(i.Model.Group, _selectedGroup, StringComparison.CurrentCultureIgnoreCase));

        query = _selectedFilter switch
        {
            "常用" => query.Where(i => i.IsPinned || i.Model.LastUsedAt is not null),
            "程序" => query.Where(i => i.Model.Type == ToolboxItemType.Application),
            "文件" => query.Where(i => i.Model.Type is ToolboxItemType.File or ToolboxItemType.Folder),
            "网络" => query.Where(i => i.Model.Type == ToolboxItemType.Url),
            "脚本" => query.Where(i => i.Model.Type == ToolboxItemType.Script),
            "内置工具" => query.Where(i => i.Model.Type == ToolboxItemType.Builtin),
            _ => query
        };

        var keyword = _searchText.Trim();
        if (keyword.Length > 0)
        {
            query = query.Where(i =>
                i.Name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
                i.Model.Description.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
                i.Model.Target.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
                i.Model.Group.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
                i.Model.Tags.Contains(keyword, StringComparison.CurrentCultureIgnoreCase));
        }

        var list = query
            .OrderBy(i => i.Model.SortOrder)
            .ThenBy(i => i.Model.CreatedAt)
            .ToList();

        Items.Clear();
        foreach (var item in list)
            Items.Add(item);
        OnPropertyChanged(nameof(HasItems));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ToolboxItemViewModel : INotifyPropertyChanged
{
    public ToolboxItemViewModel(ToolboxItem model) => Model = model;

    public ToolboxItem Model { get; }
    public string Name => Model.Name;
    public string Description => Model.Description;
    public string TypeText => ToolboxTypeText.For(Model.Type);
    public bool IsPinned => Model.IsPinned;
    public bool CanEdit => Model.Type != ToolboxItemType.Builtin;
    public string Group => Model.Group;
    public bool HasGroup => !string.IsNullOrWhiteSpace(Model.Group);
    public string Tags => Model.Tags;
    public string MetaToolTip => string.Join(Environment.NewLine, new[]
    {
        string.IsNullOrWhiteSpace(Model.Group) ? null : "分组：" + Model.Group,
        string.IsNullOrWhiteSpace(Model.Tags) ? null : "标签：" + Model.Tags,
        "目标：" + Model.Target
    }.Where(v => v is not null));

    private bool _showInsertBefore;
    public bool ShowInsertBefore
    {
        get => _showInsertBefore;
        set { if (_showInsertBefore == value) return; _showInsertBefore = value; OnPropertyChanged(); }
    }

    private bool _showInsertAfter;
    public bool ShowInsertAfter
    {
        get => _showInsertAfter;
        set { if (_showInsertAfter == value) return; _showInsertAfter = value; OnPropertyChanged(); }
    }

    public bool IsMissing
    {
        get
        {
            if (Model.Type is ToolboxItemType.Builtin or ToolboxItemType.Url or ToolboxItemType.Script)
                return false;
            return Model.Type == ToolboxItemType.Folder
                ? !Directory.Exists(Model.Target)
                : !File.Exists(Model.Target);
        }
    }

    public SymbolRegular FallbackSymbol => Model.Type switch
    {
        ToolboxItemType.Application => SymbolRegular.Toolbox24,
        ToolboxItemType.File => SymbolRegular.Document24,
        ToolboxItemType.Folder => SymbolRegular.Folder24,
        ToolboxItemType.Url => SymbolRegular.Globe24,
        ToolboxItemType.Script => SymbolRegular.Code24,
        _ => Model.BuiltinId switch
        {
            "password" => SymbolRegular.Key24,
            "password-vault" => SymbolRegular.ShieldKeyhole24,
            "vault" => SymbolRegular.LockClosed24,
            _ => SymbolRegular.Wrench24
        }
    };

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            _icon = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => _icon is not null;

    public async Task LoadIconAsync()
    {
        var path = await ToolboxIconProvider.GetIconPathAsync(Model);
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            Icon = bitmap;
        }
        catch
        {
            // 图标失败使用类型图标
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
