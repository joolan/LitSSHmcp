using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.ViewModels;

/// <summary>提示词模板管理（助手设置 → 提示词模板 tab）。</summary>
public sealed class PromptTemplatesViewModel : INotifyPropertyChanged
{
    public PromptTemplatesViewModel()
    {
        AddCommand = new RelayCommand(_ => Add());
        DeleteCommand = new RelayCommand(_ => Delete(), _ => Selected is not null);
        SaveCommand = new RelayCommand(_ => Save());
        Load();
    }

    public ObservableCollection<PromptTemplateItem> Items { get; } = new();

    private PromptTemplateItem? _selected;
    public PromptTemplateItem? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
                (DeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private string _status = string.Empty;
    public string Status { get => _status; set => Set(ref _status, value); }

    public ICommand AddCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand SaveCommand { get; }

    private void Load()
    {
        Items.Clear();
        foreach (var t in AgentPromptStore.Load())
            Items.Add(new PromptTemplateItem(t));
        Selected = Items.FirstOrDefault();
    }

    private void Add()
    {
        var item = new PromptTemplateItem(new AgentPromptTemplate { Name = "新模板", Prompt = "请输入提示词…" });
        Items.Add(item);
        Selected = item;
        Status = "已新增，请编辑后点「保存」";
    }

    private void Delete()
    {
        if (Selected is null)
            return;
        Items.Remove(Selected);
        Selected = Items.FirstOrDefault();
        Status = "已删除，点「保存」生效";
    }

    public void Save()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items)
        {
            var name = item.Name.Trim().TrimStart('/').Trim();
            if (name.Length == 0 || string.IsNullOrWhiteSpace(item.Prompt))
            {
                Status = "有模板的命令名或提示词为空，未保存";
                return;
            }
            if (!names.Add(name))
            {
                Status = $"命令名重复：{name}";
                return;
            }
        }

        AgentPromptStore.Save(Items.Select(i => i.ToModel()));
        Status = "已保存（回到 AI 助手后生效）";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

/// <summary>列表/编辑用的可绑定项。</summary>
public sealed class PromptTemplateItem : INotifyPropertyChanged
{
    public PromptTemplateItem(AgentPromptTemplate model)
    {
        _name = model.Name;
        _description = model.Description;
        _prompt = model.Prompt;
    }

    private string _name;
    public string Name { get => _name; set => Set(ref _name, value); }

    private string _description;
    public string Description { get => _description; set => Set(ref _description, value); }

    private string _prompt;
    public string Prompt { get => _prompt; set => Set(ref _prompt, value); }

    public string CommandPreview => "/" + Name.Trim().TrimStart('/');

    public AgentPromptTemplate ToModel() => new() { Name = Name, Description = Description, Prompt = Prompt };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(Name))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CommandPreview)));
    }
}
