using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using Xunit;

namespace LitSSHmcp.Tests;

public class RemoteCopyCommandBuilderTests
{
    private static SshServerConfig Server(string host = "10.0.0.5", int port = 22, string? internalHost = null, int? internalPort = null)
        => new()
        {
            Id = "s1",
            Name = "srv",
            Host = host,
            Port = port,
            InternalHost = internalHost,
            InternalPort = internalPort,
            Username = "root",
            AuthType = AuthType.Password,
            Password = "secret"
        };

    [Fact]
    public void ShellQuote_wraps_and_escapes_single_quotes()
    {
        Assert.Equal("'/tmp/a'", RemoteCopyCommandBuilder.ShellQuote("/tmp/a"));
        Assert.Equal("'a'\\''b'", RemoteCopyCommandBuilder.ShellQuote("a'b"));
    }

    [Fact]
    public void ResolveEndpoint_prefers_internal_when_configured()
    {
        var s = Server("1.2.3.4", 22, "10.0.0.9", 2222);
        Assert.Equal(("10.0.0.9", 2222), RemoteCopyCommandBuilder.ResolveEndpoint(s, useInternal: true));
        Assert.Equal(("1.2.3.4", 22), RemoteCopyCommandBuilder.ResolveEndpoint(s, useInternal: false));
    }

    [Fact]
    public void ResolveEndpoint_internal_port_falls_back_to_host_port()
    {
        var s = Server("1.2.3.4", 2200, "10.0.0.9", null);
        Assert.Equal(("10.0.0.9", 2200), RemoteCopyCommandBuilder.ResolveEndpoint(s, useInternal: true));
    }

    [Fact]
    public void ResolveEndpoint_falls_back_when_internal_missing()
    {
        var s = Server("1.2.3.4", 22);
        Assert.False(RemoteCopyCommandBuilder.HasInternal(s));
        Assert.Equal(("1.2.3.4", 22), RemoteCopyCommandBuilder.ResolveEndpoint(s, useInternal: true));
    }

    [Fact]
    public void Parent_and_base_name()
    {
        Assert.Equal("/var/log", RemoteCopyCommandBuilder.ParentOf("/var/log/app.log"));
        Assert.Equal("/", RemoteCopyCommandBuilder.ParentOf("/root"));
        Assert.Equal(".", RemoteCopyCommandBuilder.ParentOf("file.txt"));
        Assert.Equal("app.log", RemoteCopyCommandBuilder.BaseNameOf("/var/log/app.log"));
        Assert.Equal("dir", RemoteCopyCommandBuilder.BaseNameOf("/a/dir/"));
    }

    [Theory]
    [InlineData("        1,234,567  45%   12.34MB/s    0:00:05 (xfr#1, to-chk=3/10)", 45)]
    [InlineData("100%", 100)]
    [InlineData("0%", 0)]
    public void ParseRsyncPercent_extracts_percentage(string line, int expected)
        => Assert.Equal(expected, RemoteCopyCommandBuilder.ParseRsyncPercent(line));

    [Theory]
    [InlineData("not a percent")]
    [InlineData("")]
    [InlineData("150%")]
    public void ParseRsyncPercent_ignores_invalid(string line)
        => Assert.Null(RemoteCopyCommandBuilder.ParseRsyncPercent(line));

    [Theory]
    [InlineData("37", 37)]
    [InlineData("  100 ", 100)]
    public void ParsePvPercent_parses_number(string line, int expected)
        => Assert.Equal(expected, RemoteCopyCommandBuilder.ParsePvPercent(line));

    [Fact]
    public void ParsePvPercent_ignores_text()
        => Assert.Null(RemoteCopyCommandBuilder.ParsePvPercent("12.3MB/s"));

    [Fact]
    public void BuildDirectCommand_rsync_keyauth_has_no_sshpass_or_bootstrap()
    {
        var req = new RemoteCopyRequest
        {
            Source = Server(),
            SourcePaths = new[] { "/var/log/a.log", "/etc/conf dir" },
            Target = Server("10.0.0.6"),
            TargetHost = "10.0.0.6",
            TargetPort = 22,
            TargetDirectory = "/backup/data dir",
            Compress = true
        };

        var cmd = RemoteCopyCommandBuilder.BuildDirectCommand(req, usePassword: false, useRsync: true, hasPv: true, totalBytes: 100);

        Assert.Contains("rsync -a -s --partial", cmd);
        Assert.Contains("-z", cmd);
        Assert.Contains("--info=progress2", cmd);
        Assert.Contains("mkdir -p --", cmd);
        Assert.Contains("root@10.0.0.6:/backup/data dir", cmd);
        Assert.DoesNotContain("sshpass", cmd);
        Assert.DoesNotContain("mktemp", cmd);
    }

