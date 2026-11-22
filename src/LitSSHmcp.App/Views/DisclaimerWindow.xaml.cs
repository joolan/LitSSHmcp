using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using LitSSHmcp.App.Services;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class DisclaimerWindow : Window
{
    public DisclaimerWindow()
    {
        InitializeComponent();
    }

    public bool Accepted { get; private set; }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        if (SkipMonthCheck.IsChecked == true)
        {
            UiPrefs.SetString(DisclaimerPolicy.SkipMonthKey,
                DisclaimerPolicy.MonthKey(DateTime.Now));
        }
        Accepted = true;
        Close();
    }

    private void OnDecline(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        Close();
    }

    private void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            NativeShell.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // 打开浏览器失败不影响免责声明
        }
        e.Handled = true;
    }
}
