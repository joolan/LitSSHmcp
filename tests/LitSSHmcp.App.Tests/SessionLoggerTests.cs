using System.IO;
using LitSSHmcp.App.Services;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class SessionLoggerTests
{
    [Fact]
    public void Terminal_log_and_cast_redact_secrets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "litssh-sessionlog-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var logger = new SessionLogger("server", "localhost", logRoot: dir);
            logger.WriteOutput("password=hunter2\n");
            logger.WriteInput("token=abcdef123456\r");
            logger.Dispose();

            var log = File.ReadAllText(logger.FilePath);
            var cast = File.ReadAllText(logger.CastPath);

            Assert.DoesNotContain("hunter2", log, StringComparison.Ordinal);
            Assert.DoesNotContain("abcdef123456", cast, StringComparison.Ordinal);
            Assert.Contains("******", log, StringComparison.Ordinal);
            Assert.Contains("******", cast, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }
}
