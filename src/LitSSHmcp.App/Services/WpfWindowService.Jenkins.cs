using System.Windows;
using LitSSHmcp.App.Views;

namespace LitSSHmcp.App.Services;

public sealed partial class WpfWindowService
{
    public JenkinsConnectionWindow CreateJenkinsConnectionWindow(Window? owner = null)
    {
        var window = new JenkinsConnectionWindow();
        if (owner is not null)
            window.Owner = owner;
        return window;
    }
}
