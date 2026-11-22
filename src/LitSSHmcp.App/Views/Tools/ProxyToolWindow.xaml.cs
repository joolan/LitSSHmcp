using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Services.Proxy;
using Wpf.Ui.Controls;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace LitSSHmcp.App.Views.Tools;

public partial class ProxyToolWindow : FluentWindow
{
    private readonly ProxyHostService _service = AppServiceFactory.ProxyHost;
    private readonly ProxyFirewallService _firewall = new();
    private readonly UserDialogService _dialogs = new();
    private readonly DispatcherTimer _timer;
    private ProxyDocument _document = new();
    private ObservableCollection<ProxyAccount> _accounts = new();
    private bool _loading;
    private bool _starting;

    public ProxyToolWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) =>
        {
            RefreshStatus();
            if (MainTabs.SelectedIndex == 3)
                RefreshLogs();
        };
        Loaded += OnLoaded;
        Closed += (_, _) => _timer.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("加载代理配置", async () =>
        {
            await LoadAsync();
            if (IsLoaded)
                _timer.Start();
        });

    private async Task LoadAsync()
    {
        try
        {
            _loading = true;
            _document = await _service.LoadAsync();
            _accounts = new ObservableCollection<ProxyAccount>(_document.Accounts);
            AccountsGrid.ItemsSource = _accounts;
            PopulateControls();
            UpdateAccountsHint();
            UpdateClientConfig();
            RefreshStatus();
            RefreshLogs();
            FooterStatus.Text = "配置文件：" + _service.ConfigPath;
        }
        catch (Exception ex)
        {
            FooterStatus.Text = "加载代理配置失败：" + ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private void PopulateControls()
    {
        var options = _document.Options;
        BindAddressBox.Text = options.BindAddress;
        HttpPortBox.Text = options.HttpPort.ToString();
        Socks5PortBox.Text = options.Socks5Port.ToString();
        HttpEnabledCheck.IsChecked = options.HttpEnabled;
        Socks5EnabledCheck.IsChecked = options.Socks5Enabled;
        EnabledCheck.IsChecked = options.Enabled;
        AnonymousCheck.IsChecked = options.AllowAnonymous;
        AllowedClientsBox.Text = string.Join(Environment.NewLine, options.AllowedClientCidrs);
        AllowedPortsBox.Text = string.Join(",", options.AllowedPorts);
        AllowAllPortsCheck.IsChecked = options.AllowAllPorts;
        BlockPrivateCheck.IsChecked = options.BlockPrivateDestinations;
        TemporaryBox.Text = options.TemporaryEnableMinutes.ToString();
        MaxTotalBox.Text = options.MaxTotalConnections.ToString();
        MaxPerClientBox.Text = options.MaxConnectionsPerClient.ToString();
        IdleTimeoutBox.Text = options.IdleTimeoutSeconds.ToString();
        AllowedPortsBox.IsEnabled = !options.AllowAllPorts;
        FirewallCommandsBox.Text = _firewall.BuildAddCommands(options);
        FooterVersion.Text = $"HTTP/SOCKS5 · {options.BindAddress}";
    }

    private bool ApplyUiToDocument()
    {
        var errors = new List<string>();
        var options = _document.Options;

        options.BindAddress = BindAddressBox.Text.Trim();
        options.HttpEnabled = HttpEnabledCheck.IsChecked == true;
        options.Socks5Enabled = Socks5EnabledCheck.IsChecked == true;
        options.Enabled = EnabledCheck.IsChecked == true;
        options.AllowAnonymous = AnonymousCheck.IsChecked == true;
        options.AllowAllPorts = AllowAllPortsCheck.IsChecked == true;
        options.BlockPrivateDestinations = BlockPrivateCheck.IsChecked == true;

        options.HttpPort = ReadInt(HttpPortBox, "HTTP 端口", 1, 65535, errors);
        options.Socks5Port = ReadInt(Socks5PortBox, "SOCKS5 端口", 1, 65535, errors);
        options.TemporaryEnableMinutes = ReadInt(TemporaryBox, "临时开启分钟数", 0, 10080, errors);
        options.MaxTotalConnections = ReadInt(MaxTotalBox, "总并发", 1, 4096, errors);
        options.MaxConnectionsPerClient = ReadInt(MaxPerClientBox, "单 IP 并发", 1, 1024, errors);
        options.IdleTimeoutSeconds = ReadInt(IdleTimeoutBox, "空闲超时", 5, 3600, errors);

        if (!options.HttpEnabled && !options.Socks5Enabled)
            errors.Add("HTTP 与 SOCKS5 至少启用一个");
        if (options.HttpEnabled && options.Socks5Enabled && options.HttpPort == options.Socks5Port)
            errors.Add("HTTP 与 SOCKS5 不能使用同一个端口");
        if (options.MaxConnectionsPerClient > options.MaxTotalConnections)
            errors.Add("单 IP 并发不能大于总并发");
        if (!IsValidBindAddress(options.BindAddress))
            errors.Add("监听地址无效，请填写 127.0.0.1、0.0.0.0 或本机局域网 IPv4");

        var ports = new List<int>();
        if (!options.AllowAllPorts)
        {
            foreach (var value in AllowedPortsBox.Text.Split(
                         new[] { ',', '，', ';', '；', ' ', '\r', '\n', '\t' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(value.Trim(), out var port) && port is >= 1 and <= 65535)
                    ports.Add(port);
                else
                    errors.Add($"目标端口“{value}”不合法");
            }
            if (ports.Count == 0)
                errors.Add("目标端口允许列表为空；请添加端口或勾选允许所有端口");
        }
        options.AllowedPorts = ports.Distinct().OrderBy(port => port).ToList();

        var clients = new List<string>();
        foreach (var value in AllowedClientsBox.Text.Split(
                     new[] { '\r', '\n', ',', '，', ';', '；' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = value.Trim();
            if (trimmed.Length == 0)
                continue;
            if (!ProxyCidr.TryParse(trimmed, out _))
                errors.Add($"客户端白名单“{trimmed}”不是合法的 IP/CIDR");
            else
                clients.Add(trimmed);
        }
        options.AllowedClientCidrs = clients;

        options.Normalize();
        _document.Accounts = _accounts.ToList();

        if (errors.Count > 0)
        {
            FooterStatus.Text = "请检查：" + string.Join("；", errors.Distinct());
            return false;
        }
        return true;
    }

    private void OnSave(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("保存代理设置", SaveAsync);

    private async Task SaveAsync()
    {
        if (!ApplyUiToDocument())
            return;
        try
        {
            await _service.SaveAsync(_document);
            FirewallCommandsBox.Text = _firewall.BuildAddCommands(_document.Options);
            FooterStatus.Text = _service.Status.Running
                ? "设置已保存；监听地址/端口或账号变更需要停止后重新启动代理才会生效。"
                : "设置已保存。";
        }
        catch (Exception ex)
        {
            FooterStatus.Text = "保存失败：" + ex.Message;
        }
    }

    private void OnStart(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("启动代理", StartAsync);

    private async Task StartAsync()
    {
        if (_starting)
            return;
        if (!ApplyUiToDocument())
            return;

        if (AnonymousCheck.IsChecked != true &&
            !_accounts.Any(account => account.Enabled &&
                !string.IsNullOrWhiteSpace(account.Username) &&
                !string.IsNullOrWhiteSpace(account.PasswordHash)))
        {
            _dialogs.Warn("未开启匿名模式时，至少需要一个已启用且设置了密码的账号。", "启动代理");
            return;
        }

        if (AnonymousCheck.IsChecked == true &&
            !_dialogs.Confirm(
                "匿名模式允许局域网内任何知道地址的人直接使用代理。\n" +
                "建议同时配置客户端白名单，并尽量缩短临时开启时间。\n\n确认开启匿名模式并启动代理？",
                "启动代理", warning: true, defaultNo: true))
            return;

        _starting = true;
        StartButton.IsEnabled = false;
        FooterStatus.Text = "正在启动代理…";
        try
        {
            await _service.StartAsync(_document);
            FirewallCommandsBox.Text = _firewall.BuildAddCommands(_document.Options);
            FooterStatus.Text = "代理已启动。";
        }
        catch (Exception ex)
        {
            FooterStatus.Text = "启动失败：" + ex.Message;
            _dialogs.Error(ex.Message, "启动代理");
        }
        finally
        {
            _starting = false;
            RefreshStatus();
        }
    }

    private void OnStop(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("停止代理", StopAsync);

    private async Task StopAsync()
    {
        StopButton.IsEnabled = false;
        FooterStatus.Text = "正在停止代理…";
        try
        {
            await _service.StopAsync();
            FooterStatus.Text = "代理已停止。";
        }
        catch (Exception ex)
        {
            FooterStatus.Text = "停止失败：" + ex.Message;
        }
        finally
        {
            RefreshStatus();
        }
    }

    private void RefreshStatus()
    {
        var status = _service.Status;
        if (status.Running)
        {
            var expiry = status.ExpiresAt is { } expires
                ? " · 临时剩余 " + FormatRemaining(expires - DateTimeOffset.Now)
                : string.Empty;
            StatusTitle.Text = "代理运行中";
            StatusTitle.Foreground = TryFindResource("AppSuccessBrush") as System.Windows.Media.Brush;
            StatusDetail.Text =
                $"HTTP {status.HttpEndpoint ?? "关闭"} · SOCKS5 {status.Socks5Endpoint ?? "关闭"}" +
                $" · 连接 {status.ActiveConnections} · 请求 {status.TotalRequests}" +
                $" · 流量 {FormatBytes(status.BytesIn + status.BytesOut)}{expiry} · 退出 App 即停止";
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
        }
        else
        {
            StatusTitle.Text = "代理未启动";
            StatusTitle.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
            StatusDetail.Text = "默认仅本机；允许局域网访问前请先配置账号、白名单与目标策略。代理随 App 进程运行，退出 App 即停止。";
            StartButton.IsEnabled = !_starting;
            StopButton.IsEnabled = false;
        }

        var error = status.LastError ?? _service.LastError;
        StatusError.Text = error ?? string.Empty;
        StatusError.Visibility = string.IsNullOrWhiteSpace(error)
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (status.Running)
            SyncAccountStats();
    }

    private void SyncAccountStats()
    {
        var running = _service.RunningAccounts;
        if (running.Count == 0)
            return;

        var changed = false;
        foreach (var account in _accounts)
        {
            var live = running.FirstOrDefault(item =>
                string.Equals(item.Id, account.Id, StringComparison.Ordinal) ||
                string.Equals(item.Username, account.Username, StringComparison.OrdinalIgnoreCase));
            if (live is null)
                continue;
            if (live.RequestCount != account.RequestCount ||
                live.BytesIn != account.BytesIn ||
                live.BytesOut != account.BytesOut ||
                live.LastUsedAt != account.LastUsedAt)
            {
                account.RequestCount = live.RequestCount;
                account.BytesIn = live.BytesIn;
                account.BytesOut = live.BytesOut;
                account.LastUsedAt = live.LastUsedAt;
                changed = true;
            }
        }

        if (changed && AccountsGrid.ItemsSource is not null)
            AccountsGrid.Items.Refresh();
    }

    private void RefreshLogs()
    {
        var entries = _service.Log.Snapshot();
        Array.Reverse(entries);
        LogGrid.ItemsSource = entries;
    }

    private void UpdateAccountsHint()
    {
        AccountsHint.Text = _accounts.Count == 0
            ? "当前没有账号。匿名模式关闭时必须至少新增一个账号，否则代理无法启动。"
            : "匿名模式关闭时，至少需要一个启用账号；修改账号后请保存并重启代理。密码只能重置，无法查看。";
    }

    private void RefreshAccounts()
    {
        AccountsGrid.ItemsSource = null;
        AccountsGrid.ItemsSource = _accounts;
        UpdateAccountsHint();
    }

    private void OnAddAccount(object sender, RoutedEventArgs e)
    {
        var dialog = new ProxyAccountEditWindow { Owner = this };
        dialog.Configure(true, string.Empty, string.Empty, true);
        if (dialog.ShowDialog() != true || dialog.Result is not { } result)
            return;
        if (IsDuplicateUsername(result.Username, null))
        {
            _dialogs.Warn("用户名已存在（不区分大小写）。", "新增账号");
            return;
        }

        var account = new ProxyAccount
        {
            Username = result.Username,
            Note = result.Note,
            Enabled = result.Enabled
        };
        if (string.IsNullOrEmpty(result.Password))
        {
            _dialogs.Warn("新增账号必须设置密码。", "新增账号");
            return;
        }
        account.SetPassword(result.Password);
        _accounts.Add(account);
        RefreshAccounts();
        FooterStatus.Text = "账号已添加；请点击“保存设置”或直接启动代理以持久化。";
    }

    private void OnEditAccount(object sender, RoutedEventArgs e)
    {
        if (AccountsGrid.SelectedItem is not ProxyAccount account)
        {
            _dialogs.Warn("请先选择一个账号。", "编辑账号");
            return;
        }

        var dialog = new ProxyAccountEditWindow { Owner = this };
        dialog.Configure(false, account.Username, account.Note, account.Enabled);
        if (dialog.ShowDialog() != true || dialog.Result is not { } result)
            return;
        if (IsDuplicateUsername(result.Username, account))
        {
            _dialogs.Warn("用户名已存在（不区分大小写）。", "编辑账号");
            return;
        }

        account.Username = result.Username;
        account.Note = result.Note;
        account.Enabled = result.Enabled;
        if (!string.IsNullOrEmpty(result.Password))
            account.SetPassword(result.Password);
        RefreshAccounts();
        FooterStatus.Text = "账号已更新；请保存并重启代理使变更生效。";
    }

    private void OnResetPassword(object sender, RoutedEventArgs e)
    {
        if (AccountsGrid.SelectedItem is not ProxyAccount account)
        {
            _dialogs.Warn("请先选择一个账号。", "重置密码");
            return;
        }

        var dialog = new ProxyAccountEditWindow { Owner = this };
        dialog.Configure(false, account.Username, account.Note, account.Enabled);
        if (dialog.ShowDialog() != true || dialog.Result is not { } result)
            return;
        if (string.IsNullOrEmpty(result.Password))
        {
            _dialogs.Warn("未输入新密码，密码保持不变。", "重置密码");
            return;
        }
        account.SetPassword(result.Password);
        RefreshAccounts();
        FooterStatus.Text = "密码已重置；请保存并重启代理使变更生效。";
    }

    private void OnDeleteAccount(object sender, RoutedEventArgs e)
    {
        if (AccountsGrid.SelectedItem is not ProxyAccount account)
        {
            _dialogs.Warn("请先选择一个账号。", "删除账号");
            return;
        }
        if (!_dialogs.Confirm($"确定删除账号“{account.Username}”？", "删除账号",
                warning: true, defaultNo: true))
            return;
        _accounts.Remove(account);
        RefreshAccounts();
        FooterStatus.Text = "账号已删除；请保存并重启代理使变更生效。";
    }

    private bool IsDuplicateUsername(string username, ProxyAccount? except) =>
        _accounts.Any(account => !ReferenceEquals(account, except) &&
            string.Equals(account.Username, username, StringComparison.OrdinalIgnoreCase));

    private void OnAddFirewall(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("添加防火墙规则", AddFirewallAsync);

    private async Task AddFirewallAsync()
    {
        if (!ApplyUiToDocument())
            return;
        if (!_dialogs.Confirm(
                "将通过 UAC 提权执行 netsh，添加 HTTP / SOCKS5 入站规则（仅 private 配置文件）。\n继续？",
                "添加防火墙规则", warning: true, defaultNo: true))
            return;

        var result = await _firewall.AddRulesAsync(_document.Options);
        FirewallCommandsBox.Text = result.Commands;
        FooterStatus.Text = result.Message;
        if (result.Success)
            _dialogs.Info(result.Message, "防火墙");
        else
            _dialogs.Error(result.Message, "防火墙");
    }

    private void OnRemoveFirewall(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("删除防火墙规则", RemoveFirewallAsync);

    private async Task RemoveFirewallAsync()
    {
        if (!ApplyUiToDocument())
            return;
        if (!_dialogs.Confirm("将删除 LitSSH 代理的防火墙规则，继续？", "删除防火墙规则",
                warning: true, defaultNo: true))
            return;

        var result = await _firewall.RemoveRulesAsync(_document.Options);
        FirewallCommandsBox.Text = _firewall.BuildAddCommands(_document.Options);
        FooterStatus.Text = result.Message;
        if (result.Success)
            _dialogs.Info(result.Message, "防火墙");
        else
            _dialogs.Error(result.Message, "防火墙");
    }

    private void OnCopyFirewall(object sender, RoutedEventArgs e) =>
        CopyText(FirewallCommandsBox.Text, "防火墙命令");

    private void OnCopyHttpConfig(object sender, RoutedEventArgs e) =>
        CopyText(BuildHttpConfig(), "HTTP 代理配置");

    private void OnCopySocksConfig(object sender, RoutedEventArgs e) =>
        CopyText(BuildSocksConfig(), "SOCKS5 代理配置");

    private void OnCopyAllConfig(object sender, RoutedEventArgs e) =>
        CopyText(BuildAllConfig(), "客户端配置");

    private void OnRefreshLogs(object sender, RoutedEventArgs e) => RefreshLogs();

    private void OnClearLogs(object sender, RoutedEventArgs e)
    {
        _service.Log.Clear();
        RefreshLogs();
    }

    private void OnCopyLogs(object sender, RoutedEventArgs e)
    {
        var entries = _service.Log.Snapshot();
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.AppendLine(string.Join('\t', entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                entry.Protocol, entry.Client, entry.Username, entry.Target, entry.Result,
                entry.BytesIn, entry.BytesOut));
        }
        CopyText(builder.ToString(), "代理日志");
    }

    private void OnOpenConfigFolder(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("打开配置目录", OpenConfigFolderAsync);

    private async Task OpenConfigFolderAsync()
    {
        try
        {
            if (!File.Exists(_service.ConfigPath))
                await _service.SaveAsync(_document);
            NativeShell.RevealInExplorer(_service.ConfigPath);
        }
        catch (Exception ex)
        {
            FooterStatus.Text = "打开配置目录失败：" + ex.Message;
        }
    }

    private void OnAllowAllPortsChanged(object sender, RoutedEventArgs e)
    {
        if (AllowedPortsBox is not null)
            AllowedPortsBox.IsEnabled = AllowAllPortsCheck.IsChecked != true;
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MainTabs.SelectedIndex == 2)
            UpdateClientConfig();
        else if (MainTabs.SelectedIndex == 3)
            RefreshLogs();
    }

    private void OnAnonymousChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || AnonymousCheck.IsChecked != true)
            return;
        StatusError.Text = "匿名模式已开启：请确认客户端白名单与临时开启时间。";
        StatusError.Visibility = Visibility.Visible;
    }

    private void UpdateClientConfig()
    {
        ClientConfigBox.Text = BuildAllConfig();
    }

    private string BuildHttpConfig()
    {
        var options = _document.Options;
        if (!options.HttpEnabled)
            return "HTTP 代理未启用。";
        var host = AdvertisedHost();
        var account = FirstEnabledUsername();
        var builder = new StringBuilder();
        builder.AppendLine($"HTTP 代理：http://{host}:{options.HttpPort}");
        builder.AppendLine();
        builder.AppendLine("Windows 临时环境变量：");
        builder.AppendLine($"set HTTP_PROXY=http://{HostWithCredentials(host, options.HttpPort, account)}");
        builder.AppendLine($"set HTTPS_PROXY=http://{HostWithCredentials(host, options.HttpPort, account)}");
        builder.AppendLine();
        builder.AppendLine("curl：");
        builder.AppendLine($"curl -x http://{HostWithCredentials(host, options.HttpPort, account)} https://example.com");
        builder.AppendLine();
        builder.AppendLine("apt：");
        builder.AppendLine($"export http_proxy=http://{HostWithCredentials(host, options.HttpPort, account)}");
        builder.AppendLine("export https_proxy=$http_proxy");
        builder.AppendLine();
        builder.AppendLine("git：");
        builder.AppendLine($"git config --global http.proxy http://{HostWithCredentials(host, options.HttpPort, account)}");
        builder.AppendLine("git config --global --unset http.proxy");
        if (options.AllowAnonymous)
            builder.AppendLine().AppendLine("当前为匿名模式，无需账号密码。");
        else
            builder.AppendLine().AppendLine("密码无法查看；请使用创建/重置账号时保存的密码替换 <密码>。");
        return builder.ToString().TrimEnd();
    }

    private string BuildSocksConfig()
    {
        var options = _document.Options;
        if (!options.Socks5Enabled)
            return "SOCKS5 未启用。";
        var host = AdvertisedHost();
        var account = FirstEnabledUsername();
        var builder = new StringBuilder();
        builder.AppendLine($"SOCKS5 代理：socks5://{host}:{options.Socks5Port}");
        builder.AppendLine();
        builder.AppendLine("curl（socks5h 让代理解析域名）：");
        builder.AppendLine($"curl --socks5-hostname {UserInfo(account)}{host}:{options.Socks5Port} https://example.com");
        builder.AppendLine();
        builder.AppendLine("git：");
        builder.AppendLine($"git config --global http.proxy socks5h://{UserInfo(account)}{host}:{options.Socks5Port}");
        builder.AppendLine("git config --global --unset http.proxy");
        builder.AppendLine();
        builder.AppendLine("ssh（需要支持 SOCKS5 的 nc）：");
        builder.AppendLine($"ssh -o ProxyCommand=\"nc -X 5 -x {host}:{options.Socks5Port} %h %p\" user@target");
        if (options.AllowAnonymous)
            builder.AppendLine().AppendLine("当前为匿名模式，无需账号密码。");
        else
            builder.AppendLine().AppendLine("密码无法查看；请使用创建/重置账号时保存的密码替换 <密码>。");
        return builder.ToString().TrimEnd();
    }

    private string BuildAllConfig() =>
        BuildHttpConfig() + Environment.NewLine + Environment.NewLine + BuildSocksConfig();

    private string AdvertisedHost()
    {
        var bind = _document.Options.BindAddress;
        if (!string.IsNullOrWhiteSpace(bind) &&
            bind is not ("0.0.0.0" or "::" or "*" or "127.0.0.1" or "localhost"))
            return bind;
        return FindLanIpv4() ?? "127.0.0.1";
    }

    private string? FirstEnabledUsername() =>
        _accounts.FirstOrDefault(account => account.Enabled &&
            !string.IsNullOrWhiteSpace(account.Username))?.Username;

    private string HostWithCredentials(string host, int port, string? username)
    {
        var account = string.IsNullOrWhiteSpace(username) ? string.Empty : username;
        if (_document.Options.AllowAnonymous)
            return $"{host}:{port}";
        return $"{account}:<密码>@{host}:{port}";
    }

    private string UserInfo(string? username) =>
        _document.Options.AllowAnonymous ? string.Empty : $"{username ?? "user"}:<密码>@";

    private static string? FindLanIpv4()
    {
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up ||
                    network.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var unicast in network.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(address) ||
                        address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                        continue;
                    return address.ToString();
                }
            }
        }
        catch
        {
            // 网络接口枚举失败时回退到本机
        }
        return null;
    }

    private static bool IsValidBindAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (value is "0.0.0.0" or "::" or "*")
            return true;
        if (string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(value, out _);
    }

    private void CopyText(string text, string title)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            FooterStatus.Text = $"{title}为空。";
            return;
        }
        try
        {
            Clipboard.SetText(text);
            FooterStatus.Text = $"已复制{title}。";
        }
        catch (Exception ex)
        {
            FooterStatus.Text = $"复制{title}失败：{ex.Message}";
        }
    }

    private static int ReadInt(WpfTextBox box, string name, int min, int max, List<string> errors)
    {
        if (int.TryParse(box.Text.Trim(), out var value) && value >= min && value <= max)
            return value;
        errors.Add($"{name}应为 {min}~{max} 的整数");
        return Math.Clamp(value, min, max);
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
            return "已到期";
        return remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.##} MB",
        >= 1024L => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes} B"
    };
}
