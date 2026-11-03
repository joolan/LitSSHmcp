using System.Windows;
using LitSSHmcp.App.Views;

namespace LitSSHmcp.App.Services;

public sealed partial class WpfWindowService
{
    public KubernetesConnectionWindow CreateKubernetesConnectionWindow(Window? owner = null)
    {
        var window = new KubernetesConnectionWindow();
        if (owner is not null)
            window.Owner = owner;
        return window;
    }
}
