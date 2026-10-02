using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public class ApplicationEditViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService;
    private readonly ApplicationConfig? _editing;

    private string _name = string.Empty;
    private string _type = "java";
    private string _host = string.Empty;
    private string _portText = string.Empty;
    private string _description = string.Empty;
    private string _containerName = string.Empty;
    private string _appPath = string.Empty;
    private string _logPaths = string.Empty;
    private string _statusMessage = string.Empty;

    public event EventHandler<bool>? DialogClosed;

    public ApplicationEditViewModel(IConfigService configService, ApplicationConfig? application = null, ApplicationConfig? prefill = null)
    {
        _configService = configService;
        _editing = application;

        SaveCommand = new RelayCommand(_ => _ = SaveAsync());
        CancelCommand = new RelayCommand(_ => DialogClosed?.Invoke(this, false));

        if (application != null)
        {
            Name = application.Name;
            Type = application.Type;
            Host = application.Host ?? string.Empty;
            PortText = application.Port?.ToString() ?? string.Empty;
            Description = application.Description ?? string.Empty;
            ContainerName = application.ContainerName ?? string.Empty;
            AppPath = application.Path ?? string.Empty;
            LogPaths = application.LogPaths == null ? string.Empty : string.Join(Environment.NewLine, application.LogPaths);
        }
        else if (prefill != null)
        {
            // "待确认"节点确认时的新增预填（仍是新增模式，_editing 为 null）
            Name = prefill.Name;
            Type = string.IsNullOrWhiteSpace(prefill.Type) ? "java" : prefill.Type;
            Host = prefill.Host ?? string.Empty;
            PortText = prefill.Port?.ToString() ?? string.Empty;
            ContainerName = prefill.ContainerName ?? string.Empty;
            AppPath = prefill.Path ?? string.Empty;
            Description = prefill.Description ?? string.Empty;
        }
    }

    public string WindowTitle => _editing == null ? "添加应用" : "编辑应用";

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Type { get => _type; set => Set(ref _type, value); }
    public string Host { get => _host; set => Set(ref _host, value); }
    public string PortText { get => _portText; set => Set(ref _portText, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public string ContainerName { get => _containerName; set => Set(ref _containerName, value); }
    public string AppPath { get => _appPath; set => Set(ref _appPath, value); }
    public string LogPaths { get => _logPaths; set => Set(ref _logPaths, value); }

    public string StatusMessage
    {
        get => _statusMessage;
        set => Set(ref _statusMessage, value);
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            StatusMessage = "应用名称不能为空";
            return;
        }

        int? port = null;
        if (!string.IsNullOrWhiteSpace(PortText))
        {
            if (!int.TryParse(PortText.Trim(), out var value) || value < 1 || value > 65535)
            {
                StatusMessage = "端口必须是 1-65535 的数字";
                return;
            }
            port = value;
        }

        try
        {
            var config = await _configService.LoadConfigAsync();

            if (_editing == null)
            {
                config.Applications = config.Applications
                    .Concat(new[]
                    {
                        new ApplicationConfig
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            Name = Name.Trim(),
                            Type = string.IsNullOrWhiteSpace(Type) ? "java" : Type.Trim(),
                            Host = string.IsNullOrWhiteSpace(Host) ? null : Host.Trim(),
                            Port = port,
                            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
                            ContainerName = string.IsNullOrWhiteSpace(ContainerName) ? null : ContainerName.Trim(),
                            Path = string.IsNullOrWhiteSpace(AppPath) ? null : AppPath.Trim(),
                            LogPaths = SplitPaths(LogPaths)
                        }
                    })
                    .ToArray();
            }
            else
            {
                var target = config.Applications.FirstOrDefault(a => a.Id == _editing.Id);
                if (target == null)
                {
                    StatusMessage = "应用不存在，可能已被删除";
                    return;
                }

                target.Name = Name.Trim();
                target.Type = string.IsNullOrWhiteSpace(Type) ? "java" : Type.Trim();
                target.Host = string.IsNullOrWhiteSpace(Host) ? null : Host.Trim();
                target.Port = port;
                target.Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
                target.ContainerName = string.IsNullOrWhiteSpace(ContainerName) ? null : ContainerName.Trim();
                target.Path = string.IsNullOrWhiteSpace(AppPath) ? null : AppPath.Trim();
                target.LogPaths = SplitPaths(LogPaths);
            }

            await _configService.SaveConfigAsync(config);
            DialogClosed?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败: {ex.Message}";
        }
    }

    private static string[] SplitPaths(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.TrimEnd('\r').Trim())
                .Where(x => x.Length > 0)
                .ToArray();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
