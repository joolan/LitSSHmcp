using LitSSHmcp.Core.Services.Snapshot.Collectors;
using Xunit;

namespace LitSSHmcp.Tests;

public class NginxConfigProbeTests
{
    [Fact]
    public void Parses_nginx_tengine_openresty_detect_output()
    {
        var nginx = NginxConfigProbe.ParseDetect(
            "##BIN /usr/sbin/nginx\nnginx version: nginx/1.24.0");
        Assert.NotNull(nginx);
        Assert.Equal("Nginx", nginx!.ServerType);
        Assert.Equal("/usr/sbin/nginx", nginx.Bin);
        Assert.Equal("nginx/1.24.0", nginx.Version);

        var tengine = NginxConfigProbe.ParseDetect(
            "##BIN /www/server/nginx/sbin/nginx\nTengine version: Tengine/2.3.3 (nginx/1.18.0)");
        Assert.NotNull(tengine);
        Assert.Equal("Tengine", tengine!.ServerType);
        Assert.Equal("/www/server/nginx/sbin/nginx", tengine.Bin);

        var openresty = NginxConfigProbe.ParseDetect(
            "##BIN /usr/local/openresty/nginx/sbin/nginx\nnginx version: openresty/1.21.4.1");
        Assert.NotNull(openresty);
        Assert.Equal("OpenResty", openresty!.ServerType);
    }

    [Fact]
    public void Returns_null_without_bin()
    {
        Assert.Null(NginxConfigProbe.ParseDetect(""));
        Assert.Null(NginxConfigProbe.ParseDetect("nginx: command not found"));
    }

    [Fact]
    public void Builds_config_and_fallback_commands()
    {
        var config = NginxConfigProbe.BuildConfigCommand("/usr/sbin/nginx");
        Assert.Contains("-T", config);
        Assert.Contains("head -", config);

        var fallback = NginxConfigProbe.BuildFallbackConfigCommand("/usr/sbin/nginx");
        Assert.Contains("--conf-path", fallback);
        Assert.Contains("sites-enabled", fallback);
        Assert.Contains("vhost", fallback);
    }
}
