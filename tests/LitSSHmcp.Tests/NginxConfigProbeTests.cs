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
    public void Parses_process_instances_and_prefers_master_over_worker()
    {
        const string output = """
            ##NGX_PROC	100	0	master	/home/u/nginx/sbin/nginx	/home/u	/home/u/nginx/conf/nginx.conf	/home/u/nginx
            ##NGX_PROC	101	0	worker	/home/u/nginx/sbin/nginx	/home/u	/home/u/nginx/conf/nginx.conf	/home/u/nginx
            ##NGX_CAND	0		path	/usr/local/nginx/sbin/nginx		/usr/local/nginx/conf/nginx.conf	/usr/local/nginx
            ##NGINX_BIN /home/u/nginx/sbin/nginx
            ##BIN /home/u/nginx/sbin/nginx
            nginx version: nginx/1.24.0
            """;

        var instances = NginxConfigProbe.ParseInstances(output);

        Assert.Equal(2, instances.Count);
        var primary = Assert.Single(instances, instance => instance.Pid == 100);
        Assert.True(primary.IsMaster);
        Assert.Equal("/home/u/nginx/conf/nginx.conf", primary.ConfPath);
        Assert.Equal("/home/u/nginx", primary.Prefix);
        Assert.Equal("process", primary.Source);
        Assert.Equal(100, NginxConfigProbe.PrimaryInstance(instances)!.Pid);

        var detected = NginxConfigProbe.ParseDetect(output);
        Assert.NotNull(detected);
        Assert.Equal("/home/u/nginx/sbin/nginx", detected!.Bin);
        Assert.Equal("Nginx", detected.ServerType);
    }

    [Fact]
    public void Parses_process_visible_without_readable_binary()
    {
        var instances = NginxConfigProbe.ParseInstances(
            "##NGX_PROC\t200\t1000\tmaster\t\t\t\t");

        var instance = Assert.Single(instances);
        Assert.False(instance.HasBinary);
        Assert.Equal(200, instance.Pid);
        Assert.Equal(1000, instance.Uid);
        Assert.False(NginxConfigProbe.PrimaryInstance(instances)!.HasBinary);
    }

    [Fact]
    public void Builds_config_command_with_process_prefix_and_conf()
    {
        var instance = new NginxInstance(100, 1000, "master", "/home/u/nginx/sbin/nginx",
            "/home/u", "conf/nginx.conf", "/home/u/nginx", "process");

        var command = NginxConfigProbe.BuildConfigCommand(instance);

        Assert.Contains("-p '/home/u/nginx'", command);
        Assert.Contains("-c '/home/u/nginx/conf/nginx.conf'", command);
        Assert.Contains("-T", command);
        Assert.Contains("head -", command);
    }

    [Fact]
    public void Fallback_includes_user_directories_and_compiled_prefix()
    {
        var fallback = NginxConfigProbe.BuildFallbackConfigCommand("/home/u/nginx/sbin/nginx");

        Assert.Contains("--conf-path", fallback);
        Assert.Contains("--prefix", fallback);
        Assert.Contains("$HOME/nginx/conf/nginx.conf", fallback);
        Assert.Contains("$HOME/openresty/nginx/conf/nginx.conf", fallback);
        Assert.Contains("sites-enabled", fallback);
        Assert.Contains("vhost", fallback);
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
