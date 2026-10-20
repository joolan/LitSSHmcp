using System.IO;
using System.Text;
using LitSSHmcp.App.Services;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class SessionLogReaderTests
{
    [Fact]
    public void Reads_log_while_recorder_keeps_write_handle()
    {
        var path = Path.Combine(Path.GetTempPath(), "litssh-log-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using var writer = new StreamWriter(
                new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(true))
            {
                AutoFlush = true
            };
            writer.WriteLine("line-1");
            writer.WriteLine("line-2");

            var text = SessionLogReader.ReadAllText(path);
            var lines = SessionLogReader.ReadAllLines(path);

            Assert.Contains("line-1", text);
            Assert.Equal(new[] { "line-1", "line-2" }, lines);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
