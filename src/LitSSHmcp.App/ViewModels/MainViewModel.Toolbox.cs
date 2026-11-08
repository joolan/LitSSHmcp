using System.Windows.Input;
using LitSSHmcp.App.Services.Toolbox;
using LitSSHmcp.App.Views;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    private ToolboxViewModel? _toolbox;
    private ToolboxView? _toolboxView;
    private ToolboxWindow? _toolboxWindow;
    private ToolboxQuickLaunchWindow? _toolboxQuickWindow;
    private bool _toolboxDetached;
    private ICommand? _navigateToolboxCommand;
    private RelayCommand? _activateToolboxCommand;
    private RelayCommand? _dockToolboxCommand;

    public ToolboxViewModel Toolbox => _toolbox ??= CreateToolbox();

    public ToolboxView? ToolboxPageView => _toolboxDetached ? null : _toolboxView;

    public bool IsToolboxDetached => _toolboxDetached;

    public ICommand NavigateToolboxCommand =>
        _navigateToolboxCommand ??= new RelayCommand(_ => NavigateToolbox());

    public ICommand ActivateToolboxCommand =>
        _activateToolboxCommand ??= new RelayCommand(_ => ActivateToolbox(), _ => _toolboxDetached);

    public ICommand DockToolboxCommand =>
        _dockToolboxCommand ??= new RelayCommand(_ => DockToolbox(), _ => _toolboxDetached);

    private ToolboxViewModel CreateToolbox()
    {
        var viewModel = new ToolboxViewModel(new ToolboxService(_dialogs), _dialogs);
        viewModel.OpenWindowRequested += ShowToolboxWindow;
        return viewModel;
    }

    private ToolboxView EnsureToolboxView()
    {
        if (_toolboxView is null)
        {
            _toolboxView = new ToolboxView { DataContext = Toolbox };
            RaiseToolboxPageStateChanged();
        }
        return _toolboxView;
    }

    public void NavigateToolbox()
    {
        CurrentPage = MainPage.Toolbox;
        if (_toolboxDetached)
        {
            ActivateToolbox();
            return;
        }

        EnsureToolboxView();
        _ = Toolbox.LoadAsync();
    }

    public void FocusToolboxSearch() => _toolboxView?.FocusSearch();

    public void ShowToolboxWindow()
    {
        if (_toolboxWindow is { IsLoaded: true })
        {
            _toolboxWindow.Activate();
            return;
        }

        var view = EnsureToolboxView();
        _toolboxDetached = true;
        RaiseToolboxPageStateChanged();

        var window = _windows.CreateToolboxWindow(view);
        _toolboxWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_toolboxWindow, window))
                _toolboxWindow = null;
            if (_toolboxDetached)
            {
                _toolboxDetached = false;
                RaiseToolboxPageStateChanged();
            }
        };
        window.Show();
    }

    private void ActivateToolbox()
    {
        if (_toolboxWindow is { IsLoaded: true })
        {
            _toolboxWindow.Activate();
            _toolboxWindow.Focus();
            return;
        }

        if (_toolboxDetached)
            DockToolbox();
    }

    private void DockToolbox()
    {
        if (!_toolboxDetached)
            return;
        if (_toolboxWindow is { IsLoaded: true })
        {
            _toolboxWindow.Close();
            return;
        }

        _toolboxWindow = null;
        _toolboxDetached = false;
        RaiseToolboxPageStateChanged();
    }

    public void DisposeToolboxPage()
    {
        var window = _toolboxWindow;
        _toolboxWindow = null;
        _toolboxDetached = false;
        _toolboxView = null;
        if (window is not null)
        {
            try { window.Close(); } catch { /* ignore */ }
        }
    }

    private void RaiseToolboxPageStateChanged()
    {
        OnPropertyChanged(nameof(ToolboxPageView));
        OnPropertyChanged(nameof(IsToolboxDetached));
        _activateToolboxCommand?.RaiseCanExecuteChanged();
        _dockToolboxCommand?.RaiseCanExecuteChanged();
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
