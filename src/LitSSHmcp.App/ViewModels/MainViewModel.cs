using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService;
    private readonly ISshService _sshService;
    private SshServerConfig? _selectedServer;
    private string _commandText = string.Empty;
    private string _commandOutput = string.Empty;
    private string _statusMessage = string.Empty;

    public ObservableCollection<SshServerConfig> Servers { get; } = new();
    public ObservableCollection<CommandAuditLog> AuditLogs { get; } = new();

    public ICommand LoadServersCommand { get; }
    public ICommand AddServerCommand { get; }
    public ICommand EditServerCommand { get; }
    public ICommand DeleteServerCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand ExecuteCommandCommand { get; }

    public MainViewModel()
    {
        _configService = new ConfigService();
        _sshService = new SshService();

        LoadServersCommand = new RelayCommand(_ => LoadServers());
        AddServerCommand = new RelayCommand(_ => AddServer());
        EditServerCommand = new RelayCommand(_ => EditServer(), _ => SelectedServer != null);
        DeleteServerCommand = new RelayCommand(_ => DeleteServer(), _ => SelectedServer != null);
        TestConnectionCommand = new RelayCommand(_ => TestConnection(), _ => SelectedServer != null);
        ExecuteCommandCommand = new RelayCommand(_ => ExecuteCommand(), _ => SelectedServer != null && !string.IsNullOrWhiteSpace(CommandText));

        LoadServers();
    }

    public SshServerConfig? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (_selectedServer != value)
            {
                _selectedServer = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedServer));
                OnPropertyChanged(nameof(SelectedServerSudoType));
                RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelectedServer => SelectedServer != null;

    public string SelectedServerSudoType => SelectedServer?.SudoType switch
    {
        SudoType.None => "不启用",
        SudoType.CurrentUser => "当前用户sudo",
        SudoType.RootUser => "root用户",
        SudoType.CustomUser => $"指定用户: {SelectedServer?.SudoUsername}",
        _ => "未知"
    };

    public string CommandText
    {
        get => _commandText;
        set
        {
            if (_commandText != value)
            {
                _commandText = value;
                OnPropertyChanged();
                RaiseCanExecuteChanged();
            }
        }
    }

    public string CommandOutput
    {
        get => _commandOutput;
        set { _commandOutput = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    private void RaiseCanExecuteChanged()
    {
        (EditServerCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteServerCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (TestConnectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExecuteCommandCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private async void LoadServers()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            Servers.Clear();
            foreach (var server in config.Servers)
                Servers.Add(server);
            StatusMessage = $"已加载 {Servers.Count} 台服务器";
        }
        catch (Exception ex)
        {
            StatusMessage = $"错误: {ex.Message}";
        }
    }

    private void AddServer()
    {
        var editWindow = new ServerEditWindow
        {
            Owner = Application.Current.MainWindow
        };
        var editViewModel = new ServerEditViewModel(_configService, _sshService, editWindow);
        editWindow.DataContext = editViewModel;

        editViewModel.DialogClosed += (sender, result) =>
        {
            editWindow.Close();
            if (result) LoadServers();
        };

        editWindow.ShowDialog();
    }

    private void EditServer()
    {
        if (SelectedServer == null) return;

        var editWindow = new ServerEditWindow
        {
            Owner = Application.Current.MainWindow
        };
        var editViewModel = new ServerEditViewModel(_configService, _sshService, editWindow, SelectedServer);
        editWindow.DataContext = editViewModel;

        editViewModel.DialogClosed += (sender, result) =>
        {
            editWindow.Close();
            if (result) LoadServers();
        };

        editWindow.ShowDialog();
    }

    private async void DeleteServer()
    {
        if (SelectedServer == null) return;

        var result = MessageBox.Show(
            $"确定要删除服务器 \"{SelectedServer.Name}\" 吗？",
            "确认删除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            var config = await _configService.LoadConfigAsync();
            config.Servers = config.Servers.Where(s => s.Id != SelectedServer.Id).ToArray();
            await _configService.SaveConfigAsync(config);

            StatusMessage = $"已删除服务器: {SelectedServer.Name}";
            SelectedServer = null;
            LoadServers();
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败: {ex.Message}";
        }
    }

    private async void TestConnection()
    {
        if (SelectedServer == null) return;
        StatusMessage = "测试连接中...";
        var result = await _sshService.TestConnectionAsync(SelectedServer);
        StatusMessage = result ? "连接成功" : "连接失败";
    }

    private async void ExecuteCommand()
    {
        if (SelectedServer == null || string.IsNullOrWhiteSpace(CommandText)) return;

        StatusMessage = "执行命令中...";
        var result = await _sshService.ExecuteCommandAsync(SelectedServer, CommandText);

        CommandOutput = result.Success
            ? $"[退出码: {result.ExitCode}]\n{result.Output}"
            : $"[错误] {result.Error}";

        StatusMessage = result.Success ? "命令执行完成" : "命令执行失败";

        AuditLogs.Insert(0, new CommandAuditLog
        {
            ServerId = SelectedServer.Id,
            ServerName = SelectedServer.Name,
            Command = CommandText,
            Result = result.Output,
            Status = result.Success ? CommandStatus.Executed : CommandStatus.Failed,
            ExitCode = result.ExitCode,
            Timestamp = DateTime.UtcNow
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}