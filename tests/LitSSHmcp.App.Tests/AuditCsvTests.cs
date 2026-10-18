using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>审计 CSV 导出的「用户」列（UserId 归因；空=local）。</summary>
public class AuditCsvTests
{
    [Fact]
    public void Command_csv_includes_user_column_with_local_fallback()
    {
        var vm = new AuditViewModel();
        vm.CommandLogs.Add(new CommandAuditLog
        {
            ServerId = "s", ServerName = "srv", Command = "ls", UserId = "alice", Status = CommandStatus.Executed
        });
        vm.CommandLogs.Add(new CommandAuditLog
        {
            ServerId = "s", ServerName = "srv", Command = "pwd", UserId = null, Status = CommandStatus.Executed
        });

        var csv = vm.BuildCommandsCsv();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Contains("用户", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Contains("alice", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains(",local,", StringComparison.Ordinal));
    }

    [Fact]
    public void User_display_prefers_resolved_username_over_id()
    {
        var log = new CommandAuditLog
        {
            ServerId = "s", ServerName = "srv", Command = "ls", UserId = "1", UserName = "admin",
            Status = CommandStatus.Executed
        };

        Assert.Equal("admin", log.UserDisplay);

        var vm = new AuditViewModel();
        vm.CommandLogs.Add(log);
        Assert.Contains(",admin,", vm.BuildCommandsCsv(), StringComparison.Ordinal);
        Assert.DoesNotContain(",1,", vm.BuildCommandsCsv().Split('\n')[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_csv_includes_user_column_with_local_fallback()
    {
        var vm = new AuditViewModel();
        vm.SqlLogs.Add(new SqlAuditLog
        {
            DataSourceId = "d", DataSourceName = "db", Operation = SqlOperation.Query, Sql = "select 1",
            UserId = "1", Status = CommandStatus.Executed
        });
        vm.SqlLogs.Add(new SqlAuditLog
        {
            DataSourceId = "d", DataSourceName = "db", Operation = SqlOperation.Query, Sql = "select 2",
            UserId = "   ", Status = CommandStatus.Executed
        });

        var csv = vm.BuildSqlCsv();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Contains("用户", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Contains(",1,", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains(",local,", StringComparison.Ordinal));
    }
}
