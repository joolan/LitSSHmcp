using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Pet;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public sealed class PetSettingsViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService;
    private readonly PetAssetService _assets = new();
    private AppConfig? _config;
    private bool _busy;
    private string _statusMessage = string.Empty;

    public PetSettingsViewModel(IConfigService? configService = null)
    {
        _configService = configService ?? AppServiceFactory.CreateConfigService();
    }

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled == value) return; _enabled = value; OnPropertyChanged(); }
    }

    private double _scale = 1.0;
    public double Scale
    {
        get => _scale;
        set { if (Math.Abs(_scale - value) < 0.001) return; _scale = value; OnPropertyChanged(); }
    }

    private double _opacity = 1.0;
    public double Opacity
    {
        get => _opacity;
        set { if (Math.Abs(_opacity - value) < 0.001) return; _opacity = value; OnPropertyChanged(); }
    }

    private int _speed = 70;
    public int Speed
    {
        get => _speed;
        set { if (_speed == value) return; _speed = value; OnPropertyChanged(); }
    }

    private bool _movementEnabled = true;
    public bool MovementEnabled
    {
        get => _movementEnabled;
        set { if (_movementEnabled == value) return; _movementEnabled = value; OnPropertyChanged(); }
    }

    private bool _topMost = true;
    private bool _reduceMotion;
    public bool TopMost
    {
        get => _topMost;
        set { if (_topMost == value) return; _topMost = value; OnPropertyChanged(); }
    }

    public bool ReduceMotion
    {
        get => _reduceMotion;
        set { if (_reduceMotion == value) return; _reduceMotion = value; OnPropertyChanged(); }
    }

    public bool Busy
    {
        get => _busy;
        private set { if (_busy == value) return; _busy = value; OnPropertyChanged(); }
    }

    public ObservableCollection<PetAssetInfo> Assets { get; } = new();

    private PetAssetInfo? _selectedAsset;
    public PetAssetInfo? SelectedAsset
    {
        get => _selectedAsset;
        set { if (ReferenceEquals(_selectedAsset, value)) return; _selectedAsset = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set { if (_statusMessage == value) return; _statusMessage = value; OnPropertyChanged(); }
    }

    public async Task LoadAsync()
    {
        Busy = true;
        try
        {
            _config = await _configService.LoadConfigAsync();
            var pet = _config.Pet ?? new PetConfig();
            await ReloadAssetsAsync(pet.AssetId);
            Enabled = pet.Enabled;
            Scale = pet.Scale;
            Opacity = pet.Opacity;
            Speed = pet.Speed;
            MovementEnabled = pet.MovementEnabled;
            TopMost = pet.TopMost;
            ReduceMotion = pet.ReduceMotion;
            RaiseAll();
            StatusMessage = Enabled ? "桌面宠物已启用。" : "桌面宠物未启用。";
        }
        catch (Exception ex)
        {
            StatusMessage = "加载桌面宠物设置失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task SaveAsync()
    {
        Busy = true;
        try
        {
            _config ??= await _configService.LoadConfigAsync();
            var pet = _config.Pet ?? new PetConfig();
            pet.AssetId = SelectedAsset?.Id ?? PetAssetService.BuiltinId;
            pet.Enabled = Enabled;
            pet.Scale = Math.Clamp(Scale, 0.5, 2.0);
            pet.Opacity = Math.Clamp(Opacity, 0.2, 1.0);
            pet.Speed = Math.Clamp(Speed, 10, 240);
            pet.MovementEnabled = MovementEnabled;
            pet.TopMost = TopMost;
            pet.ReduceMotion = ReduceMotion;
            _config.Pet = pet;
            await _configService.SaveConfigAsync(_config);
            await PetService.ApplyAsync(pet);
            StatusMessage = Enabled ? "已保存并显示桌面宠物。" : "已保存，桌面宠物已关闭。";
        }
        catch (Exception ex)
        {
            StatusMessage = "保存桌面宠物设置失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task<PetAssetInfo?> ImportFolderAsync(string path)
    {
        try
        {
            var info = await _assets.ImportFolderAsync(path);
            await ReloadAssetsAsync(info.Id);
            await SaveAsync();
            StatusMessage = "已导入素材：" + info.Name;
            return info;
        }
        catch (Exception ex)
        {
            StatusMessage = "导入素材失败：" + ex.Message;
            return null;
        }
    }

    public async Task<PetAssetInfo?> ImportZipAsync(string path)
    {
        try
        {
            var info = await _assets.ImportZipAsync(path);
            await ReloadAssetsAsync(info.Id);
            await SaveAsync();
            StatusMessage = "已导入素材：" + info.Name;
            return info;
        }
        catch (Exception ex)
        {
            StatusMessage = "导入素材失败：" + ex.Message;
            return null;
        }
    }

    public async Task DeleteSelectedAssetAsync()
    {
        if (SelectedAsset is not { IsBuiltin: false } asset)
            return;
        try
        {
            await _assets.DeleteAsync(asset.Id);
            await ReloadAssetsAsync(PetAssetService.BuiltinId);
            await SaveAsync();
            StatusMessage = "素材已删除。";
        }
        catch (Exception ex)
        {
            StatusMessage = "删除素材失败：" + ex.Message;
        }
    }

    public async Task ExportSelectedAssetAsync(string path)
    {
        if (SelectedAsset is not { } asset)
            return;
        try
        {
            await _assets.ExportZipAsync(asset.Id, path);
            StatusMessage = "素材已导出：" + path;
        }
        catch (Exception ex)
        {
            StatusMessage = "导出素材失败：" + ex.Message;
        }
    }

    public async Task CopySelectedAssetAsync(string? name)
    {
        if (SelectedAsset is not { } asset)
            return;
        try
        {
            var copy = await _assets.CopyAsync(asset.Id, name);
            await ReloadAssetsAsync(copy.Id);
            StatusMessage = "已复制素材：" + copy.Name;
        }
        catch (Exception ex)
        {
            StatusMessage = "复制素材失败：" + ex.Message;
        }
    }

    public async Task RenameSelectedAssetAsync(string name)
    {
        if (SelectedAsset is not { IsBuiltin: false } asset)
            return;
        try
        {
            var renamed = await _assets.RenameAsync(asset.Id, name);
            await ReloadAssetsAsync(renamed.Id);
            StatusMessage = "素材已重命名：" + renamed.Name;
        }
        catch (Exception ex)
        {
            StatusMessage = "重命名素材失败：" + ex.Message;
        }
    }

    public async Task UseAssetAsync(PetAssetInfo asset)
    {
        await ReloadAssetsAsync(asset.Id);
        await SaveAsync();
    }

    public void ResetPosition()
    {
        PetService.ResetPosition();
        StatusMessage = "宠物位置已重置。";
    }

    private async Task ReloadAssetsAsync(string? selectedId)
    {
        var assets = await _assets.ListAsync();
        Assets.Clear();
        foreach (var asset in assets)
            Assets.Add(asset);
        SelectedAsset = Assets.FirstOrDefault(item => item.Id == selectedId)
            ?? Assets.FirstOrDefault();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(Scale));
        OnPropertyChanged(nameof(Opacity));
        OnPropertyChanged(nameof(Speed));
        OnPropertyChanged(nameof(MovementEnabled));
        OnPropertyChanged(nameof(TopMost));
        OnPropertyChanged(nameof(ReduceMotion));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
