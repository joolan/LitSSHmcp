using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using LitSSHmcp.Core.Services.Proxy;

namespace LitSSHmcp.App.Services;

public sealed record ProxyFirewallResult(bool Success, string Message, string Commands);

public sealed record ProxyFirewallRuleStatus(
    string Protocol,
    int Port,
    string RuleName,
    bool Exists,
    string? Detail);

/// <summary>
/// Windows 防火墙规则：只放行 private 配置文件的 HTTP / SOCKS5 入站端口。
/// 同时提供可复制命令和 UAC 自动执行入口。
/// </summary>
public sealed class ProxyFirewallService
{
    public string BuildAddCommands(ProxyOptions options)
    {
        var builder = new StringBuilder();
        if (options.HttpEnabled)
            builder.AppendLine(AddCommand("HTTP", options.HttpPort));
        if (options.Socks5Enabled)
            builder.AppendLine(AddCommand("SOCKS5", options.Socks5Port));
        return builder.ToString().TrimEnd();
    }

    public string BuildRemoveCommands(ProxyOptions options)
    {
        var builder = new StringBuilder();
        if (options.HttpEnabled)
            builder.AppendLine(RemoveCommand("HTTP", options.HttpPort));
        if (options.Socks5Enabled)
            builder.AppendLine(RemoveCommand("SOCKS5", options.Socks5Port));
        return builder.ToString().TrimEnd();
    }

    public async Task<ProxyFirewallResult> AddRulesAsync(ProxyOptions options,
        CancellationToken ct = default)
    {
        var commands = BuildAddCommands(options);
        if (string.IsNullOrWhiteSpace(commands))
            return new ProxyFirewallResult(false, "没有启用的代理端口。", commands);
        return await RunElevatedAsync(commands, "已请求添加防火墙规则。", ct)
            .ConfigureAwait(false);
    }

    public async Task<ProxyFirewallResult> RemoveRulesAsync(ProxyOptions options,
        CancellationToken ct = default)
    {
        var commands = BuildRemoveCommands(options);
        if (string.IsNullOrWhiteSpace(commands))
            return new ProxyFirewallResult(false, "没有启用的代理端口。", commands);
        return await RunElevatedAsync(commands, "已请求删除防火墙规则。", ct)
            .ConfigureAwait(false);
    }

    /// <summary>检测规则是否存在；netsh show rule 不需要管理员权限。</summary>
    public async Task<IReadOnlyList<ProxyFirewallRuleStatus>> GetStatusAsync(
        ProxyOptions options, CancellationToken ct = default)
    {
        var result = new List<ProxyFirewallRuleStatus>();
        if (options.HttpEnabled)
            result.Add(await CheckRuleAsync("HTTP", options.HttpPort, ct).ConfigureAwait(false));
        if (options.Socks5Enabled)
            result.Add(await CheckRuleAsync("SOCKS5", options.Socks5Port, ct).ConfigureAwait(false));
        return result;
    }

    private static async Task<ProxyFirewallRuleStatus> CheckRuleAsync(string protocol,
        int port, CancellationToken ct)
    {
        var name = RuleName(protocol, port);
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall show rule name=\"{name}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = NativeShell.Start(info);
            if (process is null)
                return new ProxyFirewallRuleStatus(protocol, port, name, false, "无法启动 netsh");

            var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            if (process.ExitCode == 0)
                return new ProxyFirewallRuleStatus(protocol, port, name, true, "规则存在");
            var detail = string.IsNullOrWhiteSpace(error) ? "规则不存在" : error.Trim();
            return new ProxyFirewallRuleStatus(protocol, port, name, false, detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProxyFirewallRuleStatus(protocol, port, name, false, ex.Message);
        }
    }

    private static string AddCommand(string protocol, int port) =>
        $"netsh advfirewall firewall delete rule name=\"{RuleName(protocol, port)}\" >nul 2>&1 & " +
        $"netsh advfirewall firewall add rule name=\"{RuleName(protocol, port)}\" " +
        $"dir=in action=allow protocol=TCP localport={port} profile=private";

    private static string RemoveCommand(string protocol, int port) =>
        $"netsh advfirewall firewall delete rule name=\"{RuleName(protocol, port)}\"";

    private static string RuleName(string protocol, int port) =>
        $"LitSSH {protocol} Proxy (TCP {port})";

    private static async Task<ProxyFirewallResult> RunElevatedAsync(string commands,
        string successMessage, CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + commands.Replace("\r", string.Empty).Replace("\n", " & "),
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var process = NativeShell.Start(info);
            if (process is null)
                return new ProxyFirewallResult(false, "无法启动提权进程。", commands);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode == 0
                ? new ProxyFirewallResult(true, successMessage, commands)
                : new ProxyFirewallResult(false,
                    $"netsh 返回退出码 {process.ExitCode}，请检查命令或手动执行。", commands);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new ProxyFirewallResult(false, "用户取消了管理员授权。", commands);
        }
        catch (OperationCanceledException)
        {
            return new ProxyFirewallResult(false, "操作已取消。", commands);
        }
        catch (Exception ex)
        {
            return new ProxyFirewallResult(false, "添加防火墙规则失败：" + ex.Message, commands);
        }
    }
}
