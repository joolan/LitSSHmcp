using System.IO;
using System.Threading;
using LitSSHmcp.App.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.TerminalAi;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class TerminalAiTests
{
    [Fact]
    public void Payload_contains_split_marker_and_exit_capture()
    {
        var payload = TerminalCommandRunner.BuildPayload(
            new SshServerConfig { Username = "ops", SudoType = SudoType.None },
            "uptime", sudo: false, token: "LITSSH_AI_TEST", disableEcho: false, password: string.Empty);

        Assert.Contains("uptime", payload);
        Assert.Contains("[AI] %s", payload);
        Assert.Contains("__litssh_ec=$?", payload);
        Assert.Contains("__litssh_m='<<<'\"LITSSH_AI_TEST\"'>>>'", payload);
        Assert.DoesNotContain("<<<LITSSH_AI_TEST>>>", payload);
    }

    [Fact]
    public void Sudo_payload_disables_echo_and_uses_prompt()
    {
        var server = new SshServerConfig
        {
            Username = "ops",
            SudoType = SudoType.CurrentUser,
            SudoPassword = "secret"
        };
        var payload = TerminalCommandRunner.BuildPayload(
            server, "systemctl restart nginx", sudo: true,
            token: "LITSSH_AI_TEST", disableEcho: true, password: "secret");

        Assert.Contains("stty echo", payload);
        Assert.Contains("__litssh_sudo_prompt=$(printf '\\033]0;LITSSH_SUDO_PASSWORD:\\007')", payload);
        Assert.Contains("sudo -S -p \"$__litssh_sudo_prompt\"", payload);
        Assert.DoesNotContain("secret", payload);
        Assert.Contains("LITSSH_EO_TEST", TerminalCommandRunner.BuildEchoOffReady("LITSSH_EO_TEST"));
    }

    [Fact]
    public void Manual_sudo_payload_uses_visible_prompt_and_waits_for_user()
    {
        var server = new SshServerConfig
        {
            Username = "ops",
            SudoType = SudoType.CurrentUser,
            SudoPassword = "secret"
        };
        var payload = TerminalCommandRunner.BuildPayload(
            server, "iptables -S", sudo: true, token: "LITSSH_AI_TEST",
            disableEcho: true, password: "secret", manualPassword: true);

        Assert.Contains("sudo 密码", payload);
        Assert.Contains("sudo -S -p 'Password: '", payload);
        Assert.DoesNotContain("$__litssh_sudo_prompt", payload);
        Assert.DoesNotContain("secret", payload);
    }

    [Fact]
    public void Pty_input_does_not_add_an_extra_empty_line()
    {
        Assert.Equal("id\r", TerminalCommandRunner.ToPtyInput("id\n"));
        Assert.Equal("id\r", TerminalCommandRunner.ToPtyInput("id"));
        Assert.DoesNotContain("\r\r", TerminalCommandRunner.ToPtyInput("id\n"));
    }

    [Fact]
    public void Sudo_marker_is_on_same_line_to_avoid_being_consumed_as_password()
    {
        var server = new SshServerConfig
        {
            Username = "ops",
            SudoType = SudoType.CurrentUser,
            SudoPassword = "secret"
        };
        var payload = TerminalCommandRunner.BuildPayload(
            server, "id", sudo: true, token: "LITSSH_AI_TEST",
            disableEcho: true, password: "secret", manualPassword: true);
        var sudoLine = payload.Split('\n').First(line => line.Contains("sudo -S", StringComparison.Ordinal));

        Assert.Contains("__litssh_ec=$?", sudoLine);
        Assert.Contains("__litssh_m=", sudoLine);
        Assert.Contains("printf '\\n%s|%s|%s\\n", sudoLine);
        Assert.Contains("stty echo", sudoLine);
    }

    [Fact]
    public void Manual_command_wraps_raw_command_with_configured_escalation()
    {
        var sudoUser = new SshServerConfig { Username = "ops", SudoType = SudoType.CurrentUser };
        Assert.Equal("sudo /bin/sh -c 'id'", TerminalCommandRunner.BuildManualSudoCommand(sudoUser, "id"));

        var rootUser = new SshServerConfig { Username = "ops", SudoType = SudoType.RootUser };
        Assert.Equal("su - root -c 'id'", TerminalCommandRunner.BuildManualSudoCommand(rootUser, "id"));
    }

    [Fact]
    public void Marker_parse_returns_exit_code_and_cwd()
    {
        const string marker = "<<<LITSSH_AI_TEST>>>";
        var text = "some output\r\n" + marker + "|2|/var/log/nginx\r\n$ ";
        var index = text.LastIndexOf(marker, StringComparison.Ordinal);

        var (exitCode, cwd) = TerminalCommandRunner.ParseMarker(text, index + marker.Length);

        Assert.Equal(2, exitCode);
        Assert.Equal("/var/log/nginx", cwd);
    }

    [Fact]
    public void Wrapper_lines_are_removed_from_output()
    {
        const string output =
            "stty -echo 2>/dev/null || true\n" +
            "hello\n" +
            "__litssh_ec=$?\n" +
            "stty echo 2>/dev/null || true\n" +
            "__litssh_m='<<<'\"" + "LITSSH_AI_TEST" + "\"'>>>'\n";

        var cleaned = TerminalCommandRunner.StripWrapper(output, includeStty: true);

        Assert.Equal("hello", cleaned);
    }

    [Fact]
    public async Task Fill_terminal_command_raises_event_for_the_view()
    {
        var terminal = new TerminalSessionViewModel(
            new SshServerConfig { Id = "s1", Name = "web-01", Host = "127.0.0.1" },
            new FakeSshService());
        var vm = new TerminalAiViewModel(
            terminal,
            new ConfigService(),
            new LitSSHmcp.Core.Services.Storage.AuditLogService(),
            (_, _, _, _, _) => Task.FromResult(new TerminalAiApprovalResult(true, false)));
        string? filled = null;
        vm.FillTerminalRequested += command => filled = command;

        vm.FillTerminalCommand.Execute("id && hostname");

        Assert.Equal("id && hostname", filled);
        await vm.DisposeAsync();
    }

    [Fact]
    public void Session_log_keeps_ai_command_and_drops_internal_wrapper_lines()
    {
        var root = Path.Combine(Path.GetTempPath(), "litssh-ai-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var logger = new SessionLogger("web-01", "192.168.1.4", logRoot: root);
            logger.WriteOutput(
                "ranjuan@10:~$ stty -echo 2>/dev/null || true\r\n" +
                "[AI] sed -i 's/x/y/' ~/test.txt\r\n" +
                "<<<LITSSH_AI_0123456789abcdef0123456789abcdef>>>|0|/home/ranjuan\r\n" +
                "done\r\n");
            var path = logger.FilePath;
            logger.Dispose();

            var log = File.ReadAllText(path);
            Assert.Contains("[AI] sed -i", log);
            Assert.Contains("done", log);
            Assert.DoesNotContain("stty -echo", log);
            Assert.DoesNotContain("<<<LITSSH_AI_", log);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Terminal_ai_windows_parse_xaml()
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                var approval = new TerminalAiCommandApprovalWindow(
                    "web-01", "systemctl status nginx", "/var/log", sudo: true,
                    LitSSHmcp.Core.Services.Security.CommandFilterResult.Sensitive,
                    hasConfiguredPassword: true);
                approval.Close();
                var drawer = new TerminalAiDrawer();
                _ = drawer;
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(captured);
    }
}
