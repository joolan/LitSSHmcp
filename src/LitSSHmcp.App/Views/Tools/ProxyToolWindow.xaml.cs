using System.Collections.ObjectModel;
using System.IO;
using System.Net;
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
    private bool _loaded;
    private bool _loading;
    private bool _starting;
    private bool _clearUpstreamPassword;

    public ProxyToolWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) =>
        {
            RefreshStatus();
            if (MainTabs.SelectedIndex == 3)
                RefreshConnections();
            else if (MainTabs.SelectedIndex == 4)
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
            RefreshConnections();
            RefreshLogs();
            FooterStatus.Text = "配置文件：" + _service.ConfigPath;
            _loaded = true;
            _ = RefreshFirewallStatusAsync();
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
        BindAddressBox.ItemsSource = ProxyNetworkInterfaces.GetBindAddressOptions(options.BindAddress);
        BindAddressBox.SelectedValue = options.BindAddress;
        if (BindAddressBox.SelectedValue is null)
            BindAddressBox.Text = options.BindAddress;
        HttpPortBox.Text = options.HttpPort.ToString();
        Socks5PortBox.Text = options.Socks5Port.ToString();
        HttpEnabledCheck.IsChecked = options.HttpEnabled;
        Socks5EnabledCheck.IsChecked = options.Socks5Enabled;
        EnabledCheck.IsChecked = options.Enabled;
        AnonymousCheck.IsChecked = options.AllowAnonymous;
        AllowedClientsBox.Text = string.Join(Environment.NewLine, options.AllowedClientCidrs);
        AllowedDomainsBox.Text = string.Join(Environment.NewLine, options.AllowedDomains);
        BlockedDomainsBox.Text = string.Join(Environment.NewLine, options.BlockedDomains);
        AllowedPortsBox.Text = string.Join(",", options.AllowedPorts);
        AllowAllPortsCheck.IsChecked = options.AllowAllPorts;
        BlockPrivateCheck.IsChecked = options.BlockPrivateDestinations;
        TemporaryBox.Text = options.TemporaryEnableMinutes.ToString();
        AuthFailureLimitBox.Text = options.AuthFailureLimit.ToString();
        AuthFailureWindowBox.Text = options.AuthFailureWindowSeconds.ToString();
        AuthFailureBlockBox.Text = options.AuthFailureBlockSeconds.ToString();
        MaxTotalBox.Text = options.MaxTotalConnections.ToString();
        MaxPerClientBox.Text = options.MaxConnectionsPerClient.ToString();
        IdleTimeoutBox.Text = options.IdleTimeoutSeconds.ToString();
        AllowedPortsBox.IsEnabled = !options.AllowAllPorts;

        UpstreamEnabledCheck.IsChecked = options.UpstreamProxyEnabled;
        UpstreamTypeBox.SelectedIndex =
            string.Equals(options.UpstreamProxyType, "socks5", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        UpstreamHostBox.Text = options.UpstreamProxyHost;
        UpstreamPortBox.Text = options.UpstreamProxyPort > 0
            ? options.UpstreamProxyPort.ToString()
            : string.Empty;
        UpstreamUsernameBox.Text = options.UpstreamProxyUsername;
        UpstreamPasswordBox.Password = string.Empty;
        _clearUpstreamPassword = false;
        UpstreamPasswordHint.Text = string.IsNullOrWhiteSpace(options.UpstreamProxyPasswordProtected)
            ? "密码留空表示不设置或保持为空；保存时使用 DPAPI 加密。"
            : "已保存上游密码；留空保持不变，输入新密码则覆盖。保存时使用 DPAPI 加密。";
        UpdateUpstreamEnabledState();

        FirewallCommandsBox.Text = _firewall.BuildAddCommands(options);
        FooterVersion.Text = $"HTTP/SOCKS5 · {options.BindAddress}";
    }

    private bool ApplyUiToDocument()
    {
        var errors = new List<string>();
        var options = _document.Options;

        options.BindAddress = GetBindAddress();
        options.HttpEnabled = HttpEnabledCheck.IsChecked == true;
        options.Socks5Enabled = Socks5EnabledCheck.IsChecked == true;
        options.Enabled = EnabledCheck.IsChecked == true;
        options.AllowAnonymous = AnonymousCheck.IsChecked == true;
        options.AllowAllPorts = AllowAllPortsCheck.IsChecked == true;
        options.BlockPrivateDestinations = BlockPrivateCheck.IsChecked == true;

        options.HttpPort = ReadInt(HttpPortBox, "HTTP 端口", 1, 65535, errors);
        options.Socks5Port = ReadInt(Socks5PortBox, "SOCKS5 端口", 1, 65535, errors);
        options.TemporaryEnableMinutes = ReadInt(TemporaryBox, "临时开启分钟数", 0, 10080, errors);
        options.AuthFailureLimit = ReadInt(AuthFailureLimitBox, "认证失败上限", 0, 1000, errors);
        options.AuthFailureWindowSeconds = ReadInt(AuthFailureWindowBox, "认证失败统计窗口", 30, 86400, errors);
        options.AuthFailureBlockSeconds = ReadInt(AuthFailureBlockBox, "认证失败封禁时长", 30, 86400, errors);
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

        options.AllowedDomains = ParseDomainList(AllowedDomainsBox.Text, "白名单", errors);
        options.BlockedDomains = ParseDomainList(BlockedDomainsBox.Text, "黑名单", errors);

        options.UpstreamProxyEnabled = UpstreamEnabledCheck.IsChecked == true;
        options.UpstreamProxyType = (UpstreamTypeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "http";
        options.UpstreamProxyHost = UpstreamHostBox.Text.Trim();
        var upstreamPort = 0;
        if (!string.IsNullOrWhiteSpace(UpstreamPortBox.Text))
        {
            if (int.TryParse(UpstreamPortBox.Text.Trim(), out upstreamPort) &&
                upstreamPort is >= 1 and <= 65535)
            {
                // 有效端口
            }
            else
            {
                errors.Add("上游代理端口应为 1~65535");
                upstreamPort = 0;
            }
        }
        options.UpstreamProxyPort = upstreamPort;
        options.UpstreamProxyUsername = UpstreamUsernameBox.Text.Trim();
        if (!string.IsNullOrEmpty(UpstreamPasswordBox.Password))
        {
            options.UpstreamProxyPassword = UpstreamPasswordBox.Password;
            options.UpstreamProxyPasswordProtected = string.Empty;
            _clearUpstreamPassword = false;
        }
        if (_clearUpstreamPassword)
        {
            options.UpstreamProxyPassword = string.Empty;
            options.UpstreamProxyPasswordProtected = string.Empty;
        }
        if (options.UpstreamProxyEnabled)
        {
            if (string.IsNullOrWhiteSpace(options.UpstreamProxyHost))
                errors.Add("启用上游代理时，上游地址不能为空");
            if (options.UpstreamProxyPort is < 1 or > 65535)
                errors.Add("启用上游代理时，上游端口应为 1~65535");
        }

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
            var result = await _service.SaveAndApplyAsync(_document);
            FirewallCommandsBox.Text = _firewall.BuildAddCommands(_document.Options);
            FooterStatus.Text = result.Message;
            _ = RefreshFirewallStatusAsync();
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
            RefreshConnections();
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
            RefreshConnections();
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

    private void RefreshConnections()
    {
        var selected = (ConnectionsGrid.SelectedItem as ProxyConnectionSnapshot)?.Id;
        var list = _service.ActiveConnections;
        ConnectionsGrid.ItemsSource = list;
        if (selected is { } id)
            ConnectionsGrid.SelectedItem = list.FirstOrDefault(item => item.Id == id);
    }

    private void OnRefreshConnections(object sender, RoutedEventArgs e) => RefreshConnections();

    private void OnDisconnectSelected(object sender, RoutedEventArgs e)
    {
        if (ConnectionsGrid.SelectedItem is not ProxyConnectionSnapshot connection)
        {
            _dialogs.Warn("请先选择一条活动连接。", "断开连接");
            return;
        }
        if (!_dialogs.Confirm($"断开 {connection.Protocol} 连接 {connection.Client} → {connection.Target}？",
                "断开连接", warning: true, defaultNo: true))
            return;
        _service.Disconnect(connection.Id);
        RefreshConnections();
        FooterStatus.Text = "已请求断开选中连接。";
    }

    private void OnDisconnectAll(object sender, RoutedEventArgs e)
    {
        var count = _service.ActiveConnections.Count;
        if (count == 0)
        {
            FooterStatus.Text = "当前没有活动连接。";
            return;
        }
        if (!_dialogs.Confirm($"确定断开全部 {count} 条活动连接？", "全部断开",
                warning: true, defaultNo: true))
            return;
        var removed = _service.DisconnectAll();
        RefreshConnections();
        FooterStatus.Text = $"已请求断开 {removed} 条连接。";
    }

    private void OnCheckFirewall(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("检测防火墙状态", RefreshFirewallStatusAsync);

    private async Task RefreshFirewallStatusAsync()
    {
        try
        {
            if (!ApplyUiToDocument())
                return;
            var statuses = await _firewall.GetStatusAsync(_document.Options);
            FirewallStatusText.Text = statuses.Count == 0
                ? "没有启用的代理端口。"
                : string.Join("；", statuses.Select(item =>
                    $"{item.Protocol} {item.Port}：{(item.Exists ? "已放行" : "未放行")}" +
                    (string.IsNullOrWhiteSpace(item.Detail) ? string.Empty : $"（{item.Detail}）")));
        }
        catch (Exception ex)
        {
            FirewallStatusText.Text = "检测失败：" + ex.Message;
        }
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
            : "匿名模式关闭时，至少需要一个启用账号；账号修改后自动保存并热更新。密码只能重置，无法查看。";
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
        SaveAccounts("账号已添加并保存。");
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

        if (account.Enabled && !result.Enabled && IsLastEnabledAccount(account))
        {
            _dialogs.Warn("匿名模式关闭且代理正在运行，至少需要一个启用账号；请先启用其他账号或开启匿名模式。",
                "编辑账号");
            return;
        }

        account.Username = result.Username;
        account.Note = result.Note;
        account.Enabled = result.Enabled;
        if (!string.IsNullOrEmpty(result.Password))
            account.SetPassword(result.Password);
        RefreshAccounts();
        SaveAccounts("账号已更新并保存。");
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
        SaveAccounts("密码已重置并保存。");
    }

    private void OnDeleteAccount(object sender, RoutedEventArgs e)
    {
        if (AccountsGrid.SelectedItem is not ProxyAccount account)
        {
            _dialogs.Warn("请先选择一个账号。", "删除账号");
            return;
        }
        if (IsLastEnabledAccount(account))
        {
            _dialogs.Warn("匿名模式关闭且代理正在运行，至少需要一个启用账号；请先启用其他账号或开启匿名模式。",
                "删除账号");
            return;
        }
        if (!_dialogs.Confirm($"确定删除账号“{account.Username}”？", "删除账号",
                warning: true, defaultNo: true))
            return;
        _accounts.Remove(account);
        RefreshAccounts();
        SaveAccounts("账号已删除并保存。");
    }

    /// <summary>
    /// 账号独立于其他页面的表单，增删改后立即持久化并热更新，
    /// 避免用户误以为还要到「监听与策略」里点保存。
    /// </summary>
    private void SaveAccounts(string message)
    {
        if (!_loaded)
        {
            FooterStatus.Text = "配置尚未加载完成，请稍后再试。";
            return;
        }
        FooterStatus.Text = "正在保存代理账号…";
        AsyncOperation.Run("保存代理账号", () => SaveAccountsAsync(message));
    }

    private async Task SaveAccountsAsync(string message)
    {
        try
        {
            var document = new ProxyDocument
            {
                Options = _document.Options.Clone(),
                Accounts = _accounts.Select(account => account.Clone()).ToList()
            };
            var result = await _service.SaveAndApplyAsync(document);
            _document.Accounts = _accounts.ToList();
            FooterStatus.Text = message + result.Message;
            UpdateAccountsHint();
        }
        catch (Exception ex)
        {
            FooterStatus.Text = "账号保存失败：" + ex.Message;
        }
    }

    private bool IsLastEnabledAccount(ProxyAccount account) =>
        account.Enabled &&
        _service.Status.Running &&
        !_document.Options.AllowAnonymous &&
        _accounts.Count(item => item.Enabled && !ReferenceEquals(item, account)) == 0;

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
        await RefreshFirewallStatusAsync();
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
        await RefreshFirewallStatusAsync();
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
        if (!_loaded)
        {
            FooterStatus.Text = "配置尚未加载完成，请稍后再试。";
            return;
        }
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

    private void OnUpstreamChanged(object sender, RoutedEventArgs e) =>
        UpdateUpstreamEnabledState();

    private void UpdateUpstreamEnabledState()
    {
        if (UpstreamPanel is not null)
            UpstreamPanel.IsEnabled = UpstreamEnabledCheck.IsChecked == true;
    }

    private void OnClearUpstreamPassword(object sender, RoutedEventArgs e)
    {
        _clearUpstreamPassword = true;
        UpstreamPasswordBox.Clear();
        UpstreamPasswordHint.Text = "已标记清除上游密码；点击顶部「保存设置」后生效。";
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MainTabs.SelectedIndex == 2)
            UpdateClientConfig();
        else if (MainTabs.SelectedIndex == 3)
            RefreshConnections();
        else if (MainTabs.SelectedIndex == 4)
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
        return ProxyNetworkInterfaces.FindLanIpv4() ?? "127.0.0.1";
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

    private string GetBindAddress()
    {
        var text = BindAddressBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(text) &&
            (IPAddress.TryParse(text, out _) || text is "0.0.0.0" or "::" or "*" or "localhost"))
            return text;
        return (BindAddressBox.SelectedValue as string ?? text).Trim();
    }

    private static int ReadInt(WpfTextBox box, string name, int min, int max, List<string> errors)
    {
        if (int.TryParse(box.Text.Trim(), out var value) && value >= min && value <= max)
            return value;
        errors.Add($"{name}应为 {min}~{max} 的整数");
        return Math.Clamp(value, min, max);
    }

    private static List<string> ParseDomainList(string text, string label, List<string> errors)
    {
        var result = new List<string>();
        foreach (var value in text.Split(
                     new[] { '\r', '\n', ',', '，', ';', '；' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = value.Trim();
            if (trimmed.Length == 0)
                continue;
            if (!ProxyDomainMatcher.IsValidPattern(trimmed))
                errors.Add($"{label}域名规则“{trimmed}”不合法");
            else
                result.Add(trimmed);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
