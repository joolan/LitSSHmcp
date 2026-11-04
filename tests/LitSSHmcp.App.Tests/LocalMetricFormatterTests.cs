using LitSSHmcp.App.Services;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class LocalMetricFormatterTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1024L * 1024 * 1024, "1 GB")]
    public void Bytes_uses_binary_units(long bytes, string expected)
        => Assert.Equal(expected, LocalMetricFormatter.Bytes(bytes));

    [Theory]
    [InlineData(0, "0 B/s")]
    [InlineData(1024, "1 KB/s")]
    [InlineData(1024 * 1024 * 1.5, "1.5 MB/s")]
    public void Rate_uses_binary_units_per_second(double rate, string expected)
        => Assert.Equal(expected, LocalMetricFormatter.Rate(rate));

    [Theory]
    [InlineData(0, "0%")]
    [InlineData(12.34, "12.3%")]
    [InlineData(100, "100%")]
    [InlineData(150, "100%")]
    public void Percent_clamps_to_zero_hundred(double value, string expected)
        => Assert.Equal(expected, LocalMetricFormatter.Percent(value));

    [Theory]
    [InlineData(30, "不足 1 分钟")]
    [InlineData(300, "5 分钟")]
    [InlineData(3900, "1 小时 5 分")]
    [InlineData(90000, "1 天 1 小时")]
    public void Uptime_is_human_readable(int seconds, string expected)
        => Assert.Equal(expected, LocalMetricFormatter.Uptime(TimeSpan.FromSeconds(seconds)));
}
