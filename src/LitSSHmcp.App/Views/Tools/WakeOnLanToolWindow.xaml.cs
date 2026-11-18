using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Toolbox;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views.Tools;

public partial class WakeOnLanToolWindow : FluentWindow
{
    private static readonly int[] RepeatOptions = { 1, 3, 5, 10 };

    private readonly WakeOnLanService _service = new();
    private readonly WakeOnLanStore _store = new();
    private readonly IUserDialogService _dialogs = new UserDialogService();
    private readonly ObservableCollection<WakeOnLanHost> _hosts = new();
    private readonly ObservableCollection<WakeOnLanScanEntry> _scanEntries = new();
    private CancellationTokenSource? _scanCts;
    private Guid? _editingId;
    private bool _loading;

    public WakeOnLanToolWindow()
    {
        InitializeComponent();
        HostGrid.ItemsSource = _hosts;
        ScanGrid.ItemsSource = _scanEntries;
        Loaded += (_, _) => AsyncOperation.Run("加载网络唤醒设备", LoadAsync);
        Closed += (_, _) => _scanCts?.Cancel();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var document = await _store.LoadAsync();
            _hosts.Clear();
            foreach (var host in document.Hosts.OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase))
                _hosts.Add(host);
            RefreshHostSummary();

            var adapters = WakeOnLanService.GetAdapters();
            AdapterBox.ItemsSource = adapters;
            if (adapters.Count > 0)
            {
                AdapterBox.SelectedIndex = 0;
            }
            else
            {
                ScanButton.IsEnabled = false;
                SubnetText.Text = "未发现可用 IPv4 网卡，请检查网络连接。";
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshHostSummary()
        => HostSummaryText.Text = $"共 {_hosts.Count} 台设备";

    private void OnHostSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        if (HostGrid.SelectedItem is not WakeOnLanHost host)
            return;

        _editingId = host.Id;
        EditorTitleText.Text = "编辑设备";
        NameBox.Text = host.Name;
        MacBox.Text = host.MacAddress;
        IpBox.Text = host.IpAddress;
        BroadcastBox.Text = host.BroadcastAddress;
        PortBox.Text = host.Port.ToString();
        SecureOnBox.Text = host.SecureOnPassword;
        ScheduleEnabledBox.IsChecked = host.ScheduleEnabled;
        ScheduleTimeBox.Text = string.IsNullOrWhiteSpace(host.ScheduleTime) ? "08:00" : host.ScheduleTime;
        NotesBox.Text = host.Notes;
        StatusText.Text = $"已选择「{host.Name}」，可直接唤醒或修改后保存。";
    }

