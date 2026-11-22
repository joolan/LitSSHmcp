using System.Threading;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Views;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class DisclaimerTests
{
    [Fact]
    public void Disclaimer_is_suppressed_only_for_the_same_month()
    {
        var now = new DateTime(2026, 11, 22);
        Assert.True(DisclaimerPolicy.ShouldShow(null, now));
        Assert.True(DisclaimerPolicy.ShouldShow("2026-10", now));
        Assert.False(DisclaimerPolicy.ShouldShow("2026-11", now));
        Assert.True(DisclaimerPolicy.ShouldShow("2025-11", now));
    }

    [Fact]
    public void Disclaimer_window_parses_xaml()
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { _ = new DisclaimerWindow(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(captured);
    }
}
