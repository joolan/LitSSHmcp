using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;

namespace LitSSHmcp.App.ViewModels;

/// <summary>
/// Redis 键浏览：SCAN 分页、类型/TTL 补全、本地过滤与键值读取。
/// 由 <see cref="DbWorkbenchViewModel"/> 把数据源/忙碌/状态等宿主状态以委托传入，便于单独演进与测试。
/// </summary>
public sealed class RedisKeyBrowser : INotifyPropertyChanged
{
    private readonly Func<DataSourceConfig?> _datasource;
    private readonly Func<bool> _isRedis;
    private readonly Func<bool> _isBusy;
    private readonly Action<bool> _setBusy;
    private readonly Action<string> _setStatus;
    private readonly Action<string> _setSql;
    private readonly Action<DataView?> _setResults;
    private readonly Func<bool> _isWriteMode;
    private readonly Func<DataSourceConfig, string, Task<bool>> _writeAsync;
    private readonly Action<DataView?> _rebuildTabs;
    private readonly Func<DataSourceConfig, DataSourceConfig> _effective;
    private readonly IDatasourceDriverRegistry _registry;
    private readonly Action _refreshCommands;

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _readCts;
    private Task? _activeScan;
    private string _scanCursor = "0";
    private bool _scanHasMore;
    private bool _isScanning;
    private string _scanProgress = string.Empty;
    private long? _redisTotalKeys;
    private string _keyPattern = "*";
    private string _keyInfo = string.Empty;
    private string _filter = string.Empty;
    private string _typeFilter = TypeAll;
    private RedisKeyRow? _selectedKey;
    private ListCollectionView? _view;

    /// <summary>每次扫描（点一次按钮）的时间预算；超预算自动停，交给「继续扫描」。</summary>
    private static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(2);

    /// <summary>「补全类型/TTL/大小」的时间预算（逐键 3 条命令，不能无上限）。</summary>
    private static readonly TimeSpan MetaBudget = TimeSpan.FromSeconds(10);

    /// <summary>单批 SCAN 的 COUNT（每批最多返回的键数）。</summary>
    private const int ScanBatchCount = 500;

    /// <summary>单次扫描最多执行的批次数（与预算共同兜底）。</summary>
    private const int ScanMaxBatches = 20;

    /// <summary>「全部」类型过滤项。</summary>
    public const string TypeAll = "全部";

    public RedisKeyBrowser(
        Func<DataSourceConfig?> datasource,
        Func<bool> isRedis,
        Func<bool> isBusy,
        Action<bool> setBusy,
        Action<string> setStatus,
        Action<string> setSql,
        Action<DataView?> setResults,
        Func<bool> isWriteMode,
        Func<DataSourceConfig, string, Task<bool>> writeAsync,
        Action<DataView?> rebuildTabs,
        Func<DataSourceConfig, DataSourceConfig> effective,
        IDatasourceDriverRegistry registry,
        Action refreshCommands)
    {
        _datasource = datasource;
        _isRedis = isRedis;
        _isBusy = isBusy;
        _setBusy = setBusy;
        _setStatus = setStatus;
        _setSql = setSql;
        _setResults = setResults;
        _isWriteMode = isWriteMode;
        _writeAsync = writeAsync;
        _rebuildTabs = rebuildTabs;
        _effective = effective;
        _registry = registry;
        _refreshCommands = refreshCommands;
    }

    /// <summary>SCAN 出的键（类型/TTL/大小按需补全）。</summary>
    public ObservableCollection<RedisKeyRow> Keys { get; } = new();

    /// <summary>键列表的视图（本地过滤 + 列头排序都作用在它上面）。</summary>
    public ListCollectionView View =>
        _view ??= (ListCollectionView)CollectionViewSource.GetDefaultView(Keys);

    /// <summary>类型过滤下拉选项（补全类型后按类型本地过滤）。</summary>
    public IReadOnlyList<string> TypeOptions { get; } =
        new[] { TypeAll, "string", "hash", "list", "set", "zset", "stream" };