    [Fact]
    public void BuildDirectCommand_password_uses_stdin_bootstrap_and_never_inlines_password()
    {
        var req = new RemoteCopyRequest
        {
            Source = Server(),
            SourcePaths = new[] { "/var/log/a.log" },
            Target = Server("10.0.0.6"),
            TargetHost = "10.0.0.6",
            TargetPort = 22,
            TargetDirectory = "/backup",
            Compress = true
        };

        var cmd = RemoteCopyCommandBuilder.BuildDirectCommand(req, usePassword: true, useRsync: true, hasPv: false, totalBytes: 0);

        Assert.Contains("mktemp -d", cmd);
        Assert.Contains("cat > \"$f\"", cmd);
        Assert.Contains("chmod 600", cmd);
        Assert.Contains("sshpass -f \"$f\"", cmd);
        Assert.Contains("trap 'rm -rf \"$d\"'", cmd);
        // 密码经 stdin 写入文件，绝不出现 -p / -e 或明文
        Assert.DoesNotContain("sshpass -p", cmd);
        Assert.DoesNotContain("sshpass -e", cmd);
        Assert.DoesNotContain("secret", cmd);
    }

    [Fact]
    public void BuildDirectCommand_tar_fallback_pipes_to_ssh()
    {
        var req = new RemoteCopyRequest
        {
            Source = Server(),
            SourcePaths = new[] { "/opt/app data" },
            Target = Server("10.0.0.6"),
            TargetHost = "10.0.0.6",
            TargetPort = 22,
            TargetDirectory = "/backup",
            Compress = false
        };

        var cmd = RemoteCopyCommandBuilder.BuildDirectCommand(req, usePassword: false, useRsync: false, hasPv: true, totalBytes: 12345);

        Assert.Contains("tar -C", cmd);
        Assert.Contains("pv -n -s 12345", cmd);
        Assert.Contains("-xf -", cmd);
    }

    [Fact]
    public void BuildSshOptions_uses_old_openssh_compatible_host_key_option()
    {
        var opts = RemoteCopyCommandBuilder.BuildSshOptions(keyAuth: true, port: 22);
        Assert.Contains("StrictHostKeyChecking=no", opts);
        Assert.DoesNotContain("accept-new", opts);
    }

    [Fact]
    public void BuildTargetProbeCommand_reports_rsync_and_tar_markers()
    {
        var cmd = RemoteCopyCommandBuilder.BuildTargetProbeCommand();
        Assert.Contains("LITSSH_T_RSYNC", cmd);
        Assert.Contains("LITSSH_T_TAR", cmd);
        Assert.Contains("LITSSH_T_TAR_SKIP", cmd);
    }

    [Fact]
    public void BuildDestFileListCommand_maps_to_target_directory()
    {
        var cmd = RemoteCopyCommandBuilder.BuildDestFileListCommand(new[] { "/opt/a dir", "/etc/x.log" }, "/backup");
        Assert.Contains("'/backup'", cmd);
        Assert.Contains("/opt/a dir", cmd);
        Assert.Contains("find . -type f", cmd);
    }

    [Fact]
    public void BuildCountExistingCommand_emits_skipped_marker()
    {
        var cmd = RemoteCopyCommandBuilder.BuildCountExistingCommand();
        Assert.Contains("LITSSH_SKIPPED:", cmd);
        Assert.Contains("[ -f ", cmd);
    }

    [Fact]
    public void BuildDirectCommand_tar_skip_old_files_flag()
    {
        var req = new RemoteCopyRequest
        {
            Source = Server(),
            SourcePaths = new[] { "/opt/app" },
            Target = Server("10.0.0.6"),
            TargetHost = "10.0.0.6",
            TargetPort = 22,
            TargetDirectory = "/backup",
            Overwrite = false
        };

        var withSkip = RemoteCopyCommandBuilder.BuildDirectCommand(req, usePassword: false, useRsync: false, hasPv: false, totalBytes: 0, tarSkipOldFiles: true);
        Assert.Contains("--skip-old-files", withSkip);

        var without = RemoteCopyCommandBuilder.BuildDirectCommand(req, usePassword: false, useRsync: false, hasPv: false, totalBytes: 0, tarSkipOldFiles: false);
        Assert.DoesNotContain("--skip-old-files", without);
    }

    [Fact]
    public void BuildDirectCommand_rsync_no_overwrite_uses_ignore_existing()
    {
        var req = new RemoteCopyRequest
        {
            Source = Server(),
            SourcePaths = new[] { "/opt/app" },
            Target = Server("10.0.0.6"),
            TargetHost = "10.0.0.6",
            TargetPort = 22,
            TargetDirectory = "/backup",
            Overwrite = false
        };

        var noOverwrite = RemoteCopyCommandBuilder.BuildDirectCommand(req, usePassword: false, useRsync: true, hasPv: false, totalBytes: 0);
        Assert.Contains("--ignore-existing", noOverwrite);
    }

    [Fact]
    public void BuildProbeCommand_reports_capabilities_and_key_status()
    {
        var cmd = RemoteCopyCommandBuilder.BuildProbeCommand("10.0.0.6", 22, "root");
        Assert.Contains("LITSSH_HAS_RSYNC", cmd);
        Assert.Contains("LITSSH_HAS_TAR", cmd);
        Assert.Contains("LITSSH_HAS_SSHPASS", cmd);
        Assert.Contains("LITSSH_KEY_OK", cmd);
        Assert.Contains("root@10.0.0.6", cmd);
    }
}
