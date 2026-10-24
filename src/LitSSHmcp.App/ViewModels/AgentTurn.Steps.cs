using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace LitSSHmcp.App.ViewModels;

public partial class AgentTurn
{
    public ObservableCollection<LocalApprovalRequest> Approvals { get; } = new();

    private readonly HashSet<AgentStep> _hookedSteps = new();

    private void OnStepsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (AgentStep step in e.OldItems)
                UnhookStep(step);
        if (e.NewItems is not null)
            foreach (AgentStep step in e.NewItems)
                HookStep(step);

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var step in _hookedSteps.ToList())
                if (!Steps.Contains(step))
                    UnhookStep(step);
            foreach (var step in Steps)
                HookStep(step);
        }

        RaiseToolState();
    }

    private void HookStep(AgentStep step)
    {
        if (_hookedSteps.Add(step))
            step.PropertyChanged += OnStepPropertyChanged;
    }

    private void UnhookStep(AgentStep step)
    {
        if (_hookedSteps.Remove(step))
            step.PropertyChanged -= OnStepPropertyChanged;
    }

    private void OnStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AgentStep.Success)
            or nameof(AgentStep.IsRunning)
            or nameof(AgentStep.DurationMs)))
            return;

        RaiseToolState();
        if (sender is AgentStep { IsRunning: false, Success: false })
            ShowTools = true;
    }

    private void RaiseToolState()
    {
        OnPropertyChanged(nameof(HasSteps));
        OnPropertyChanged(nameof(ToolsVisibility));
        OnPropertyChanged(nameof(ToolHeader));
        OnPropertyChanged(nameof(HasFailedTools));
        OnPropertyChanged(nameof(HasDestructiveTools));
    }
}
