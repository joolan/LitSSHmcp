using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Controls;

public partial class DatasourceAiDrawer : UserControl
{
    private DatasourceAiViewModel? _viewModel;

    public event Action? CloseRequested;

    public DatasourceAiDrawer()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        if (e.NewValue is DatasourceAiViewModel vm)
        {
            _viewModel = vm;
            vm.Turns.CollectionChanged += OnTurnsChanged;
            foreach (var turn in vm.Turns)
                turn.PropertyChanged += OnTurnPropertyChanged;
        }
    }

    private void Detach()
    {
        if (_viewModel is not null)
        {
            _viewModel.Turns.CollectionChanged -= OnTurnsChanged;
            foreach (var turn in _viewModel.Turns)
                turn.PropertyChanged -= OnTurnPropertyChanged;
        }
        _viewModel = null;
    }

    private void OnTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<DatasourceAiTurn>())
                item.PropertyChanged -= OnTurnPropertyChanged;
        }
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<DatasourceAiTurn>())
                item.PropertyChanged += OnTurnPropertyChanged;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ScrollToEnd));
    }

    private void OnTurnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DatasourceAiTurn.AssistantText)
            or nameof(DatasourceAiTurn.Note)
            or nameof(DatasourceAiTurn.IsRunning))
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ScrollToEnd));
    }

    private void ScrollToEnd()
    {
        try { ChatScroll.ScrollToEnd(); } catch { /* ignore */ }
    }

    private void OnClose(object sender, RoutedEventArgs e)
        => CloseRequested?.Invoke();

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            return;
        e.Handled = true;
        if (DataContext is DatasourceAiViewModel vm)
            vm.SendOrStopCommand.Execute(null);
    }

    public void FocusComposer()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            try { InputBox.Focus(); } catch { /* ignore */ }
        }));
    }
}
