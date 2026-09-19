using System.Diagnostics;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Renci.SshNet;

namespace LitSSHmcp.Cli;

class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        var command = args[0].ToLower();

        switch (command)
        {
            case "list":
                return await ListServers();

            case "connect":
                if (args.Length < 2)
                {
                    Console.WriteLine("错误: 请指定服务器名称或ID");
                    Console.WriteLine("用法: litssh connect <服务器名称>");
                    return 1;
                }
                return await ConnectToServer(args[1]);

            case "run":
                if (args.Length < 3)
                {
                    Console.WriteLine("错误: 请指定服务器和命令");
                    Console.WriteLine("用法: litssh run <服务器名称> <命令>");
                    return 1;
                }
                return await RunCommand(args[1], string.Join(" ", args.Skip(2)));

            default:
                Console.WriteLine($"未知命令: {command}");
                PrintUsage();
                return 1;
        }
    }

    static void PrintUsage()
    {
        Console.WriteLine(@"LitSSH CLI - SSH连接命令行工具

用法:
  litssh list                    列出所有已配置的服务器
  litssh connect <服务器名称>     连接到服务器(交互式)
  litssh run <服务器名称> <命令>  在服务器上执行命令

示例:
  litssh list
  litssh connect web-server
  litssh run web-server ""df -h""
  litssh run my-server ""docker ps""");
    }

    static async Task<int> ListServers()
    {
        var configService = new ConfigService();
        var config = await configService.LoadConfigAsync();

        if (config.Servers.Length == 0)
        {
            Console.WriteLine("没有配置的服务器");
            Console.WriteLine($"配置文件位置: {configService.GetConfigPath()}");
            return 0;
        }

        Console.WriteLine("已配置的服务器:");
        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"{"名称",-20} {"主机",-25} {"端口",-6} {"用户名",-10}");
        Console.WriteLine(new string('-', 60));

        foreach (var server in config.Servers)
        {
            Console.WriteLine($"{server.Name,-20} {server.Host,-25} {server.Port,-6} {server.Username,-10}");
        }

        return 0;
    }

    static async Task<int> ConnectToServer(string serverName)
    {
        var configService = new ConfigService();
        var config = await configService.LoadConfigAsync();

        var server = config.Servers.FirstOrDefault(s =>
            s.Name.Equals(serverName, StringComparison.OrdinalIgnoreCase) ||
            s.Id.Equals(serverName, StringComparison.OrdinalIgnoreCase));

        if (server == null)
        {
            Console.WriteLine($"未找到服务器: {serverName}");
            return 1;
        }

        Console.WriteLine($"正在连接到 {server.Name} ({server.Host}:{server.Port})...");

        try
        {
            using var client = CreateSshClient(server);
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(10);
            client.Connect();

            Console.WriteLine("连接成功!");
            Console.WriteLine("输入命令执行，输入 'exit' 退出");
            Console.WriteLine(new string('-', 40));

            using var shellStream = client.CreateShellStream("xterm-256color", 80, 24, 800, 600, 1024);

            var inputTask = Task.Run(() =>
            {
                while (true)
                {
                    var line = Console.ReadLine();
                    if (line?.ToLower() == "exit")
                    {
                        shellStream.Close();
                        break;
                    }
                    if (line != null)
                        shellStream.WriteLine(line);
                }
            });

            shellStream.DataReceived += (sender, e) =>
            {
                Console.Write(System.Text.Encoding.UTF8.GetString(e.Data));
            };

            while (shellStream.Length > 0 || client.IsConnected)
            {
                await Task.Delay(100);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"连接失败: {ex.Message}");
            return 1;
        }
    }

    static async Task<int> RunCommand(string serverName, string command)
    {
        var configService = new ConfigService();
        var config = await configService.LoadConfigAsync();

        var server = config.Servers.FirstOrDefault(s =>
            s.Name.Equals(serverName, StringComparison.OrdinalIgnoreCase) ||
            s.Id.Equals(serverName, StringComparison.OrdinalIgnoreCase));

        if (server == null)
        {
            Console.WriteLine($"未找到服务器: {serverName}");
            return 1;
        }

        Console.WriteLine($"在 {server.Name} 上执行: {command}");

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = CreateSshClient(server);
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(10);
            client.Connect();

            using var cmd = client.CreateCommand(command);
            cmd.CommandTimeout = TimeSpan.FromSeconds(60);
            var result = cmd.Execute();
            sw.Stop();

            if (!string.IsNullOrEmpty(result))
                Console.Write(result);

            if (!string.IsNullOrEmpty(cmd.Error))
                Console.Error.Write(cmd.Error);

            return cmd.ExitStatus ?? 0;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"执行失败: {ex.Message}");
            return 1;
        }
    }

    static SshClient CreateSshClient(SshServerConfig server)
    {
        if (server.AuthType == AuthType.KeyFile && !string.IsNullOrEmpty(server.KeyFilePath))
        {
            var keyFile = string.IsNullOrEmpty(server.KeyFilePassphrase)
                ? new PrivateKeyFile(server.KeyFilePath)
                : new PrivateKeyFile(server.KeyFilePath, server.KeyFilePassphrase);
            return new SshClient(server.Host, server.Port, server.Username, keyFile);
        }

        return new SshClient(server.Host, server.Port, server.Username, server.Password ?? string.Empty);
    }
}