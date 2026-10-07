using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

/// <summary>
/// 在 SSH 会话窗口内批量执行：把命令下发到已打开的终端。
/// 未打开的服务器会询问是否新建连接后执行；已打开的会先做安全检测（备用屏/全屏程序或未连接则跳过）。
/// </summary>
public sealed class SessionBatchExecViewModel : INotifyPropertyChanged
{
    private readonly MainViewModel _main;

    public SessionBatchExecViewModel(MainViewModel main, IEnumerable<SshServerConfig> servers)
    {
        _main = main;
        foreach (var server in servers.Where(s => !s.Disabled))
            Servers.Add(new BatchServerItem(server));
        RunCommand = new RelayCommand(_ => _ = RunAsync(), _ => !IsRunning);
        SelectAllCommand = new RelayCommand(p => SetAll(p as string == "all"));
    }

    public ObservableCollection<BatchServerItem> Servers { get; } = new();
    public ICommand RunCommand { get; }
    public ICommand SelectAllCommand { get; }

    private string _command = string.Empty;
    public string Command { get => _command; set { _command = value; OnPropertyChanged(); } }

    private string _statusMessage = string.Empty;
    public string StatusMessage { get => _statusMessage; set { _statusMessage = value; OnPropertyChanged(); } }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set { _isRunning = value; OnPropertyChanged(); (RunCommand as RelayCommand)?.RaiseCanExecuteChanged(); }
    }

    private void SetAll(bool selected)
    {
        foreach (var s in Servers)
            s.IsSelected = selected;
    }

    public async Task RunAsync()
    {
        if (IsRunning)
            return;
        var command = Command?.Trim();
        if (string.IsNullOrEmpty(command))
        {
            StatusMessage = "请输入命令";
            return;
        }

        var targets = Servers.Where(s => s.IsSelected).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "请勾选至少一台服务器";
            return;
        }

        // 未打开的服务器统一询问一次
        var notOpen = targets
            .Where(t => _main.Sessions.OfType<TerminalSessionViewModel>().All(x => x.Server.Id != t.Server.Id))
            .ToList();
        var openNotOpen = false;
        if (notOpen.Count > 0)
        {
            var answer = MessageBox.Show(
                $"有 {notOpen.Count} 台服务器尚未打开终端连接。\n是否新建终端连接后再执行该命令？\n（选“否”将跳过这些服务器）",
                "批量执行", MessageBoxButton.YesNo, MessageBoxImage.Question);
            openNotOpen = answer == MessageBoxResult.Yes;
        }

        IsRunning = true;
        StatusMessage = "执行中…";

        var sent = 0;
        var skipped = 0;
        foreach (var target in targets)
        {
            target.Status = "处理中…";
            var term = _main.Sessions.OfType<TerminalSessionViewModel>().FirstOrDefault(x => x.Server.Id == target.Server.Id);

            if (term is null)
            {
                if (!openNotOpen)
                {
                    target.Status = "已跳过（未打开）";
                    skipped++;
                    continue;
                }

                _main.ConnectTerminal(target.Server);
                term = await WaitForTerminalAsync(target.Server.Id);
                if (term is null)
                {
                    target.Status = "已跳过（连接失败）";
                    skipped++;
                    continue;
                }
            }

            if (!term.IsConnected)
            {
                target.Status = "已跳过（未连接）";
                skipped++;
                continue;
            }
            if (term.IsFullScreenActive)
            {
                target.Status = "已跳过（vim/top 等全屏程序运行中）";
                skipped++;
                continue;
            }

            term.SendInput(command + "\r");
            target.Status = "已发送";
            sent++;
        }

        IsRunning = false;
        StatusMessage = $"完成：已发送 {sent} 台，跳过 {skipped} 台";
    }

    private async Task<TerminalSessionViewModel?> WaitForTerminalAsync(string serverId, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var term = _main.Sessions.OfType<TerminalSessionViewModel>().FirstOrDefault(x => x.Server.Id == serverId);
            if (term is { IsConnected: true })
                return term;
            await Task.Delay(300);
        }
        return _main.Sessions.OfType<TerminalSessionViewModel>().FirstOrDefault(x => x.Server.Id == serverId);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
