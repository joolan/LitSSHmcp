using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using LitSSHmcp.Core.Services.DnsServer;

namespace LitSSHmcp.App.Services;

public sealed record DnsFirewallResult(bool Success, string Message, string Commands);

public sealed record DnsFirewallRuleStatus(
    string Protocol,
    int Port,
    string RuleName,
    bool Exists,
    string? Detail);

/// <summary>
/// Windows 防火墙规则：只放行 private 配置文件的 DNS UDP/TCP 入站端口。
/// 同时提供可复制命令和 UAC 自动执行入口。
/// </summary>
public sealed class DnsFirewallService
{
    public string BuildAddCommands(DnsOptions options)
    {
        var builder = new StringBuilder();
        if (options.UdpEnabled)
            builder.AppendLine(AddCommand("UDP", options.Port));
        if (options.TcpEnabled)
            builder.AppendLine(AddCommand("TCP", options.Port));
        return builder.ToString().TrimEnd();
    }

    public string BuildRemoveCommands(DnsOptions options)
    {
        var builder = new StringBuilder();
        if (options.UdpEnabled)
            builder.AppendLine(RemoveCommand("UDP", options.Port));
        if (options.TcpEnabled)
            builder.AppendLine(RemoveCommand("TCP", options.Port));
        return builder.ToString().TrimEnd();
    }

    public async Task<DnsFirewallResult> AddRulesAsync(DnsOptions options,
        CancellationToken ct = default)
    {
        var commands = BuildAddCommands(options);
        if (string.IsNullOrWhiteSpace(commands))
            return new DnsFirewallResult(false, "没有启用的 DNS 协议。", commands);
        return await RunElevatedAsync(commands, "已请求添加防火墙规则。", ct)
            .ConfigureAwait(false);
    }

    public async Task<DnsFirewallResult> RemoveRulesAsync(DnsOptions options,
        CancellationToken ct = default)
    {
        var commands = BuildRemoveCommands(options);
        if (string.IsNullOrWhiteSpace(commands))
            return new DnsFirewallResult(false, "没有启用的 DNS 协议。", commands);
        return await RunElevatedAsync(commands, "已请求删除防火墙规则。", ct)
            .ConfigureAwait(false);
    }

    /// <summary>检测规则是否存在；netsh show rule 不需要管理员权限。</summary>
    public async Task<IReadOnlyList<DnsFirewallRuleStatus>> GetStatusAsync(
        DnsOptions options, CancellationToken ct = default)
    {
        var result = new List<DnsFirewallRuleStatus>();
        if (options.UdpEnabled)
            result.Add(await CheckRuleAsync("UDP", options.Port, ct).ConfigureAwait(false));
        if (options.TcpEnabled)
            result.Add(await CheckRuleAsync("TCP", options.Port, ct).ConfigureAwait(false));
        return result;
    }

    private static async Task<DnsFirewallRuleStatus> CheckRuleAsync(string protocol,
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
                return new DnsFirewallRuleStatus(protocol, port, name, false, "无法启动 netsh");

            var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            if (process.ExitCode == 0)
                return new DnsFirewallRuleStatus(protocol, port, name, true, "规则存在");
            var detail = string.IsNullOrWhiteSpace(error) ? "规则不存在" : error.Trim();
            return new DnsFirewallRuleStatus(protocol, port, name, false, detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DnsFirewallRuleStatus(protocol, port, name, false, ex.Message);
        }
    }

    private static string AddCommand(string protocol, int port) =>
        $"netsh advfirewall firewall delete rule name=\"{RuleName(protocol, port)}\" >nul 2>&1 & " +
        $"netsh advfirewall firewall add rule name=\"{RuleName(protocol, port)}\" " +
        $"dir=in action=allow protocol={protocol} localport={port} profile=private";

    private static string RemoveCommand(string protocol, int port) =>
        $"netsh advfirewall firewall delete rule name=\"{RuleName(protocol, port)}\"";

    private static string RuleName(string protocol, int port) =>
        $"LitSSH DNS ({protocol} {port})";

    private static async Task<DnsFirewallResult> RunElevatedAsync(string commands,
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
                return new DnsFirewallResult(false, "无法启动提权进程。", commands);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode == 0
                ? new DnsFirewallResult(true, successMessage, commands)
                : new DnsFirewallResult(false,
                    $"netsh 返回退出码 {process.ExitCode}，请检查命令或手动执行。", commands);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new DnsFirewallResult(false, "用户取消了管理员授权。", commands);
        }
        catch (OperationCanceledException)
        {
            return new DnsFirewallResult(false, "操作已取消。", commands);
        }
        catch (Exception ex)
        {
            return new DnsFirewallResult(false, "添加防火墙规则失败：" + ex.Message, commands);
        }
    }
}