    private void OnHostDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (HostGrid.SelectedItem is WakeOnLanHost)
            AsyncOperation.Run("唤醒设备", WakeSelectedAsync);
    }

    private void OnNewHost(object sender, RoutedEventArgs e)
    {
        _editingId = null;
        HostGrid.SelectedItem = null;
        EditorTitleText.Text = "新增设备";
        NameBox.Text = string.Empty;
        MacBox.Text = string.Empty;
        IpBox.Text = string.Empty;
        BroadcastBox.Text = string.Empty;
        PortBox.Text = WakeOnLanService.DefaultPort.ToString();
        SecureOnBox.Text = string.Empty;
        ScheduleEnabledBox.IsChecked = false;
        ScheduleTimeBox.Text = "08:00";
        NotesBox.Text = string.Empty;
        NameBox.Focus();
        StatusText.Text = "填写名称与 MAC 地址后保存，MAC 支持冒号、短横线或 Cisco 点分格式。";
    }

    private void OnClearEditor(object sender, RoutedEventArgs e)
    {
        _editingId = null;
        HostGrid.SelectedItem = null;
        EditorTitleText.Text = "新增设备";
        NameBox.Text = string.Empty;
        MacBox.Text = string.Empty;
        IpBox.Text = string.Empty;
        BroadcastBox.Text = string.Empty;
        PortBox.Text = WakeOnLanService.DefaultPort.ToString();
        SecureOnBox.Text = string.Empty;
        ScheduleEnabledBox.IsChecked = false;
        ScheduleTimeBox.Text = "08:00";
        NotesBox.Text = string.Empty;
        StatusText.Text = "表单已清空。";
    }

    private void OnSaveHost(object sender, RoutedEventArgs e)
        => AsyncOperation.Run("保存网络唤醒设备", SaveHostAsync);

    private async Task SaveHostAsync()
    {
        if (!TryReadEditor(out var values))
            return;

        var now = DateTimeOffset.Now;
        var host = _editingId is Guid id
            ? _hosts.FirstOrDefault(h => h.Id == id)
            : null;
        if (host is null)
        {
            host = new WakeOnLanHost { CreatedAt = now };
            _hosts.Add(host);
        }

        host.Name = values.Name;
        host.MacAddress = values.Mac;
        host.IpAddress = values.Ip;
        host.BroadcastAddress = values.Broadcast;
        host.Port = values.Port;
        host.SecureOnPassword = values.SecureOn;
        host.ScheduleEnabled = values.ScheduleEnabled;
        host.ScheduleTime = values.ScheduleTime;
        host.Notes = values.Notes;
        host.UpdatedAt = now;

        _editingId = host.Id;
        EditorTitleText.Text = "编辑设备";
        await SaveDocumentAsync();
        HostGrid.Items.Refresh();
        HostGrid.SelectedItem = host;
        RefreshHostSummary();
        StatusText.Text = $"已保存设备「{host.Name}」。";
    }

    private void OnDeleteHost(object sender, RoutedEventArgs e)
        => AsyncOperation.Run("删除网络唤醒设备", DeleteHostAsync);

    private async Task DeleteHostAsync()
    {
        if (HostGrid.SelectedItem is not WakeOnLanHost host)
        {
            StatusText.Text = "请先选择要删除的设备。";
            return;
        }

        if (!_dialogs.Confirm($"确定删除设备「{host.Name}」？\n\nMAC：{host.MacAddress}", "删除设备"))
            return;

        _hosts.Remove(host);
        if (_editingId == host.Id)
        {
            _editingId = null;
            EditorTitleText.Text = "新增设备";
        }
        await SaveDocumentAsync();
        RefreshHostSummary();
        StatusText.Text = $"已删除设备「{host.Name}」。";
    }

    private void OnWakeSelected(object sender, RoutedEventArgs e)
        => AsyncOperation.Run("唤醒设备", WakeSelectedAsync);

    private async Task WakeSelectedAsync()
    {
        var targets = HostGrid.SelectedItems.OfType<WakeOnLanHost>().ToList();
        if (targets.Count == 0)
        {
            if (!TryReadEditor(out var values))
                return;
            targets.Add(new WakeOnLanHost
            {
                Name = values.Name,
                MacAddress = values.Mac,
                IpAddress = values.Ip,
                BroadcastAddress = values.Broadcast,
                Port = values.Port,
                SecureOnPassword = values.SecureOn,
                Notes = values.Notes
            });
        }

        var repeat = RepeatOptions[Math.Clamp(RepeatBox.SelectedIndex, 0, RepeatOptions.Length - 1)];
        var success = 0;
        var failed = new List<string>();
        var persist = false;
        foreach (var host in targets)
        {
            StatusText.Text = $"正在向「{host.Name}」发送魔术包...";
            var result = await _service.WakeAsync(host, repeat: repeat);
            if (!result.Success)
            {
                failed.Add($"{host.Name}: {result.Message}");
                continue;
            }

            success++;
            if (_hosts.Contains(host))
            {
                host.LastWokenAt = DateTimeOffset.Now;
                host.WakeCount++;
                host.UpdatedAt = DateTimeOffset.Now;
                persist = true;
            }
        }

        if (persist)
        {
            await SaveDocumentAsync();
            HostGrid.Items.Refresh();
        }

        StatusText.Text = failed.Count == 0
            ? $"已向 {success} 台设备发送魔术包。目标开机通常需要 3-15 秒。"
            : $"成功 {success} 台，失败 {failed.Count} 台。";
        if (failed.Count > 0)
            _dialogs.Warn(string.Join("\n", failed), "网络唤醒");
    }

    private void OnRefreshHosts(object sender, RoutedEventArgs e)
        => AsyncOperation.Run("刷新网络唤醒设备", RefreshHostsAsync);

    private async Task RefreshHostsAsync()
    {
        await LoadAsync();
        StatusText.Text = "已从本机存储刷新设备列表。";
    }

    private void OnImportCsv(object sender, RoutedEventArgs e)
        => AsyncOperation.Run("导入网络唤醒 CSV", ImportCsvAsync);

    private async Task ImportCsvAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入网络唤醒 CSV",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        string text;
        try { text = await File.ReadAllTextAsync(dialog.FileName); }
        catch (Exception ex)
        {
            _dialogs.Warn("读取 CSV 失败：" + ex.Message, "网络唤醒");
            return;
        }

        var imported = WakeOnLanCsv.Import(text);
        var added = 0;
        var skipped = 0;
        foreach (var host in imported)
        {
            if (_hosts.Any(h => WakeOnLanService.TryNormalizeMac(h.MacAddress, out var mac) &&
                                string.Equals(mac, host.MacAddress, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }
            _hosts.Add(host);
            added++;
        }

        if (added > 0)
        {
            await SaveDocumentAsync();
            HostGrid.Items.Refresh();
            RefreshHostSummary();
        }
        StatusText.Text = $"导入完成：新增 {added} 台，跳过重复/无效 {skipped} 台。";
    }

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        if (_hosts.Count == 0)
        {
            StatusText.Text = "没有可导出的设备。";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出网络唤醒 CSV",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"wake-on-lan-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, WakeOnLanCsv.Export(_hosts), new UTF8Encoding(true));
            StatusText.Text = "已导出：" + dialog.FileName;
        }
        catch (Exception ex)
        {
            _dialogs.Warn("导出 CSV 失败：" + ex.Message, "网络唤醒");
        }
    }

    private bool TryReadEditor(out (string Name, string Mac, string Ip, string Broadcast, int Port,
        string SecureOn, bool ScheduleEnabled, string ScheduleTime, string Notes) values)
    {
        values = default;
        if (!WakeOnLanService.TryNormalizeMac(MacBox.Text, out var mac))
        {
            StatusText.Text = "MAC 地址格式无效，示例：AA:BB:CC:DD:EE:FF。";
            _dialogs.Warn("MAC 地址格式无效。支持 AA:BB:CC:DD:EE:FF、AA-BB-CC-DD-EE-FF 或 AABB.CCDD.EEFF。", "网络唤醒");
            MacBox.Focus();
            return false;
        }

        var ip = IpBox.Text.Trim();
        if (ip.Length > 0 && (!IPAddress.TryParse(ip, out var parsedIp) ||
                              parsedIp is null ||
                              parsedIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
        {
            StatusText.Text = "IP 地址无效。";
            _dialogs.Warn("IP 地址无效，请填写 IPv4 地址或留空。", "网络唤醒");
            IpBox.Focus();
            return false;
        }

        var broadcast = BroadcastBox.Text.Trim();
        if (broadcast.Length > 0 &&
            (!IPAddress.TryParse(broadcast, out var parsedBroadcast) ||
             parsedBroadcast is null ||
             parsedBroadcast.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
        {
            StatusText.Text = "广播地址无效。";
            _dialogs.Warn("广播地址无效，请填写 IPv4 广播地址（如 192.168.1.255）或留空自动推断。", "网络唤醒");
            BroadcastBox.Focus();
            return false;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            StatusText.Text = "端口必须在 1-65535 之间。";
            _dialogs.Warn("端口必须在 1-65535 之间，WOL 常用 7 或 9。", "网络唤醒");
            PortBox.Focus();
            return false;
        }

        var secureOn = SecureOnBox.Text.Trim();
        if (secureOn.Length > 0)
        {
            if (!WakeOnLanService.TryNormalizeMac(secureOn, out var normalizedSecureOn))
            {
                StatusText.Text = "SecureOn 密码格式无效。";
                _dialogs.Warn("SecureOn 密码必须是 6 字节十六进制，例如 AA:BB:CC:DD:EE:FF；留空表示未启用。", "网络唤醒");
                SecureOnBox.Focus();
                return false;
            }
            secureOn = normalizedSecureOn;
        }

        var scheduleTime = ScheduleTimeBox.Text.Trim();
        if (scheduleTime.Length == 0)
            scheduleTime = "08:00";
        if (!WakeOnLanCsv.IsValidTime(scheduleTime))
        {
            StatusText.Text = "定时唤醒时间格式无效。";
            _dialogs.Warn("定时唤醒时间必须是 HH:mm，例如 08:00。", "网络唤醒");
            ScheduleTimeBox.Focus();
            return false;
        }

        var name = NameBox.Text.Trim();
        if (name.Length == 0)
            name = mac;

        values = (name, mac, ip, broadcast, port, secureOn,
            ScheduleEnabledBox.IsChecked == true, scheduleTime, NotesBox.Text.Trim());
        return true;
    }

    private async Task SaveDocumentAsync()
    {
        try
        {
            await _store.SaveAsync(new WakeOnLanDocument { Hosts = _hosts.ToList() });
        }
        catch (Exception ex)
        {
            StatusText.Text = "保存设备列表失败：" + ex.Message;
        }
    }

    private void OnAdapterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;
        UpdateSubnetText();
    }

    private void UpdateSubnetText()
    {
        if (AdapterBox.SelectedItem is not WakeOnLanAdapter adapter)
        {
            SubnetText.Text = "未选择网卡。";
            return;
        }

        try
        {
            var count = WakeOnLanService.GetScanHosts(adapter).Count;
            SubnetText.Text = $"网段 {adapter.Cidr} · 本机 {adapter.Address} · 将探测 {count} 个地址";
        }
        catch (Exception ex)
        {
            SubnetText.Text = ex.Message;
        }
    }

    private void OnScan(object sender, RoutedEventArgs e)
        => AsyncOperation.Run("扫描局域网", ScanAsync);

    private void OnStopScan(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    private async Task ScanAsync()
    {
        if (_scanCts is not null)
            return;
        if (AdapterBox.SelectedItem is not WakeOnLanAdapter adapter)
        {
            StatusText.Text = "请先选择要扫描的网卡。";
            return;
        }

        _scanCts = new CancellationTokenSource();
        var cancellationToken = _scanCts.Token;
        _scanEntries.Clear();
        ScanButton.IsEnabled = false;
        StopScanButton.IsEnabled = true;
        AdapterBox.IsEnabled = false;
        ScanProgress.Visibility = Visibility.Visible;
        ScanProgress.IsIndeterminate = true;
        ScanStatusText.Text = "正在准备扫描...";

        var progress = new Progress<WakeOnLanScanProgress>(value =>
        {
            if (value.ReadingArpTable)
            {
                ScanProgress.IsIndeterminate = true;
                ScanStatusText.Text = "Ping 完成，正在读取 ARP 缓存与主机名...";
                return;
            }

            ScanProgress.IsIndeterminate = false;
            ScanProgress.Maximum = Math.Max(1, value.Total);
            ScanProgress.Value = value.Probed;
            ScanStatusText.Text = $"Ping 探测 {value.Probed}/{value.Total}";
        });

        try
        {
            var knownMacs = _hosts.Select(h => h.MacAddress).ToList();
            var result = await _service.ScanAsync(adapter, knownMacs, progress, cancellationToken);
            foreach (var entry in result.Entries)
                _scanEntries.Add(entry);

            ScanStatusText.Text = result.Entries.Count == 0
                ? $"扫描完成，未发现设备（{result.Subnet}）"
                : $"扫描完成，发现 {result.Entries.Count} 台设备（{result.Subnet}）";
            StatusText.Text = result.Entries.Count == 0
                ? "未发现设备。已关机/休眠的目标不会出现在 ARP 缓存中，请手动添加已知 MAC。"
                : "勾选（Ctrl/Shift 多选）目标后点击「添加选中」，或双击行查看；添加前请确认 MAC 对应的是目标网卡。";
        }
        catch (OperationCanceledException)
        {
            ScanStatusText.Text = "已停止扫描。";
            StatusText.Text = "扫描已停止。";
        }
        catch (Exception ex)
        {
            ScanStatusText.Text = "扫描失败：" + ex.Message;
            _dialogs.Warn("扫描失败：" + ex.Message, "网络唤醒");
        }
        finally
        {
            _scanCts.Dispose();
            _scanCts = null;
            ScanButton.IsEnabled = true;
            StopScanButton.IsEnabled = false;
            AdapterBox.IsEnabled = true;
            ScanProgress.Visibility = Visibility.Collapsed;
            ScanProgress.IsIndeterminate = false;
        }
    }

    private void OnSelectAllScan(object sender, RoutedEventArgs e) => ScanGrid.SelectAll();

    private void OnAddScanSelected(object sender, RoutedEventArgs e)
        => AsyncOperation.Run("添加扫描结果", AddScanSelectedAsync);

    private async Task AddScanSelectedAsync()
    {
        var selected = ScanGrid.SelectedItems.OfType<WakeOnLanScanEntry>().ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "请先在扫描结果中选择要添加的设备。";
            return;
        }

        var now = DateTimeOffset.Now;
        var added = 0;
        WakeOnLanHost? last = null;
        foreach (var entry in selected)
        {
            if (_hosts.Any(h => WakeOnLanService.TryNormalizeMac(h.MacAddress, out var mac) &&
                                string.Equals(mac, entry.MacAddress, StringComparison.OrdinalIgnoreCase)))
                continue;

            last = new WakeOnLanHost
            {
                Name = string.IsNullOrWhiteSpace(entry.HostName) ? entry.IpAddress : entry.HostName,
                MacAddress = entry.MacAddress,
                IpAddress = entry.IpAddress,
                Port = WakeOnLanService.DefaultPort,
                Notes = "由局域网扫描添加",
                CreatedAt = now,
                UpdatedAt = now
            };
            _hosts.Add(last);
            added++;
        }

        if (added == 0)
        {
            StatusText.Text = "所选设备的 MAC 已在列表中，无需重复添加。";
            return;
        }

        await SaveDocumentAsync();
        RefreshHostSummary();
        StatusText.Text = $"已添加 {added} 台设备，可在「唤醒设备」中查看。";
        MainTabs.SelectedIndex = 0;
        HostGrid.SelectedItem = last;
    }
}
