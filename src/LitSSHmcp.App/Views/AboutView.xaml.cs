using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace LitSSHmcp.App.Views;

public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
    }

    private void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // 打开浏览器失败时忽略，不影响界面
        }

        e.Handled = true;
    }
}
