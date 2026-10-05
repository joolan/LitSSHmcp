using System.Windows;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Views;

public partial class TopologyManageWindow : Wpf.Ui.Controls.FluentWindow
{
    public TopologyManageWindow()
    {
        InitializeComponent();
        DataContext = new TopologyManageViewModel(new ConfigService());
    }
}
