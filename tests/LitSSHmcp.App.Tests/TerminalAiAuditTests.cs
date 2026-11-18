using System.IO;
using LitSSHmcp.App.Services.TerminalAi;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class TerminalAiAuditTests
{
    [Fact]
    public async Task Tool_prefix_filter_returns_only_terminal_ai_records()
    {
        var root = Path.Combine(Path.GetTempPath(), "litssh-ai-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var audit = new AuditLogService(null, Path.Combine(root, "audit.db"));
            await audit.InitializeAsync();
            await audit.LogCommandAsync(new CommandAuditLog
            {
                ServerId = "s1",
                ServerName = "web-01",
                Command = "df -h",
                Status = CommandStatus.Executed,
                Category = AuditCategory.Exec,
                Tool = TerminalAiTool.ExecAuditToolName
            });
            await audit.LogCommandAsync(new CommandAuditLog
            {
                ServerId = "s1",
                ServerName = "web-01",
                Command = "ls",
                Status = CommandStatus.Executed,
                Category = AuditCategory.Exec,
                Tool = "ssh_execute_command"
            });

            var ai = await audit.GetLogsAsync(toolPrefix: "terminal_ai_");
            var all = await audit.GetLogsAsync();

            var item = Assert.Single(ai);
            Assert.Equal("df -h", item.Command);
            Assert.Equal(2, all.Length);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}
