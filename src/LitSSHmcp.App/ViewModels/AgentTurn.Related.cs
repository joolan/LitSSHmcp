using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace LitSSHmcp.App.ViewModels;

public partial class AgentTurn
{
    public ObservableCollection<AgentRelatedTarget> RelatedServers { get; } = new();
    public ObservableCollection<AgentRelatedTarget> RelatedDataSources { get; } = new();
    public ObservableCollection<AgentRelatedTarget> RelatedDocuments { get; } = new();

    public bool HasRelatedServers => RelatedServers.Count > 0;
    public bool HasRelatedDataSources => RelatedDataSources.Count > 0;
    public bool HasRelatedDocuments => RelatedDocuments.Count > 0;
    public bool HasRelatedTargets => HasRelatedServers || HasRelatedDataSources || HasRelatedDocuments;

    private void InitializeRelatedTargets()
    {
        RelatedServers.CollectionChanged += OnRelatedChanged;
        RelatedDataSources.CollectionChanged += OnRelatedChanged;
        RelatedDocuments.CollectionChanged += OnRelatedChanged;
    }

    private void OnRelatedChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasRelatedServers));
        OnPropertyChanged(nameof(HasRelatedDataSources));
        OnPropertyChanged(nameof(HasRelatedDocuments));
        OnPropertyChanged(nameof(HasRelatedTargets));
    }

    public void ClearRelatedTargets()
    {
        RelatedServers.Clear();
        RelatedDataSources.Clear();
        RelatedDocuments.Clear();
    }

    public void UpsertRelated(AgentRelatedTarget target)
    {
        var collection = target.Kind switch
        {
            AgentRelatedTargetKind.Server => RelatedServers,
            AgentRelatedTargetKind.DataSource => RelatedDataSources,
            _ => RelatedDocuments
        };

        var existing = collection.FirstOrDefault(item =>
            string.Equals(item.Id, target.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            collection.Add(target);
            return;
        }

        existing.Calls++;
    }
}
