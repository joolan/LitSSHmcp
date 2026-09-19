using System.Windows;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}