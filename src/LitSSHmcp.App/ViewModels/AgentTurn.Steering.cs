using System.Collections.ObjectModel;

namespace LitSSHmcp.App.ViewModels;

public sealed class AgentSteeringNote
{
    public AgentSteeringNote(string text)
    {
        Text = text;
        Time = DateTime.Now.ToString("HH:mm:ss");
    }

    public string Text { get; }
    public string Time { get; }
}

public partial class AgentTurn
{
    public ObservableCollection<AgentSteeringNote> SteeringNotes { get; } = new();
    public bool HasSteeringNotes => SteeringNotes.Count > 0;

    public void AddSteeringNote(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        SteeringNotes.Add(new AgentSteeringNote(text));
        OnPropertyChanged(nameof(HasSteeringNotes));
    }
}
