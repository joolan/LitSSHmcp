using System.ComponentModel;
using System.Runtime.CompilerServices;
using LitSSHmcp.App.Services.Toolbox;

namespace LitSSHmcp.App.ViewModels;

public sealed record ToolboxTypeOption(ToolboxItemType Type, string Text);

public sealed class ToolboxItemEditViewModel : INotifyPropertyChanged
{
    private readonly ToolboxItem _source;

    public ToolboxItemEditViewModel(ToolboxItem? source = null)
    {
        _source = source ?? new ToolboxItem();
        Name = _source.Name;
        Description = _source.Description;
        Type = _source.Type;
        Target = _source.Target;
        Arguments = _source.Arguments;
        WorkingDirectory = _source.WorkingDirectory;
        ScriptShell = string.IsNullOrWhiteSpace(_source.ScriptShell) ? "cmd" : _source.ScriptShell;
        Group = _source.Group;
        Tags = _source.Tags;
        RunAsAdmin = _source.RunAsAdmin;
        IsPinned = _source.IsPinned;
    }

    public bool IsNew => string.IsNullOrWhiteSpace(_source.Name);
    public string WindowTitle => IsNew ? "添加工具" : "编辑工具";
    public IReadOnlyList<ToolboxItemType> Types { get; } = Enum.GetValues<ToolboxItemType>();
    public IReadOnlyList<ToolboxTypeOption> TypeOptions { get; } = new[]
    {
        new ToolboxTypeOption(ToolboxItemType.Application, "程序"),
        new ToolboxTypeOption(ToolboxItemType.File, "文件"),
        new ToolboxTypeOption(ToolboxItemType.Folder, "文件夹"),
        new ToolboxTypeOption(ToolboxItemType.Url, "URL"),
        new ToolboxTypeOption(ToolboxItemType.Script, "脚本")
    };
    public IReadOnlyList<string> ScriptShells { get; } = new[] { "cmd", "powershell" };

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    private string _description = string.Empty;
    public string Description
    {
        get => _description;
        set { _description = value; OnPropertyChanged(); }
    }

    private ToolboxItemType _type;
    public ToolboxItemType Type
    {
        get => _type;
        set
        {
            _type = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsScript));
            OnPropertyChanged(nameof(IsPathTarget));
            OnPropertyChanged(nameof(TargetHint));
        }
    }

    private string _target = string.Empty;
    public string Target
    {
        get => _target;
        set { _target = value; OnPropertyChanged(); }
    }

    private string _arguments = string.Empty;
    public string Arguments
    {
        get => _arguments;
        set { _arguments = value; OnPropertyChanged(); }
    }

    private string _workingDirectory = string.Empty;
    public string WorkingDirectory
    {
        get => _workingDirectory;
        set { _workingDirectory = value; OnPropertyChanged(); }
    }

    private string _scriptShell = "cmd";
    public string ScriptShell
    {
        get => _scriptShell;
        set { _scriptShell = value; OnPropertyChanged(); }
    }

    private bool _runAsAdmin;
    public bool RunAsAdmin
    {
        get => _runAsAdmin;
        set { _runAsAdmin = value; OnPropertyChanged(); }
    }

    private bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        set { _isPinned = value; OnPropertyChanged(); }
    }

    private string _group = string.Empty;
    public string Group
    {
        get => _group;
        set { _group = value; OnPropertyChanged(); }
    }

    private string _tags = string.Empty;
    public string Tags
    {
        get => _tags;
        set { _tags = value; OnPropertyChanged(); }
    }

    public bool IsScript => Type == ToolboxItemType.Script;
    public bool IsPathTarget => Type is ToolboxItemType.Application or ToolboxItemType.File or ToolboxItemType.Folder or ToolboxItemType.Script;
    public string TargetHint => Type switch
    {
        ToolboxItemType.Url => "例如：https://example.com",
        ToolboxItemType.Script => "脚本内容，或选择一个 .bat / .ps1 文件",
        ToolboxItemType.Folder => "选择一个文件夹",
        _ => "选择或输入完整路径"
    };

    public ToolboxItem ToModel()
    {
        var item = _source.Clone();
        item.Name = Name.Trim();
        item.Description = Description.Trim();
        item.Type = Type;
        item.Target = Target.Trim();
        item.Arguments = Arguments.Trim();
        item.WorkingDirectory = WorkingDirectory.Trim();
        item.ScriptShell = ScriptShell;
        item.Group = Group.Trim();
        item.Tags = Tags.Trim();
        item.RunAsAdmin = RunAsAdmin;
        item.IsPinned = IsPinned;
        item.UpdatedAt = DateTimeOffset.Now;
        return item;
    }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "请填写工具名称。";
        if (string.IsNullOrWhiteSpace(Target))
            return "请填写目标或脚本内容。";
        if (Type == ToolboxItemType.Url &&
            (!Uri.TryCreate(Target, UriKind.Absolute, out var uri) ||
             uri.Scheme is not ("http" or "https" or "mailto")))
            return "URL 仅支持 http / https / mailto。";
        return null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
