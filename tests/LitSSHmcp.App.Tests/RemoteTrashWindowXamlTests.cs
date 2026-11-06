using System.Threading;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class RemoteTrashWindowXamlTests
{
    [Fact]
    public void Remote_trash_window_parses_xaml()
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                var server = new SshServerConfig
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "test",
                    Host = "127.0.0.1",
                    Port = 22
                };
                var window = new RemoteTrashWindow(server, new FakeSshService());
                window.Close();
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