    /// <summary>键列表本地过滤关键字（在已加载的键中即时过滤，不回服务端）。</summary>
    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) RefreshView(); }
    }

    /// <summary>键类型过滤（"全部" 或补全后的 string/hash/… ；本地过滤）。</summary>
    public string TypeFilter
    {
        get => _typeFilter;
        set { if (Set(ref _typeFilter, value)) RefreshView(); }
    }

    /// <summary>当前选中的键行（右键菜单用）。</summary>
    public RedisKeyRow? SelectedKey
    {
        get => _selectedKey;
        set => Set(ref _selectedKey, value);
    }

    /// <summary>是否正在扫描 / 补全键信息（驱动进度条与「停止」按钮）。</summary>
    public bool IsScanning
    {
        get => _isScanning;
        set
        {
            if (!Set(ref _isScanning, value)) return;
            OnPropertyChanged(nameof(HasScanProgress));
            _refreshCommands();
        }
    }

    /// <summary>扫描/补全进度文本（已加载键数、耗时、DBSIZE 总数）。</summary>
    public string ScanProgress
    {
        get => _scanProgress;
        set { if (Set(ref _scanProgress, value)) OnPropertyChanged(nameof(HasScanProgress)); }
    }

    public bool HasScanProgress => !string.IsNullOrEmpty(_scanProgress);

    /// <summary>是否还能继续扫描（游标未走完）。</summary>
    public bool CanScanMore => _isRedis() && _scanHasMore && !_isBusy();

    /// <summary>Redis 键扫描的 MATCH 模式。</summary>
    public string KeyPattern
    {
        get => _keyPattern;
        set => Set(ref _keyPattern, value);
    }

    /// <summary>当前查看的 Redis 键信息（类型 / 生存时间）。</summary>
    public string KeyInfo
    {
        get => _keyInfo;
        set { if (Set(ref _keyInfo, value)) OnPropertyChanged(nameof(HasKeyInfo)); }
    }

    public bool HasKeyInfo => !string.IsNullOrEmpty(_keyInfo);

    /// <summary>切换数据源时清空键列表、扫描游标与键信息。</summary>
    public void Reset()
    {
        StopScan();
        CancelRead();
        Keys.Clear();
        KeyInfo = string.Empty;
        ResetScanState();
    }

    /// <summary>重新扫描：游标归零、清空列表，带 2s 时间预算（不会因键空间大而卡死）。</summary>
    public Task ScanKeysAsync() => StartScanAsync(reset: true);

    /// <summary>继续上次未完成的扫描（游标续扫，再给 2s 预算）。</summary>
    public Task ScanMoreAsync() => StartScanAsync(reset: false);

    private async Task StartScanAsync(bool reset)
    {
        var previous = _activeScan;
        if (previous is not null)
        {
            try { await previous; } catch { /* 上一次扫描的异常已在其内部处理 */ }
        }

        var run = RunScanAsync(reset);
        _activeScan = run;
        try
        {
            await run;
        }
        finally
        {
            if (ReferenceEquals(_activeScan, run))
                _activeScan = null;
        }
    }

    /// <summary>停止当前扫描/补全（Esc 也走这里）。</summary>
    public void StopScan()
    {
        try { _scanCts?.Cancel(); } catch { /* ignore */ }
    }

    /// <summary>取消正在进行的键值读取（Esc 也走这里）。</summary>
    public void CancelRead()
    {
        try { _readCts?.Cancel(); } catch { /* ignore */ }
    }

    private void ResetScanState()
    {
        _scanCursor = "0";
        _scanHasMore = false;
        _redisTotalKeys = null;
        ScanProgress = string.Empty;
        OnPropertyChanged(nameof(CanScanMore));
    }

    private async Task RunScanAsync(bool reset)
    {
        var ds = _datasource();
        if (ds is null || !_isRedis() || _isBusy()) return;
        var explorer = _registry.Get(ds.Type) as IRedisKeyExplorer;
        if (explorer is null) { _setStatus("该数据源不支持键浏览"); return; }

        if (reset)
        {
            ResetScanState();
            Keys.Clear();
            KeyInfo = string.Empty;
            _scanHasMore = true;
        }
        else if (!_scanHasMore)
        {
            return;
        }

        var effective = _effective(ds);
        var pattern = string.IsNullOrWhiteSpace(KeyPattern) ? "*" : KeyPattern.Trim();

        _setBusy(true);
        IsScanning = true;
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var token = cts.Token;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var batches = 0;
        _setStatus(reset ? "扫描键…" : "继续扫描…");

        try
        {
            if (reset)
                _redisTotalKeys = await TryGetTotalKeysAsync(effective, token);

            while (_scanHasMore && batches < ScanMaxBatches && stopwatch.Elapsed < ScanBudget && !token.IsCancellationRequested)
            {
                var page = await explorer.ScanKeysPageAsync(effective, pattern, _scanCursor, ScanBatchCount, token);
                _scanCursor = page.Cursor;
                _scanHasMore = page.Cursor != "0";
                batches++;

                foreach (var key in page.Keys)
                    Keys.Add(new RedisKeyRow(key));

                ScanProgress = ScanProgressText(stopwatch.Elapsed);
                OnPropertyChanged(nameof(CanScanMore));
            }

            _setStatus(token.IsCancellationRequested
                ? $"已停止扫描：已加载 {Keys.Count} 个键。"
                : _scanHasMore
                    ? $"已加载 {Keys.Count} 个键（{stopwatch.Elapsed.TotalSeconds:F1}s，模式 {pattern}），按「继续扫描」加载更多"
                    : $"扫描完成：共 {Keys.Count} 个键（模式 {pattern}，{stopwatch.Elapsed.TotalMilliseconds:F0} ms）");
        }
        catch (OperationCanceledException)
        {
            _setStatus($"已停止扫描：已加载 {Keys.Count} 个键。");
        }
        catch (Exception ex)
        {
            _setStatus("扫描键失败: " + ex.Message);
        }
        finally
        {
            ScanProgress = ScanProgressText(stopwatch.Elapsed);
            IsScanning = false;
            _setBusy(false);
            if (ReferenceEquals(_scanCts, cts))
                _scanCts = null;
            cts.Dispose();
            _refreshCommands();
            OnPropertyChanged(nameof(CanScanMore));
        }
    }

    private string ScanProgressText(TimeSpan elapsed) =>
        _redisTotalKeys is long total
            ? $"已加载 {Keys.Count} / {total} 键 · {elapsed.TotalSeconds:F1}s"
            : $"已加载 {Keys.Count} 键 · {elapsed.TotalSeconds:F1}s";

    /// <summary>DBSIZE（仅用于进度展示，失败不影响扫描）。</summary>
    private async Task<long?> TryGetTotalKeysAsync(DataSourceConfig ds, CancellationToken ct)
    {
        try
        {
            var driver = _registry.Get(ds.Type);
            if (driver is null) return null;
            var result = await driver.QueryAsync(ds, "DBSIZE", 1, ct);
            if (result.Success && result.Rows.Count > 0 &&
                result.Rows[0].TryGetValue("value", out var value) && value is not null)
                return Convert.ToInt64(value);
        }
        catch { /* 总数只是进度参考 */ }
        return null;
    }

    /// <summary>批量补全键的类型/TTL/大小（逐键 3 条命令，带 10s 预算与取消）。</summary>
    public async Task LoadKeyMetaAsync()
    {
        var ds = _datasource();
        if (ds is null || !_isRedis() || _isBusy()) return;
        var explorer = _registry.Get(ds.Type) as IRedisKeyExplorer;
        if (explorer is null) return;

        var pending = Keys.Where(r => !r.MetaLoaded).ToList();
        if (pending.Count == 0)
        {
            _setStatus("所有键的类型/TTL/大小已补全。");
            return;
        }

        var lookup = pending.ToDictionary(r => r.Key, StringComparer.Ordinal);
        var effective = _effective(ds);

        _setBusy(true);
        IsScanning = true;
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var token = cts.Token;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        const int chunk = 50;
        var done = 0;

        try
        {
            while (done < pending.Count && stopwatch.Elapsed < MetaBudget)
            {
                token.ThrowIfCancellationRequested();

                var batch = pending.Skip(done).Take(chunk).Select(r => r.Key).ToList();
                var metas = await explorer.GetKeysMetaAsync(effective, batch, token);
                foreach (var meta in metas)
                {
                    if (!lookup.TryGetValue(meta.Key, out var row)) continue;
                    row.Type = meta.Type;
                    row.TtlSeconds = meta.TtlSeconds;
                    row.SizeBytes = meta.SizeBytes;
                }

                done += batch.Count;
                ScanProgress = $"已补全 {done}/{pending.Count} 键的类型/TTL/大小…";
            }

            RefreshView();
            _setStatus(done >= pending.Count
                ? $"已补全 {done} 个键的类型/TTL/大小（{stopwatch.Elapsed.TotalSeconds:F1}s）。"
                : $"已补全 {done}/{pending.Count}（{stopwatch.Elapsed.TotalSeconds:F1}s 预算），可再次点击继续。");
        }
        catch (OperationCanceledException)
        {
            RefreshView();
            _setStatus($"已停止补全：{done}/{pending.Count}。");
        }
        catch (Exception ex)
        {
            _setStatus("补全键信息失败: " + ex.Message);
        }
        finally
        {
            IsScanning = false;
            _setBusy(false);
            if (ReferenceEquals(_scanCts, cts))
                _scanCts = null;
            cts.Dispose();
            _refreshCommands();
        }
    }

    /// <summary>读取选中键的值（有界读取：hash/set 走 HSCAN/SSCAN、string 超 64KB 截断）。</summary>
    public async Task UseKeyAsync(RedisKeyRow? row)
    {
        var ds = _datasource();
        if (ds is null || row is null || string.IsNullOrWhiteSpace(row.Key) || !_isRedis() || _isBusy()) return;
        var explorer = _registry.Get(ds.Type) as IRedisKeyExplorer;
        if (explorer is null) return;

        var key = row.Key;
        _setBusy(true);
        _setStatus($"读取键 {key}…");
        var cts = new CancellationTokenSource();
        _readCts = cts;
        try
        {
            var result = await explorer.GetKeyValueAsync(_effective(ds), key, 200, cts.Token);
            if (!result.Success)
            {
                _setStatus("读取键失败: " + result.Error);
                _setResults(null);
                KeyInfo = string.Empty;
                _rebuildTabs(null);
                return;
            }

            var view = result.Data is not null ? WorkbenchResultView.Build(result.Data) : null;
            _setResults(view);
            // 查询报错后选中的「消息」标签会盖住键值结果（ShowResultGrid=false），必须切回「结果」标签
            _rebuildTabs(view);
            KeyInfo = BuildKeyInfo(key, result);
            _setStatus(result.Data is not null
                ? $"{result.Data.RowCount} 项，{result.Data.DurationMs:F0} ms" + (result.Data.Truncated ? "（已截断）" : "")
                : (result.Error ?? "该键无值可显示"));
        }
        catch (OperationCanceledException)
        {
            _setStatus("已取消");
        }
        catch (Exception ex)
        {
            _setStatus("读取键失败: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_readCts, cts))
                _readCts = null;
            cts.Dispose();
            _setBusy(false);
            _refreshCommands();
        }
    }

    /// <summary>键信息栏：类型 / 生存时间 / 总数 / 是否截断。</summary>
    private static string BuildKeyInfo(string key, RedisKeyValue result)
    {
        var info = $"键：{key}    类型：{result.Type}    生存时间：{FormatTtl(result.TtlSeconds)}";
        if (result.TotalCount is long total)
            info += result.Type == "string" ? $"    长度：{total} 字节" : $"    元素数：{total}";
        if (result.Data is { Truncated: true })
            info += $"    （已截断，仅显示前 {result.Data.RowCount} 项）";
        return info;
    }

    public void CopyKey(RedisKeyRow? row)
    {
        if (row is null) return;
        try
        {
            Clipboard.SetText(row.Key);
            _setStatus("已复制键名。");
        }
        catch (Exception ex)
        {
            _setStatus("复制失败: " + ex.Message);
        }
    }

    public void KeyToEditor(RedisKeyRow? row)
    {
        if (row is null) return;
        _setSql("GET " + QuoteArgument(row.Key));
        _setStatus("已把 GET 命令填入输入框（Ctrl+Enter 执行）。");
    }

    /// <summary>删除选中键（仅写入模式；走既有写确认 + 审计链）。</summary>
    public async Task DeleteKeyAsync(RedisKeyRow? row)
    {
        var ds = _datasource();
        if (ds is null || row is null || !_isRedis()) return;
        if (!_isWriteMode())
        {
            _setStatus("删除键需要先切换到「写入模式」。");
            return;
        }

        var command = $"DEL {QuoteArgument(row.Key)}";
        var ok = await _writeAsync(ds, command);
        if (ok)
        {
            Keys.Remove(row);
            if (ReferenceEquals(SelectedKey, row))
                SelectedKey = null;
        }
    }

    /// <summary>给 Redis 参数加引号（含空白/引号/反斜杠时），与 RedisCommandParser 的转义规则匹配。</summary>
    public static string QuoteArgument(string value)
    {
        var needsQuote = value.Length == 0 ||
                         value.Any(char.IsWhiteSpace) ||
                         value.Contains('\\') ||
                         value.Contains('"') ||
                         value.Contains('\'');
        return needsQuote
            ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
            : value;
    }

    // ---------- 键列表本地过滤 ----------

    private void RefreshView()
    {
        var view = View;
        view.Filter = o => MatchesFilter(o as RedisKeyRow);
        view.Refresh();
    }

    private bool MatchesFilter(RedisKeyRow? row)
    {
        if (row is null)
            return false;

        if (!string.IsNullOrWhiteSpace(_typeFilter) &&
            _typeFilter != TypeAll &&
            !string.Equals(row.Type ?? string.Empty, _typeFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(_filter))
            return true;

        return row.Key.Contains(_filter.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatTtl(long ttlSeconds) => ttlSeconds switch
    {
        -1 => "永不过期",
        -2 => "键不存在",
        _ => $"{ttlSeconds} 秒"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
