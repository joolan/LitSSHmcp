using System.ComponentModel;
using System.Runtime.CompilerServices;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

public sealed record RuntimeTargetOption(string AssetKey, string Label);

public sealed class UrlPatternRow : INotifyPropertyChanged
{
    private string _url = string.Empty;
    private int _roleIndex;
    private string _priorityText = "0";
    private string _note = string.Empty;

    public string Url
    {
        get => _url;
        set => Set(ref _url, value);
    }

    public int RoleIndex
    {
        get => _roleIndex;
        set => Set(ref _roleIndex, value);
    }

    public string PriorityText
    {
        get => _priorityText;
        set => Set(ref _priorityText, value);
    }

    public string Note
    {
        get => _note;
        set => Set(ref _note, value);
    }

    public static UrlPatternRow From(UrlPattern pattern) => new()
    {
        Url = pattern.Url,
        RoleIndex = (int)pattern.Role,
        PriorityText = pattern.Priority.ToString(),
        Note = pattern.Note ?? string.Empty
    };

    public UrlPattern? ToModel()
    {
        if (string.IsNullOrWhiteSpace(Url))
            return null;

        return new UrlPattern
        {
            Url = Url.Trim(),
            Role = RoleIndex is >= 0 and <= 3 ? (UrlRole)RoleIndex : UrlRole.Auto,
            Priority = int.TryParse(PriorityText?.Trim(), out var priority) ? priority : 0,
            Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim()
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
