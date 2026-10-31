using System.Windows;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class BatchProbeSelectionWindow : FluentWindow
{
    public BatchProbeSelectionWindow(BatchProbeSelectionViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
    }

    public BatchProbeSelectionViewModel ViewModel { get; }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.EnsureSelection())
            return;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
