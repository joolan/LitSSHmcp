using System.IO;
using LitSSHmcp.Web;
using Xunit;

namespace LitSSHmcp.Web.Tests;

public class WebAccessLogTests
{
    [Fact]
    public void Write_creates_daily_file_and_sanitizes_delimiters()
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-web-access-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new WebAccessLog(dir);

            log.Write("session|stop", "alice", "127.0.0.1", "line1\r\nline2");

            var file = Assert.Single(Directory.GetFiles(dir, "web-access-*.log"));
            var text = File.ReadAllText(file);
            Assert.Contains("|session/stop|alice|127.0.0.1|line1  line2", text);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }
}
