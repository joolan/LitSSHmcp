using System.Windows;
using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Pet;
using LitSSHmcp.App.ViewModels;
using Microsoft.Win32;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;

namespace LitSSHmcp.App.Views;

public partial class PetSettingsView : UserControl
{
    private readonly PetSettingsViewModel _viewModel = new();
    private readonly IUserDialogService _dialogs = new UserDialogService();
    private bool _loaded;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private PetFrameSet? _previewFrames;
    private string? _previewAssetId;
    private CancellationTokenSource? _previewCts;
    private int _previewVersion;
    private int _previewIndex;
    private double _previewAccumulator;

    public PetSettingsView()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _previewTimer.Tick += (_, _) => PreviewTick();
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _previewTimer.Stop();
            _previewCts?.Cancel();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;
        _loaded = true;
        AsyncOperation.Run("加载桌面宠物设置", _viewModel.LoadAsync);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PetSettingsViewModel.SelectedAsset) && IsLoaded)
            AsyncOperation.Run("加载宠物素材预览", LoadPreviewAsync);
    }

    private async Task LoadPreviewAsync()
    {
        if (_viewModel.SelectedAsset is not { } asset)
            return;
        var version = ++_previewVersion;
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        try
        {
            var pack = await new PetAssetService().LoadPreviewAsync(asset.Id, cts.Token);
            if (version != _previewVersion || cts.IsCancellationRequested)
                return;
            _previewFrames = pack.Frames;
            _previewAssetId = asset.Id;
            UpdatePreviewInterval();
            _previewIndex = 0;
            _previewAccumulator = 0;
            var frames = _previewFrames[PetAction.Idle];
            PreviewImage.Source = frames.Count > 0 ? frames[0] : null;
            var metadata = string.Join(" · ", new[] { pack.Source, pack.License }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            PreviewStatus.Text = $"{asset.Name} · {frames.Count} 帧 · {_previewFrames.Fps} fps"
                + (metadata.Length > 0 ? " · " + metadata : string.Empty);
            _previewTimer.Start();
            UpdateCacheInfo();
        }
        catch (OperationCanceledException)
        {
            // 新请求或页签离开时取消
        }
        catch (Exception ex)
        {
            if (version != _previewVersion)
                return;
            _previewFrames = null;
            _previewAssetId = null;
            PreviewImage.Source = null;
            PreviewStatus.Text = "预览失败：" + ex.Message;
            _previewTimer.Stop();
        }
    }

    private void UpdateCacheInfo()
    {
        var stats = PetAssetService.GetCacheStats();
        CacheInfoText.Text = $"缓存 {stats.LiveEntries}/{stats.Entries} 项 · {stats.EstimatedBytes / 1024.0 / 1024.0:0.0} MB · 命中 {stats.Hits} / 未命中 {stats.Misses}";
    }

    private void UpdatePreviewInterval()
    {
        if (_previewFrames is null)
            return;
        var frames = _previewFrames[PetAction.Idle];
        var minDelay = 100;
        for (var i = 0; i < frames.Count; i++)
            minDelay = Math.Min(minDelay, _previewFrames.GetDelay(PetAction.Idle, i));
        _previewTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(minDelay, 16, 100));
    }

    private void PreviewTick()
    {
        if (_previewFrames is null)
            return;
        var frames = _previewFrames[PetAction.Idle];
        if (frames.Count <= 1)
        {
            _previewTimer.Stop();
            return;
        }
        _previewAccumulator += _previewTimer.Interval.TotalMilliseconds;
        var delay = _previewFrames.GetDelay(PetAction.Idle, _previewIndex);
        if (_previewAccumulator < delay)
            return;
        _previewAccumulator %= delay;
        _previewIndex = (_previewIndex + 1) % frames.Count;
        PreviewImage.Source = frames[_previewIndex];
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            return;
        e.Handled = true;
        AsyncOperation.Run("导入宠物素材", async () =>
        {
            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                    await _viewModel.ImportFolderAsync(path);
                else if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    await _viewModel.ImportZipAsync(path);
            }
        });
    }

    private void OnExportAsset(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedAsset is not { } asset)
            return;
        var dialog = new SaveFileDialog
        {
            Title = "导出宠物素材",
            Filter = "ZIP 素材包 (*.zip)|*.zip",
            FileName = asset.Name + ".zip"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        AsyncOperation.Run("导出宠物素材", async () => await _viewModel.ExportSelectedAssetAsync(dialog.FileName));
    }

    private void OnSave(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("保存桌面宠物设置", _viewModel.SaveAsync);

    private void OnCreateAsset(object sender, RoutedEventArgs e)
    {
        var draft = new PetAssetService().CreateBuiltinDraft();
        var dialog = new PetAssetEditorWindow(draft) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result is not { } asset)
            return;
        AsyncOperation.Run("应用宠物素材", async () => await _viewModel.UseAssetAsync(asset));
    }

    private void OnEditAsset(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedAsset is not { } asset)
            return;
        AsyncOperation.Run("编辑宠物素材", async () =>
        {
            var service = new PetAssetService();
            var draft = asset.IsBuiltin
                ? service.CreateBuiltinDraft()
                : await service.LoadDraftAsync(asset.Id);
            var dialog = new PetAssetEditorWindow(draft, asset.IsBuiltin ? null : asset.Id)
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() != true || dialog.Result is not { } result)
                return;
            await _viewModel.UseAssetAsync(result);
        }, ex => _dialogs.Error("加载素材包失败：" + ex.Message, "编辑宠物素材"));
    }

    private void OnCopyAsset(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedAsset is not { } asset)
            return;
        var name = TextInputDialog.Prompt(Window.GetWindow(this), "复制素材包", "新素材包名称:", asset.Name + " 副本");
        if (name is null)
            return;
        AsyncOperation.Run("复制宠物素材", () => _viewModel.CopySelectedAssetAsync(name));
    }

    private void OnRenameAsset(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedAsset is not { IsBuiltin: false } asset)
            return;
        var name = TextInputDialog.Prompt(Window.GetWindow(this), "重命名素材包", "素材包名称:", asset.Name);
        if (name is null)
            return;
        AsyncOperation.Run("重命名宠物素材", () => _viewModel.RenameSelectedAssetAsync(name));
    }

    private void OnImportFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择宠物素材目录" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        AsyncOperation.Run("导入宠物素材", async () => await _viewModel.ImportFolderAsync(dialog.FolderName));
    }

    private void OnImportZip(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择宠物素材 ZIP",
            Filter = "ZIP 素材包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        AsyncOperation.Run("导入宠物素材", async () => await _viewModel.ImportZipAsync(dialog.FileName));
    }

    private void OnDeleteAsset(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedAsset is not { IsBuiltin: false } asset)
            return;
        if (!_dialogs.Confirm($"删除素材「{asset.Name}」？", "删除宠物素材",
                warning: true, defaultNo: true))
            return;
        AsyncOperation.Run("删除宠物素材", _viewModel.DeleteSelectedAssetAsync);
    }

    private void OnResetPosition(object sender, RoutedEventArgs e) => _viewModel.ResetPosition();
}
