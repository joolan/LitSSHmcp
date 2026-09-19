using System.Windows;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.McpServer.Services;
using LitSSHmcp.McpServer.Tools;
using ModelContextProtocol.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<IConfigService, ConfigService>();
builder.Services.AddSingleton<ISshService, SshService>();
builder.Services.AddSingleton<IAuditLogService, AuditLogService>();
builder.Services.AddSingleton<IApprovalService, DesktopApprovalService>();

builder.Services.AddSingleton<ICommandFilterService>(sp =>
{
    var configService = sp.GetRequiredService<IConfigService>();
    var config = configService.LoadConfigAsync().GetAwaiter().GetResult();
    return new CommandFilterService(config.Security.CommandFilter);
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ServerTools>()
    .WithTools<CommandTools>()
    .WithTools<FileTransferTools>()
    .WithTools<SudoTools>()
    .WithTools<UsageGuideTools>();

builder.Logging.AddConsole(consoleLog =>
{
    consoleLog.LogToStandardErrorThreshold = LogLevel.Trace;
});

var host = builder.Build();

var auditLog = host.Services.GetRequiredService<IAuditLogService>();
await auditLog.InitializeAsync();

await host.RunAsync();