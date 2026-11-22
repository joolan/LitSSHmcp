using System.Globalization;

namespace LitSSHmcp.App.Services;

/// <summary>启动免责声明的“本月不再提示”状态。</summary>
public static class DisclaimerPolicy
{
    public const string SkipMonthKey = "disclaimer.skipMonth";

    public static string MonthKey(DateTime value) =>
        value.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static bool ShouldShow(string? skipMonth, DateTime now) =>
        !string.Equals(skipMonth ?? string.Empty, MonthKey(now), StringComparison.Ordinal);
}
