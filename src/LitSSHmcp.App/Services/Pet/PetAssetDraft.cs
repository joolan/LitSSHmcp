using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace LitSSHmcp.App.Services.Pet;

public sealed class PetAssetDraftFrame
{
    public required BitmapSource Image { get; init; }
    public int DelayMs { get; set; } = 100;
    public string Label { get; set; } = string.Empty;
}

public sealed class PetAssetDraftAction : INotifyPropertyChanged
{
    private int _fps = 10;
    private bool _loop = true;

    public int Fps
    {
        get => _fps;
        set { if (_fps == value) return; _fps = value; OnPropertyChanged(); }
    }

    public bool Loop
    {
        get => _loop;
        set { if (_loop == value) return; _loop = value; OnPropertyChanged(); }
    }

    public ObservableCollection<PetAssetDraftFrame> Frames { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class PetAssetDraft
{
    public string Name { get; set; } = "我的宠物";
    public string Source { get; set; } = string.Empty;
    public string License { get; set; } = string.Empty;
    public int Fps { get; set; } = 10;
    public Dictionary<PetAction, PetAssetDraftAction> Actions { get; } = new();

    public PetAssetDraft()
    {
        foreach (var action in Enum.GetValues<PetAction>())
            Actions[action] = new PetAssetDraftAction();
    }
}
