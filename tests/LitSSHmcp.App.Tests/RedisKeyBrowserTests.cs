using System.Data;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class RedisKeyBrowserTests
{
    private static readonly DataSourceConfig Redis =
        new() { Id = "r1", Name = "redis", Type = "redis" };

    private sealed class FakeRegistry : IDatasourceDriverRegistry
    {
        private readonly IDatasourceDriver? _driver;
        public FakeRegistry(IDatasourceDriver? driver) => _driver = driver;
        public IDatasourceDriver? Get(string type) => _driver;
        public IDatasourceDriver GetRequired(string type) => _driver!;
        public IReadOnlyCollection<string> SupportedTypes => new[] { "redis" };
    }

    private sealed class FakeRedisDriver : IDatasourceDriver, IRedisKeyExplorer
    {
        private readonly Dictionary<string, RedisScanPage> _pages = new()
        {
            ["0"] = new RedisScanPage { Keys = new[] { "user:1", "user:2" }, Cursor = "1" },
            ["1"] = new RedisScanPage { Keys = new[] { "user:3" }, Cursor = "0" },
        };

        public string Type => "redis";
        public int MetaCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public string? LastMatch { get; private set; }
        public int? LastDb { get; private set; }

        public Task<RedisScanPage> ScanKeysPageAsync(DataSourceConfig ds, string match, string cursor, int count, CancellationToken ct = default)
        {
            LastMatch = match;
            LastDb = int.TryParse(ds.DefaultDatabase, out var db) ? db : null;
            return Task.FromResult(_pages.TryGetValue(cursor, out var page) ? page : new RedisScanPage());
        }

        public Task<IReadOnlyList<string>> ScanKeysAsync(DataSourceConfig ds, string match, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(_pages["0"].Keys);

        public Task<IReadOnlyList<RedisKeyMeta>> GetKeysMetaAsync(DataSourceConfig ds, IReadOnlyList<string> keys, CancellationToken ct = default)
        {
            MetaCalls++;
            IReadOnlyList<RedisKeyMeta> metas = keys
                .Select(k => new RedisKeyMeta { Key = k, Type = "string", TtlSeconds = -1, SizeBytes = 3 })
                .ToList();
            return Task.FromResult(metas);
        }

        public Task<RedisKeyValue> GetKeyValueAsync(DataSourceConfig ds, string key, int maxRows = 200, CancellationToken ct = default)
        {
            ReadCalls++;
            return Task.FromResult(new RedisKeyValue
            {
                Success = true,
                Type = "string",
                TtlSeconds = -1,
                TotalCount = 3,
                Data = new DatasourceQueryResult
                {
                    Success = true,
                    Columns = new[] { "value" },
                    Rows = new List<Dictionary<string, object?>> { new() { ["value"] = "abc" } },
                    RowCount = 1
                }
            });
        }

        public Task<DatasourceQueryResult> QueryAsync(DataSourceConfig ds, string sql, int maxRows, CancellationToken ct = default)
            => Task.FromResult(new DatasourceQueryResult
            {
                Success = true,
                Columns = new[] { "value" },
                Rows = new List<Dictionary<string, object?>> { new() { ["value"] = 9L } },
                RowCount = 1
            });

        public Task<DatasourceTestResult> TestAsync(DataSourceConfig ds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DatasourceExecuteResult> ExecuteAsync(DataSourceConfig ds, string sql, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DatasourceQueryResult> ExplainAsync(DataSourceConfig ds, string sql, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DatasourceDiagnosticsResult> DiagnoseAsync(DataSourceConfig ds, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Host
    {
        public string Status = string.Empty;
        public string Sql = string.Empty;
        public bool Busy;
        public bool WriteMode;
        public DataView? Results;
        public DataView? Tabs;
        public int Writes;
        public int Refreshes;

        public RedisKeyBrowser Create(FakeRedisDriver driver)
        {
            return new RedisKeyBrowser(
                () => Redis,
                () => true,
                () => Busy,
                value => Busy = value,
                value => Status = value,
                value => Sql = value,
                value => Results = value,
                () => WriteMode,
                (_, _) => { Writes++; return Task.FromResult(true); },
                value => Tabs = value,
                ds => new DataSourceConfig
                {
                    Id = ds.Id,
                    Name = ds.Name,
                    Type = ds.Type,
                    DefaultDatabase = "3"
                },
                new FakeRegistry(driver),
                () => Refreshes++);
        }
    }

    [Fact]
    public async Task Scan_loads_pages_until_cursor_is_zero()
    {
        var driver = new FakeRedisDriver();
        var host = new Host();
        var browser = host.Create(driver);

        await browser.ScanKeysAsync();

        Assert.Equal(new[] { "user:1", "user:2", "user:3" }, browser.Keys.Select(k => k.Key));
        Assert.False(browser.CanScanMore);
        Assert.False(browser.IsScanning);
        Assert.False(host.Busy);
        Assert.Contains("扫描完成", host.Status);
        Assert.Contains("3", browser.ScanProgress);
        Assert.Equal("*", driver.LastMatch);
        Assert.Equal(3, driver.LastDb);
        Assert.True(host.Refreshes > 0);
    }

    [Fact]
    public async Task Scan_uses_star_when_pattern_is_blank()
    {
        var driver = new FakeRedisDriver();
        var host = new Host();
        var browser = host.Create(driver);
        browser.KeyPattern = "  ";

        await browser.ScanKeysAsync();

        Assert.Equal("*", driver.LastMatch);
    }

    [Fact]
    public async Task Reset_clears_keys_and_progress()
    {
        var driver = new FakeRedisDriver();
        var host = new Host();
        var browser = host.Create(driver);
        await browser.ScanKeysAsync();
        browser.KeyInfo = "info";

        browser.Reset();

        Assert.Empty(browser.Keys);
        Assert.Equal(string.Empty, browser.ScanProgress);
        Assert.Equal(string.Empty, browser.KeyInfo);
        Assert.False(browser.HasKeyInfo);
    }

    [Fact]
    public async Task Load_meta_fills_type_ttl_and_size()
    {
        var driver = new FakeRedisDriver();
        var host = new Host();
        var browser = host.Create(driver);
        browser.Keys.Add(new RedisKeyRow("user:1"));
        browser.Keys.Add(new RedisKeyRow("user:2"));

        await browser.LoadKeyMetaAsync();

        Assert.Equal(1, driver.MetaCalls);
        Assert.All(browser.Keys, row =>
        {
            Assert.True(row.MetaLoaded);
            Assert.Equal("string", row.Type);
            Assert.Equal(-1, row.TtlSeconds);
            Assert.Equal(3, row.SizeBytes);
        });
        Assert.Contains("已补全 2", host.Status);
        Assert.False(host.Busy);
    }

    [Fact]
    public async Task Use_key_sets_results_tabs_and_info()
    {
        var driver = new FakeRedisDriver();
        var host = new Host();
        var browser = host.Create(driver);
        var row = new RedisKeyRow("user:1");
        browser.Keys.Add(row);

        await browser.UseKeyAsync(row);

        Assert.Equal(1, driver.ReadCalls);
        Assert.NotNull(host.Results);
        Assert.NotNull(host.Tabs);
        Assert.Contains("user:1", browser.KeyInfo);
        Assert.Contains("永不过期", browser.KeyInfo);
        Assert.Contains("1 项", host.Status);
        Assert.False(host.Busy);
    }

    [Fact]
    public async Task Delete_requires_write_mode()
    {
        var driver = new FakeRedisDriver();
        var host = new Host();
        var browser = host.Create(driver);
        var row = new RedisKeyRow("user:1");
        browser.Keys.Add(row);
        browser.SelectedKey = row;

        await browser.DeleteKeyAsync(row);

        Assert.Equal(0, host.Writes);
        Assert.Contains("写入模式", host.Status);
        Assert.Contains(row, browser.Keys);
    }

    [Fact]
    public async Task Delete_removes_key_and_clears_selection()
    {
        var driver = new FakeRedisDriver();
        var host = new Host { WriteMode = true };
        var browser = host.Create(driver);
        var row = new RedisKeyRow("user:1");
        browser.Keys.Add(row);
        browser.SelectedKey = row;

        await browser.DeleteKeyAsync(row);

        Assert.Equal(1, host.Writes);
        Assert.Empty(browser.Keys);
        Assert.Null(browser.SelectedKey);
    }

    [Fact]
    public void Key_to_editor_quotes_argument()
    {
        var host = new Host();
        var browser = host.Create(new FakeRedisDriver());

        browser.KeyToEditor(new RedisKeyRow("user name"));

        Assert.Equal("GET \"user name\"", host.Sql);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("a\\b", "\"a\\\\b\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("a'b", "\"a'b\"")]
    public void Quote_argument_matches_redis_parser(string value, string expected)
        => Assert.Equal(expected, RedisKeyBrowser.QuoteArgument(value));

    [Theory]
    [InlineData(-1, "永不过期")]
    [InlineData(-2, "键不存在")]
    [InlineData(0, "0 秒")]
    [InlineData(3600, "3600 秒")]
    public void Format_ttl_maps_special_values(long ttl, string expected)
        => Assert.Equal(expected, RedisKeyBrowser.FormatTtl(ttl));
}
