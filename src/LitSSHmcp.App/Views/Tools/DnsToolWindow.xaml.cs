using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Services.DnsServer;
using LitSSHmcp.Core.Services.Proxy;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace LitSSHmcp.App.Views.Tools;

/// <summary>
/// 轻量 DNS 管理窗口：监听/上游、本地记录、拦截与缓存、查询日志、客户端配置。
/// DNS 与代理一样随 App 进程运行，窗口关闭不会停止服务。
/// </summary>
public partial class DnsToolWindow : FluentWindow
{
    private readonly DnsHostService _service = AppServiceFactory.DnsHost;
    private readonly DnsFirewallService _firewall = new();
    private readonly UserDialogService _dialogs = new();
    private readonly DispatcherTimer _timer;
    private DnsDocument _document = new();
    private ObservableCollection<DnsLocalRecord> _records = new();
    private DnsLogEntry[] _filteredLogs = Array.Empty<DnsLogEntry>();
    private bool _loaded;
    private bool _starting;

    public DnsToolWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) =>
        {
            RefreshStatus();
            if (MainTabs.SelectedIndex == 3 && LogPauseCheck.IsChecked != true)
                RefreshLogs();
        };
        Loaded += OnLoaded;
        Closed += (_, _) => _timer.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("加载 DNS 配置", async () =>
        {
            await LoadAsync();
            if (IsLoaded)
                _timer.Start();
        });

    private async Task LoadAsync()
    {
        try
        {
            _document = await _service.LoadAsync();
            _records = new ObservableCollection<DnsLocalRecord>(_document.Options.LocalRecords);
            RecordsGrid.ItemsSource = _records;
            PopulateControls();
            RefreshStatus();
            RefreshLogs();
            UpdateClientConfig();
            FooterStatus.Text = "配置文件：" + _service.ConfigPath;
            _loaded = true;
            _ = RefreshFirewallStatusAsync();
        }
        catch (Exception ex)
        {
            FooterStatus.Text = "加载 DNS 配置失败：" + ex.Message;
        }
    }

    private void PopulateControls()
    {
        var options = _document.Options;
        BindAddressBox.ItemsSource = ProxyNetworkInterfaces.GetBindAddressOptions(options.BindAddress);
        BindAddressBox.SelectedValue = options.BindAddress;
        if (BindAddressBox.SelectedValue is null)
            BindAddressBox.Text = options.BindAddress;
        PortBox.Text = options.Port.ToString();
        UdpEnabledCheck.IsChecked = options.UdpEnabled;
        TcpEnabledCheck.IsChecked = options.TcpEnabled;
        EnabledCheck.IsChecked = options.Enabled;
        AllowPublicCheck.IsChecked = options.AllowPublicClients;
        TemporaryBox.Text = options.TemporaryEnableMinutes.ToString();
        AllowedClientsBox.Text = string.Join(Environment.NewLine, options.AllowedClientCidrs);
        UpstreamsBox.Text = string.Join(Environment.NewLine, options.Upstreams);
        MaxInFlightBox.Text = options.MaxInFlight.ToString();
        MaxQpsBox.Text = options.MaxQpsPerClient.ToString();
        MaxUdpBox.Text = options.MaxUdpResponseBytes.ToString();
        UpstreamTimeoutBox.Text = options.UpstreamTimeoutMs.ToString();
        ReuseConnectionsCheck.IsChecked = options.ReuseUpstreamConnections;
        RrlEnabledCheck.IsChecked = options.RrlEnabled;
        RrlRateBox.Text = options.RrlResponsesPerSecond.ToString();
        RrlBurstBox.Text = options.RrlBurst.ToString();

        BlockedDomainsBox.Text = string.Join(Environment.NewLine, options.BlockedDomains);
        AllowedDomainsBox.Text = string.Join(Environment.NewLine, options.AllowedDomains);
        SelectBlockMode(options.BlockMode);
        RefuseAnyCheck.IsChecked = options.RefuseAny;

        CacheEnabledCheck.IsChecked = options.Cache.Enabled;
        MaxEntriesBox.Text = options.Cache.MaxEntries.ToString();
        MinTtlBox.Text = options.Cache.MinTtlSeconds.ToString();
        MaxTtlBox.Text = options.Cache.MaxTtlSeconds.ToString();
        NegativeTtlBox.Text = options.Cache.NegativeTtlSeconds.ToString();
        StaleTtlBox.Text = options.Cache.StaleTtlSeconds.ToString();
        StaleMaxBox.Text = options.Cache.StaleMaxSeconds.ToString();
        ServeStaleCheck.IsChecked = options.Cache.ServeStale;

        FirewallCommandsBox.Text = _firewall.BuildAddCommands(options);
        FooterVersion.Text = $"轻量 DNS · {options.BindAddress}:{options.Port}";
        UpdateRecordHint();
    }

    private void SelectBlockMode(string? mode)
    {
        foreach (var item in BlockModeBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, mode, StringComparison.OrdinalIgnoreCase))
            {
                BlockModeBox.SelectedItem = item;
                return;
            }
        }
        BlockModeBox.SelectedIndex = 0;
    }

    private string SelectedBlockMode() =>
        (BlockModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "nxdomain";

    private bool ApplyUiToDocument()
    {
        var errors = new List<string>();
        var options = _document.Options;

        try { RecordsGrid.CommitEdit(DataGridEditingUnit.Row, true); }
        catch { /* 编辑未提交时忽略 */ }

        options.BindAddress = GetBindAddress();
        options.Port = ReadInt(PortBox, "DNS 端口", 1, 65535, errors);
        options.UdpEnabled = UdpEnabledCheck.IsChecked == true;
        options.TcpEnabled = TcpEnabledCheck.IsChecked == true;
        options.Enabled = EnabledCheck.IsChecked == true;
        options.AllowPublicClients = AllowPublicCheck.IsChecked == true;
        options.TemporaryEnableMinutes = ReadInt(TemporaryBox, "临时开启分钟数", 0, 10080, errors);
        options.MaxInFlight = ReadInt(MaxInFlightBox, "最大在途查询数", 1, 65535, errors);
        options.MaxQpsPerClient = ReadInt(MaxQpsBox, "单客户端 QPS", 0, 100000, errors);
        options.MaxUdpResponseBytes = ReadInt(MaxUdpBox, "UDP 响应上限", 512, 4096, errors);
        options.UpstreamTimeoutMs = ReadInt(UpstreamTimeoutBox, "上游超时", 200, 30000, errors);
        options.ReuseUpstreamConnections = ReuseConnectionsCheck.IsChecked == true;
        options.RrlEnabled = RrlEnabledCheck.IsChecked == true;
        options.RrlResponsesPerSecond = ReadInt(RrlRateBox, "RRL 每秒响应数", 1, 100000, errors);
        options.RrlBurst = ReadInt(RrlBurstBox, "RRL 突发容量", 1, 200000, errors);
        options.BlockMode = SelectedBlockMode();
        options.RefuseAny = RefuseAnyCheck.IsChecked == true;

        if (!options.UdpEnabled && !options.TcpEnabled)
            errors.Add("UDP 与 TCP 至少启用一个");
        if (!IsValidBindAddress(options.BindAddress))
            errors.Add("监听地址无效，请填写 127.0.0.1、0.0.0.0 或本机局域网 IP");

        options.AllowedClientCidrs = ParseClientList(AllowedClientsBox.Text, errors);
        options.Upstreams = ParseUpstreamList(UpstreamsBox.Text, errors);
        options.BlockedDomains = ParseDomainList(BlockedDomainsBox.Text, "黑名单", errors);
        options.AllowedDomains = ParseDomainList(AllowedDomainsBox.Text, "白名单", errors);

        options.Cache.Enabled = CacheEnabledCheck.IsChecked == true;
        options.Cache.MaxEntries = ReadInt(MaxEntriesBox, "缓存条目上限", 100, 1000000, errors);
        options.Cache.MinTtlSeconds = ReadInt(MinTtlBox, "缓存最小 TTL", 0, 3600, errors);
        options.Cache.MaxTtlSeconds = ReadInt(MaxTtlBox, "缓存最大 TTL", 1, 604800, errors);
        options.Cache.NegativeTtlSeconds = ReadInt(NegativeTtlBox, "负缓存 TTL", 0, 86400, errors);
        options.Cache.StaleTtlSeconds = ReadInt(StaleTtlBox, "过期返回 TTL", 1, 300, errors);
        options.Cache.StaleMaxSeconds = ReadInt(StaleMaxBox, "过期缓存保留上限", 0, 86400, errors);
        options.Cache.ServeStale = ServeStaleCheck.IsChecked == true;

        options.LocalRecords = _records.ToList();
        options.Normalize();

        if (options.Cache.MaxTtlSeconds < options.Cache.MinTtlSeconds)
            errors.Add("缓存最大 TTL 不能小于最小 TTL");

        foreach (var record in options.LocalRecords)
        {
            var error = record.Validate();
            if (error is not null)
                errors.Add(error);
        }

        if (errors.Count > 0)
        {
            FooterStatus.Text = "请检查：" + string.Join("；", errors.Distinct());
            return false;
        }
        return true;
    }

    private void OnSave(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("保存 DNS 设置", SaveAsync);

    private async Task SaveAsync()
    {
        if (!ApplyUiToDocument())
            return;
        try
        {
            var result = await _service.SaveAndApplyAsync(_document);
            FirewallCommandsBox.Text = _firewall.BuildAddCommands(_document.Options);
            FooterStatus.Text = result.Message;
            UpdateClientConfig();
            _ = RefreshFirewallStatusAsync();
        }
        catch (Exception ex)
        {
            FooterStatus.Text = "保存失败：" + ex.Message;
        }
    }

    private void OnStart(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("启动 DNS", StartAsync);

    private async Task StartAsync()
    {
        if (_starting)
            return;
        if (!ApplyUiToDocument())
            return;

        var options = _document.Options;
        if (options.AllowPublicClients &&
            !_dialogs.Confirm(
                "当前允许公网客户端访问 DNS。若端口暴露到公网，可能被用于 DNS 放大攻击。\n" +
                "建议只允许局域网 CIDR，并缩短临时开启时间。\n\n确认继续启动？",
                "启动 DNS", warning: true, defaultNo: true))
            return;

        if (IsExposedBindAddress(options.BindAddress) && options.AllowedClientCidrs.Count == 0 &&
            !_dialogs.Confirm(
                "监听地址不是本机回环，局域网客户端可通过本机 IP 使用 DNS。\n" +
                "默认仅允许回环与私有网段客户端，仍建议在 Windows 防火墙中只放行 private 配置文件。\n\n确认启动？",
                "启动 DNS", warning: true, defaultNo: true))
            return;

        _starting = true;
        StartButton.IsEnabled = false;
        FooterStatus.Text = "正在启动 DNS…";
        try
        {
            await _service.StartAsync(_document);
            FirewallCommandsBox.Text = _firewall.BuildAddCommands(options);
            FooterStatus.Text = "DNS 已启动；退出 App、休眠或重启 Windows 都会停止解析。";
            UpdateClientConfig();
        }
        catch (Exception ex)
        {
            FooterStatus.Text = "启动失败：" + ex.Message;
            _dialogs.Error(ex.Message, "启动 DNS");
        }
        finally
        {
            _starting = false;
            RefreshStatus();
        }
    }

    private void OnStop(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("停止 DNS", StopAsync);

    private async Task StopAsync()
    {
        if (!_dialogs.Confirm("停止 DNS 后，使用本机作为 DNS 的客户端将立即无法解析。\n继续？",
                "停止 DNS", warning: true, defaultNo: true))
            return;

        StopButton.IsEnabled = false;
        FooterStatus.Text = "正在停止 DNS…";
        try
        {
            await _service.StopAsync();
            FooterStatus.Text = "DNS 已停止。";
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
            StatusTitle.Text = "DNS 运行中";
            StatusTitle.Foreground = TryFindResource("AppSuccessBrush") as System.Windows.Media.Brush;
            StatusDetail.Text =
                $"UDP {status.UdpEndpoint ?? "关闭"} · TCP {status.TcpEndpoint ?? "关闭"}" +
                $" · 查询 {status.TotalQueries} · 缓存命中 {status.CacheHits}" +
                $" · 转发 {status.Forwarded} · 合并 {status.Coalesced}" +
                $" · 拦截 {status.Blocked} · 限速 {status.RateLimited} · 失败 {status.Failed}" +
                $" · 在途 {status.ActiveQueries} · 缓存 {_service.CacheCount} 条{expiry}";
            StatusDetail.Text += " · 退出 App 即停止";
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
        }
        else
        {
            StatusTitle.Text = "DNS 未启动";
            StatusTitle.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
            StatusDetail.Text =
                "默认仅监听 127.0.0.1 且只允许回环/私有网段客户端。" +
                " DNS 是全局基础设施：App 退出、休眠或 Windows 重启都会中断解析，请为客户端保留备用 DNS。";
            StartButton.IsEnabled = !_starting;
            StopButton.IsEnabled = false;
        }

        var error = status.LastError ?? _service.LastError;
        StatusError.Text = error ?? string.Empty;
        StatusError.Visibility = string.IsNullOrWhiteSpace(error)
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (CacheStatusText is not null)
        {
            var total = status.TotalQueries;
            var rate = total == 0
                ? "-"
                : $"{(double)status.CacheHits * 100 / total:0.0}%";
            CacheStatusText.Text =
                $"当前缓存 {_service.CacheCount} 条 · 累计命中 {status.CacheHits}/{total}（{rate}）。";
        }
    }

    private DnsLogEntry[] FilterLogs()
    {
        var filter = (LogFilterBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        var search = LogSearchBox?.Text?.Trim() ?? string.Empty;

        IEnumerable<DnsLogEntry> query = _service.Log.Snapshot();
        if (filter.Length > 0)
            query = query.Where(entry =>
                entry.Result.Contains(filter, StringComparison.OrdinalIgnoreCase));
        if (search.Length > 0)
            query = query.Where(entry =>
                entry.Question.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                entry.Client.Contains(search, StringComparison.OrdinalIgnoreCase));

        return query.Reverse().ToArray();
    }

    private void RefreshLogs()
    {
        _filteredLogs = FilterLogs();
        LogGrid.ItemsSource = _filteredLogs;
    }

    private void OnRefreshLogs(object sender, RoutedEventArgs e) => RefreshLogs();

    private void OnLogFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogGrid is not null)
            RefreshLogs();
    }

    private void OnLogSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (LogGrid is not null)
            RefreshLogs();
    }

    private void OnClearLogs(object sender, RoutedEventArgs e)
    {
        _service.Log.Clear();
        RefreshLogs();
        FooterStatus.Text = "查询日志已清空。";
    }

    private void OnCopyLogs(object sender, RoutedEventArgs e)
    {
        var entries = _filteredLogs.Length > 0 ? _filteredLogs : FilterLogs();
        if (entries.Length == 0)
        {
            FooterStatus.Text = "当前没有可复制的查询日志。";
            return;
        }

        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.AppendLine(string.Join('\t', entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                entry.Client, entry.Question, entry.Type, entry.Result, entry.Upstream,
                entry.DurationMs));
        }
        CopyText(builder.ToString(), "DNS 日志");
    }

    private void OnExportLogs(object sender, RoutedEventArgs e)
    {
        var entries = FilterLogs();
        if (entries.Length == 0)
        {
            FooterStatus.Text = "当前筛选结果为空，未导出。";
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = $"litssh-dns-log-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            Filter = "CSV 文件|*.csv|所有文件|*.*"
        };
        if (dialog.ShowDialog() != true)
            return;

        var builder = new StringBuilder();
        builder.AppendLine("时间,客户端,域名,类型,结果,上游,耗时(ms)");
        foreach (var entry in entries)
        {
            builder.AppendLine(string.Join(',',
                Csv(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")),
                Csv(entry.Client),
                Csv(entry.Question),
                Csv(entry.Type),
                Csv(entry.Result),
                Csv(entry.Upstream),
                entry.DurationMs));
        }

        try
        {
            File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(true));
            FooterStatus.Text = $"已导出 {entries.Length} 条查询日志：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            FooterStatus.Text = "导出失败：" + ex.Message;
        }
    }

    private static string Csv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
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
                ? "没有启用的 DNS 协议。"
                : string.Join("；", statuses.Select(item =>
                    $"{item.Protocol} {item.Port}：{(item.Exists ? "已放行" : "未放行")}" +
                    (string.IsNullOrWhiteSpace(item.Detail) ? string.Empty : $"（{item.Detail}）")));
        }
        catch (Exception ex)
        {
            FirewallStatusText.Text = "检测失败：" + ex.Message;
        }
    }

    private void OnAddFirewall(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("添加防火墙规则", AddFirewallAsync);

    private async Task AddFirewallAsync()
    {
        if (!ApplyUiToDocument())
            return;
        if (!_dialogs.Confirm(
                "将通过 UAC 提权执行 netsh，添加 DNS UDP/TCP 入站规则（仅 private 配置文件）。\n继续？",
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
        if (!_dialogs.Confirm("将删除 LitSSH DNS 的防火墙规则，继续？", "删除防火墙规则",
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

    private void OnProbeUpstreams(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("检测上游", ProbeUpstreamsAsync);

    private async Task ProbeUpstreamsAsync()
    {
        if (!ApplyUiToDocument())
            return;

        UpstreamProbeHint.Text = "正在检测上游…";
        try
        {
            var results = await _service.ProbeUpstreamsAsync(_document.Options);
            UpstreamProbeGrid.ItemsSource = results;
            var ok = results.Count(result => result.Success);
            UpstreamProbeHint.Text = results.Count == 0
                ? "没有可用的上游（配置为空时使用系统 DNS）。"
                : $"共 {results.Count} 个上游，{ok} 个正常。";
        }
        catch (Exception ex)
        {
            UpstreamProbeHint.Text = "检测失败：" + ex.Message;
        }
    }

    private void OnAddRecord(object sender, RoutedEventArgs e)
    {
        var window = new DnsRecordEditWindow { Owner = this };
        window.Configure(null);
        if (window.ShowDialog() != true || window.Result is null)
            return;

        _records.Add(window.Result);
        RecordsGrid.SelectedItem = window.Result;
        RecordsGrid.ScrollIntoView(window.Result);
        UpdateRecordHint();
        SaveRecords("记录已新增并保存。");
    }

    private void OnEditRecord(object sender, RoutedEventArgs e)
    {
        if (RecordsGrid.SelectedItem is not DnsLocalRecord record)
        {
            _dialogs.Warn("请先选择一条本地记录。", "编辑记录");
            return;
        }
        EditRecord(record);
    }

    private void OnRecordsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecordsGrid.SelectedItem is DnsLocalRecord record)
            EditRecord(record);
    }

    private void EditRecord(DnsLocalRecord record)
    {
        var window = new DnsRecordEditWindow { Owner = this };
        window.Configure(record);
        if (window.ShowDialog() != true || window.Result is null)
            return;

        var index = _records.IndexOf(record);
        if (index >= 0)
            _records[index] = window.Result;
        RecordsGrid.SelectedItem = window.Result;
        UpdateRecordHint();
        SaveRecords("记录已修改并保存。");
    }

    private void OnDeleteRecord(object sender, RoutedEventArgs e)
    {
        if (RecordsGrid.SelectedItem is not DnsLocalRecord record)
        {
            _dialogs.Warn("请先选择一条本地记录。", "删除记录");
            return;
        }
        _records.Remove(record);
        UpdateRecordHint();
        SaveRecords("记录已删除并保存。");
    }

    /// <summary>
    /// 本地记录独立于其他页面的表单，增删改后立即持久化并热更新，
    /// 避免用户误以为还要到「监听与上游」里点保存。
    /// </summary>
    private void SaveRecords(string message)
    {
        if (!_loaded)
        {
            FooterStatus.Text = "配置尚未加载完成，请稍后再试。";
            return;
        }
        FooterStatus.Text = "正在保存本地记录…";
        AsyncOperation.Run("保存本地记录", () => SaveRecordsAsync(message));
    }

    private async Task SaveRecordsAsync(string message)
    {
        var errors = new List<string>();
        foreach (var record in _records)
        {
            var error = record.Validate();
            if (error is not null)
                errors.Add(error);
        }
        if (errors.Count > 0)
        {
            FooterStatus.Text = "本地记录未保存：" + string.Join("；", errors.Distinct());
            return;
        }

        _document.Options.LocalRecords = _records.ToList();
        try
        {
            var document = new DnsDocument { Options = _document.Options.Clone() };
            var result = await _service.SaveAndApplyAsync(document);
            FooterStatus.Text = message + result.Message;
        }
        catch (Exception ex)
        {
            FooterStatus.Text = "本地记录保存失败：" + ex.Message;
        }
    }

    private void OnImportHosts(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "hosts 文件|hosts;*.txt|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true)
            return;

        string text;
        try
        {
            text = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex)
        {
            _dialogs.Error("读取文件失败：" + ex.Message, "导入 hosts");
            return;
        }

        var parsed = DnsHostsFile.Parse(text);
        var existing = new HashSet<string>(
            _records.Select(RecordKey), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var record in parsed.Records)
        {
            if (existing.Add(RecordKey(record)))
            {
                _records.Add(record);
                added++;
            }
        }

        UpdateRecordHint();
        var errors = parsed.Errors.Count == 0
            ? string.Empty
            : $"；{parsed.Errors.Count} 行被忽略：" + string.Join("；", parsed.Errors.Take(5));
        SaveRecords($"已导入 {added} 条记录并保存（重复 {parsed.Records.Count - added} 条已跳过）{errors}。");
    }

    private void OnExportHosts(object sender, RoutedEventArgs e)
    {
        var content = DnsHostsFile.Export(_records);
        if (string.IsNullOrWhiteSpace(content))
        {
            _dialogs.Warn("没有可导出的 A / AAAA 记录。CNAME / PTR 无法用 hosts 格式表示。", "导出 hosts");
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = $"litssh-dns-hosts-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Filter = "hosts 文件|*.txt|所有文件|*.*"
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, content, new UTF8Encoding(false));
            FooterStatus.Text = "已导出 hosts 文件：" + dialog.FileName;
        }
        catch (Exception ex)
        {
            FooterStatus.Text = "导出失败：" + ex.Message;
        }
    }

    private static string RecordKey(DnsLocalRecord record) =>
        $"{DnsWire.NormalizeName(record.Name)}|{record.Type}|{record.Value}";

    private void OnTestRule(object sender, RoutedEventArgs e)
    {
        if (!ApplyUiToDocument())
            return;

        var name = RuleTestBox.Text.Trim();
        if (name.Length == 0)
        {
            RuleTestResult.Text = "请输入要测试的域名。";
            return;
        }

        var normalized = DnsWire.NormalizeName(name);
        if (!DnsWire.IsValidRecordName(normalized))
        {
            RuleTestResult.Text = "域名格式不合法。";
            return;
        }

        var typeText = (RuleTestTypeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "A";
        var type = typeText == "ANY" ? (ushort)255 : DnsLocalResolver.TypeToUshort(typeText);
        var options = _document.Options;

        var block = DnsDomainPolicy.Evaluate(normalized,
            options.AllowedDomains, options.BlockedDomains);
        if (block is not null)
        {
            RuleTestResult.Text = $"结果：拦截（{block}），返回 {options.BlockMode}。";
            return;
        }

        if (type == 255 && options.RefuseAny)
        {
            RuleTestResult.Text = "结果：拒绝（已启用“拒绝 ANY 查询”）。";
            return;
        }

        var local = new DnsLocalResolver(options.LocalRecords).Lookup(normalized, type);
        if (local.Matched)
        {
            RuleTestResult.Text = local.Records.Count == 0
                ? "结果：本地名称存在，但没有该类型的记录（返回 NODATA，不查询上游）。"
                : "结果：命中本地记录 → " + string.Join("；",
                    local.Records.Select(record => $"{record.Type} {record.Value} (TTL {record.Ttl})"));
            return;
        }

        var cache = options.Cache.Enabled
            ? _service.GetCacheState(normalized, type)
            : DnsCacheState.None;
        var upstreamText = options.Upstreams.Count == 0
            ? "系统 DNS"
            : string.Join(", ", options.Upstreams);
        RuleTestResult.Text = cache switch
        {
            DnsCacheState.Fresh => "结果：命中缓存（新鲜），不查询上游。",
            DnsCacheState.Stale => "结果：仅有过期缓存；上游正常时会重新查询，上游失败时才返回过期缓存。",
            _ => $"结果：转发到上游（{upstreamText}）。"
        };
    }

    private void OnClearCache(object sender, RoutedEventArgs e)
    {
        _service.ClearCache();
        RefreshStatus();
        FooterStatus.Text = "DNS 缓存已清空。";
    }

    private void OnCopyRecordExample(object sender, RoutedEventArgs e) =>
        CopyText(
            "# 每行一条：名称 类型 值 TTL" + Environment.NewLine +
            "nas.lab            A     192.168.1.20   300" + Environment.NewLine +
            "ipv6.lab           AAAA  2001:db8::20    300" + Environment.NewLine +
            "www.lab            CNAME nas.lab         300" + Environment.NewLine +
            "20.1.168.192.in-addr.arpa  PTR  nas.lab   300",
            "本地记录示例");

    private void UpdateRecordHint()
    {
        if (RecordsHint is null)
            return;
        RecordsHint.Text = _records.Count == 0
            ? "当前没有本地记录。类型填写 A、AAAA、CNAME 或 PTR；A/AAAA 的值填写 IP，CNAME/PTR 的值填写目标域名。"
            : $"共 {_records.Count} 条记录。名称支持 *.lab 通配；本地记录优先于缓存和上游。修改后自动保存并热更新。";
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MainTabs.SelectedIndex == 3)
            RefreshLogs();
        else if (MainTabs.SelectedIndex == 4)
            UpdateClientConfig();
    }

    private void UpdateClientConfig()
    {
        if (ClientConfigBox is null)
            return;
        ClientConfigBox.Text = BuildClientConfig();
    }

    private void OnCopyClientConfig(object sender, RoutedEventArgs e) =>
        CopyText(BuildClientConfig(), "客户端配置");

    private void OnCopyNetsh(object sender, RoutedEventArgs e) =>
        CopyText(BuildNetsh(), "netsh 命令");

    private void OnCopyPowerShell(object sender, RoutedEventArgs e) =>
        CopyText(BuildPowerShell(), "PowerShell 命令");

    private string BuildClientConfig()
    {
        var options = _document.Options;
        var host = AdvertisedHost();
        var builder = new StringBuilder();
        builder.AppendLine($"DNS 服务器：{host}:{options.Port}（UDP/TCP）");
        builder.AppendLine("客户端请保留一个备用 DNS（例如 223.5.5.5），避免本机退出 App 后断网。");
        builder.AppendLine();
        builder.AppendLine("== netsh（管理员） ==");
        builder.AppendLine(BuildNetsh());
        builder.AppendLine();
        builder.AppendLine("== PowerShell（管理员） ==");
        builder.AppendLine(BuildPowerShell());
        builder.AppendLine();
        builder.AppendLine("== 测试 ==");
        builder.AppendLine($"nslookup example.com {host}");
        builder.AppendLine($"Resolve-DnsName example.com -Server {host}");
        builder.AppendLine();
        builder.AppendLine("== 恢复自动获取 ==");
        builder.AppendLine("netsh interface ip set dns name=\"以太网\" dhcp");
        if (IsLoopbackOnly(options))
        {
            builder.AppendLine();
            builder.AppendLine("提示：当前只监听 127.0.0.1，只有本机可以使用；客户端需要先把监听地址改为 0.0.0.0 或本机局域网 IP。");
        }
        return builder.ToString().TrimEnd();
    }

    private string BuildNetsh()
    {
        var host = AdvertisedHost();
        var builder = new StringBuilder();
        builder.AppendLine($"netsh interface ip set dns name=\"以太网\" static {host} primary");
        builder.AppendLine("netsh interface ip add dns name=\"以太网\" 223.5.5.5 index=2");
        return builder.ToString().TrimEnd();
    }

    private string BuildPowerShell()
    {
        var host = AdvertisedHost();
        return $"Set-DnsClientServerAddress -InterfaceAlias \"以太网\" -ServerAddresses (\"{host}\",\"223.5.5.5\")";
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

    private string AdvertisedHost()
    {
        var bind = _document.Options.BindAddress;
        if (!string.IsNullOrWhiteSpace(bind) &&
            bind is not ("0.0.0.0" or "::" or "*" or "127.0.0.1" or "localhost"))
            return bind;
        if (IsLoopbackOnly(_document.Options))
            return "127.0.0.1";
        return ProxyNetworkInterfaces.FindLanIpv4() ?? "127.0.0.1";
    }

    private static bool IsLoopbackOnly(DnsOptions options) =>
        options.BindAddress is "127.0.0.1" or "localhost" ||
        string.Equals(options.BindAddress, "::1", StringComparison.OrdinalIgnoreCase);

    private static bool IsExposedBindAddress(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value is not ("127.0.0.1" or "localhost" or "::1");

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

    private static List<string> ParseClientList(string text, List<string> errors)
    {
        var result = new List<string>();
        foreach (var value in text.Split(
                     new[] { '\r', '\n', ',', '，', ';', '；' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = value.Trim();
            if (trimmed.Length == 0)
                continue;
            if (!ProxyCidr.TryParse(trimmed, out _))
                errors.Add($"客户端白名单“{trimmed}”不是合法的 IP/CIDR");
            else
                result.Add(trimmed);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ParseUpstreamList(string text, List<string> errors)
    {
        var result = new List<string>();
        foreach (var value in text.Split(
                     new[] { '\r', '\n', ',', '，', ';', '；' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = value.Trim();
            if (trimmed.Length == 0)
                continue;
            if (!DnsUpstreamEndpoint.TryParse(trimmed, out _))
                errors.Add($"上游 DNS“{trimmed}”格式不合法");
            else
                result.Add(trimmed);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
}
