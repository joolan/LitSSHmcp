using System.Windows.Input;
using LitSSHmcp.App.Services.Toolbox;
using LitSSHmcp.App.Views;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    private ToolboxViewModel? _toolbox;
    private ToolboxWindow? _toolboxWindow;
    private ToolboxQuickLaunchWindow? _toolboxQuickWindow;
    private ICommand? _navigateToolboxCommand;

    public ToolboxViewModel Toolbox => _toolbox ??= CreateToolbox();

    public ICommand NavigateToolboxCommand =>
        _navigateToolboxCommand ??= new RelayCommand(_ => NavigateToolbox());

    private ToolboxViewModel CreateToolbox()
    {
        var viewModel = new ToolboxViewModel(new ToolboxService(_dialogs), _dialogs);
        viewModel.OpenWindowRequested += ShowToolboxWindow;
        return viewModel;
    }

    public void NavigateToolbox()
    {
        CurrentPage = MainPage.Toolbox;
        _ = Toolbox.LoadAsync();
    }

    public void ShowToolboxWindow()
    {
        if (_toolboxWindow is { IsLoaded: true })
        {
            _toolboxWindow.Activate();
            return;
        }

        _toolboxWindow = _windows.CreateToolboxWindow(Toolbox);
        _toolboxWindow.Closed += (_, _) => _toolboxWindow = null;
        _toolboxWindow.Show();
    }

    public void ShowToolboxQuickLaunch()
    {
        if (_toolboxQuickWindow is { IsLoaded: true })
        {
            _toolboxQuickWindow.Activate();
            return;
        }

        var viewModel = new ToolboxQuickLaunchViewModel(Toolbox.Service, _dialogs);
        _toolboxQuickWindow = _windows.CreateToolboxQuickLaunchWindow(viewModel);
        _toolboxQuickWindow.Closed += (_, _) => _toolboxQuickWindow = null;
        _toolboxQuickWindow.Show();
    }
}
