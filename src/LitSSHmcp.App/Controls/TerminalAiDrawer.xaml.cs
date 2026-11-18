using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Controls;

public partial class TerminalAiDrawer : UserControl
{
    private TerminalAiViewModel? _viewModel;
    private readonly HashSet<TerminalAiTurn> _hookedTurns = new();
    private bool _scrollPending;

    public TerminalAiDrawer()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook();
        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => Detach();
    }

    private void Hook()
    {
        Detach();
        if (DataContext is not TerminalAiViewModel vm)
            return;

        _viewModel = vm;
        vm.Turns.CollectionChanged += OnTurnsChanged;
        foreach (var turn in vm.Turns)
            HookTurn(turn);
    }

    private void Detach()
    {
        if (_viewModel is not null)
            _viewModel.Turns.CollectionChanged -= OnTurnsChanged;
        foreach (var turn in _hookedTurns)
            turn.PropertyChanged -= OnTurnPropertyChanged;
        _hookedTurns.Clear();
        _viewModel = null;
    }

    private void OnTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (TerminalAiTurn turn in e.OldItems)
                UnhookTurn(turn);
        if (e.NewItems is not null)
            foreach (TerminalAiTurn turn in e.NewItems)
                HookTurn(turn);
        ScheduleScroll();
    }

    private void HookTurn(TerminalAiTurn turn)
    {
        if (_hookedTurns.Add(turn))
            turn.PropertyChanged += OnTurnPropertyChanged;
    }

    private void UnhookTurn(TerminalAiTurn turn)
    {
        if (_hookedTurns.Remove(turn))
            turn.PropertyChanged -= OnTurnPropertyChanged;
    }

    private void OnTurnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TerminalAiTurn.AssistantText)
            or nameof(TerminalAiTurn.Note)
            or nameof(TerminalAiTurn.IsRunning)
            or nameof(TerminalAiTurn.IsStepsExpanded)
            or nameof(TerminalAiTurn.StepsHeader)
            or nameof(TerminalAiTurn.StepsVisibility))
            ScheduleScroll();
    }

    private void ScheduleScroll()
    {
        if (_scrollPending)
            return;
        _scrollPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _scrollPending = false;
            ChatScroll.ScrollToEnd();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    public void FocusInput()
    {
        InputBox.Focus();
        Keyboard.Focus(InputBox);
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        if (_viewModel?.SendOrStopCommand.CanExecute(null) == true)
            _viewModel.SendOrStopCommand.Execute(null);
        e.Handled = true;
    }
}
