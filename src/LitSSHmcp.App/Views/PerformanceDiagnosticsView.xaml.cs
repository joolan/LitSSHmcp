using System.Windows.Controls;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Views;

public partial class PerformanceDiagnosticsView : UserControl
{
    public PerformanceDiagnosticsView()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as PerformanceDiagnosticsViewModel)?.Start();
        Unloaded += (_, _) => (DataContext as PerformanceDiagnosticsViewModel)?.Stop();
    }
}
