using System.Windows;
using LitSSHmcp.App.Services;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class ToolboxWindow : FluentWindow
{
    private readonly ToolboxView _view;

    public ToolboxWindow(ToolboxView view)
    {
        InitializeComponent();
        _view = view;
        DataContext = view.DataContext;
        WindowLayout.Attach(this, "toolbox.window");
        ToolboxHost.Content = view;
        Closed += OnToolboxClosed;
    }

    private void OnToolboxClosed(object? sender, EventArgs e)
    {
        Closed -= OnToolboxClosed;
        ToolboxHost.Content = null;
    }
}
