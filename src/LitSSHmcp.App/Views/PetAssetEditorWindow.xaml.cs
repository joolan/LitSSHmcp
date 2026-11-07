using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Pet;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;

namespace LitSSHmcp.App.Views;

public partial class PetAssetEditorWindow : FluentWindow
{
    private sealed record ActionItem(PetAction Action, string Label);
    private sealed record FrameSnapshot(BitmapSource Image, int DelayMs, string Label);
    private sealed record ActionSnapshot(int Fps, bool Loop, List<FrameSnapshot> Frames);
    private sealed record EditorSnapshot(PetAction SelectedAction,
        Dictionary<PetAction, ActionSnapshot> Actions);
    private readonly PetAssetDraft _draft;
    private readonly string? _existingId;
    private readonly IUserDialogService _dialogs = new UserDialogService();
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private PetAction _selectedAction = PetAction.Idle;
    private int _previewIndex;
    private double _previewAccumulator;
    private bool _syncing;
    private bool _dirty;
    private bool _saved;
    private bool _loaded;
    private Point _dragStart;
    private PetAssetDraftFrame? _dragFrame;
    private readonly List<EditorSnapshot> _undo = new();
    private readonly List<EditorSnapshot> _redo = new();
    private bool _restoring;

    public PetAssetEditorWindow(PetAssetDraft? initial = null, string? existingId = null)
    {
        _draft = initial ?? new PetAssetDraft();
        _existingId = existingId;
        InitializeComponent();
        _syncing = true;
        NameBox.Text = _draft.Name;
        GlobalFpsBox.Text = _draft.Fps.ToString();
        SourceBox.Text = _draft.Source;
        LicenseBox.Text = _draft.License;
        ActionBox.ItemsSource = Enum.GetValues<PetAction>().Select(action => new ActionItem(action, ActionLabel(action))).ToArray();
        ActionBox.SelectedIndex = 0;
        _previewTimer.Tick += (_, _) => PreviewTick();
        Loaded += (_, _) =>
        {
            _loaded = true;
            _dirty = false;
            _previewTimer.Start();
        };
        Closed += (_, _) => _previewTimer.Stop();
        UpdateActionUi();
        _syncing = false;
        Title = existingId is null ? "素材包制作" : "编辑素材包";
        EditorTitleBar.Title = Title;
    }

    public PetAssetInfo? Result { get; private set; }

    private PetAssetDraftAction CurrentAction => _draft.Actions[_selectedAction];

    private void OnActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ActionBox.SelectedItem is not ActionItem item)
            return;
        _selectedAction = item.Action;
        UpdateActionUi();
    }

    private void UpdateActionUi()
    {
        _syncing = true;
        try
        {
            ActionFpsBox.Text = CurrentAction.Fps.ToString();
            LoopBox.IsChecked = CurrentAction.Loop;
            FramesList.ItemsSource = CurrentAction.Frames;
            FramesList.SelectedIndex = CurrentAction.Frames.Count > 0 ? 0 : -1;
            FrameDelayBox.Text = CurrentAction.Frames.Count > 0
                ? CurrentAction.Frames[0].DelayMs.ToString()
                : string.Empty;
            _previewIndex = 0;
            _previewAccumulator = 0;
            PreviewInfo.Text = $"{CurrentAction.Frames.Count} 帧 · {CurrentAction.Fps} fps";
        }
        finally
        {
            _syncing = false;
        }
        UpdatePreview();
    }

    private void OnActionFpsChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || PreviewInfo is null)
            return;
        if (!int.TryParse(ActionFpsBox.Text, out var fps))
            return;
        PushUndo();
        CurrentAction.Fps = Math.Clamp(fps, 1, 24);
        foreach (var frame in CurrentAction.Frames)
            frame.DelayMs = Math.Max(10, (int)Math.Round(1000.0 / CurrentAction.Fps));
        PreviewInfo.Text = $"{CurrentAction.Frames.Count} 帧 · {CurrentAction.Fps} fps";
        _previewAccumulator = 0;
        MarkDirty();
    }

    private void OnLoopChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing)
            return;
        PushUndo();
        CurrentAction.Loop = LoopBox.IsChecked == true;
        MarkDirty();
    }

    private void OnFrameDelayChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || FramesList.SelectedItem is not PetAssetDraftFrame frame)
            return;
        if (!int.TryParse(FrameDelayBox.Text, out var delay))
            return;
        PushUndo();
        frame.DelayMs = Math.Clamp(delay, 10, 5000);
        _previewAccumulator = 0;
        MarkDirty();
    }

    private void OnMetadataChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncing)
            MarkDirty();
    }

    private void OnImportFrames(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择宠物帧图片",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.gif)|*.png;*.jpg;*.jpeg;*.gif|所有文件 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            foreach (var file in dialog.FileNames)
            {
                foreach (var frame in LoadFrames(file, CurrentAction.Fps))
                    CurrentAction.Frames.Add(frame);
            }
            RenumberFrames();
            UpdatePreview();
            PreviewInfo.Text = $"{CurrentAction.Frames.Count} 帧 · {CurrentAction.Fps} fps";
            StatusText.Text = "已导入 " + dialog.FileNames.Length + " 个文件。";
            MarkDirty();
        }
        catch (Exception ex)
        {
            StatusText.Text = "导入失败：" + ex.Message;
        }
    }

    private void OnImportSpriteSheet(object sender, RoutedEventArgs e)
    {
        var delay = Math.Max(10, (int)Math.Round(1000.0 / Math.Max(1, CurrentAction.Fps)));
        var dialog = new SpriteSheetImportWindow(delay) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result is not { } frames)
            return;
        if (CurrentAction.Frames.Count + frames.Count > 300)
        {
            StatusText.Text = "单个动作最多 300 帧。";
            return;
        }
        PushUndo();
        foreach (var frame in frames)
            CurrentAction.Frames.Add(frame);
        RenumberFrames();
        UpdatePreview();
        PreviewInfo.Text = $"{CurrentAction.Frames.Count} 帧 · {CurrentAction.Fps} fps";
        StatusText.Text = $"已从精灵图导入 {frames.Count} 帧。";
        MarkDirty();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => MoveFrame(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => MoveFrame(1);

    private void MoveFrame(int direction)
    {
        if (FramesList.SelectedItem is not PetAssetDraftFrame frame)
            return;
        var index = CurrentAction.Frames.IndexOf(frame);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= CurrentAction.Frames.Count)
            return;
        PushUndo();
        CurrentAction.Frames.Move(index, target);
        RenumberFrames();
        FramesList.SelectedItem = frame;
        UpdatePreview();
        MarkDirty();
    }

    private void OnDeleteFrame(object sender, RoutedEventArgs e)
    {
        if (FramesList.SelectedItem is not PetAssetDraftFrame frame)
            return;
        PushUndo();
        CurrentAction.Frames.Remove(frame);
        RenumberFrames();
        UpdatePreview();
        PreviewInfo.Text = $"{CurrentAction.Frames.Count} 帧 · {CurrentAction.Fps} fps";
        MarkDirty();
    }

    private void OnClearAction(object sender, RoutedEventArgs e)
    {
        PushUndo();
        CurrentAction.Frames.Clear();
        UpdatePreview();
        PreviewInfo.Text = $"0 帧 · {CurrentAction.Fps} fps";
        MarkDirty();
    }

    private void OnFrameSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _syncing = true;
        try
        {
            FrameDelayBox.Text = FramesList.SelectedItem is PetAssetDraftFrame frame
                ? frame.DelayMs.ToString()
                : string.Empty;
        }
        finally
        {
            _syncing = false;
        }
        UpdatePreview();
    }

    private void OnFramesMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(FramesList);
        _dragFrame = FramesList.SelectedItem as PetAssetDraftFrame;
    }

    private void OnFramesMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || _dragFrame is null)
            return;
        var point = e.GetPosition(FramesList);
        if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        var frame = _dragFrame;
        _dragFrame = null;
        DragDrop.DoDragDrop(FramesList, frame, DragDropEffects.Move);
    }

    private void OnFramesDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(PetAssetDraftFrame)) is not PetAssetDraftFrame frame)
            return;
        var item = ItemsControl.ContainerFromElement(FramesList, e.OriginalSource as DependencyObject) as ListBoxItem;
        var target = item?.DataContext as PetAssetDraftFrame;
        var oldIndex = CurrentAction.Frames.IndexOf(frame);
        var newIndex = target is null ? CurrentAction.Frames.Count - 1 : CurrentAction.Frames.IndexOf(target);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex)
            return;
        PushUndo();
        CurrentAction.Frames.Move(oldIndex, newIndex);
        RenumberFrames();
        FramesList.SelectedItem = frame;
        UpdatePreview();
        MarkDirty();
        e.Handled = true;
    }

    private void RenumberFrames()
    {
        for (var i = 0; i < CurrentAction.Frames.Count; i++)
            CurrentAction.Frames[i].Label = $"{ActionLabel(_selectedAction)} {i + 1:000}";
    }

    private void PreviewTick()
    {
        var frames = CurrentAction.Frames;
        if (frames.Count <= 1)
            return;
        _previewAccumulator += _previewTimer.Interval.TotalMilliseconds;
        var delay = frames[_previewIndex].DelayMs;
        if (_previewAccumulator < delay)
            return;
        _previewAccumulator %= delay;
        _previewIndex = (_previewIndex + 1) % frames.Count;
        PreviewImage.Source = frames[_previewIndex].Image;
    }

    private void UpdatePreview()
    {
        var frames = CurrentAction.Frames;
        _previewIndex = 0;
        _previewAccumulator = 0;
        PreviewImage.Source = frames.Count > 0 ? frames[0].Image : null;
    }

    private static IEnumerable<PetAssetDraftFrame> LoadFrames(string path, int fps)
    {
        var frameDelay = Math.Max(10, (int)Math.Round(1000.0 / Math.Clamp(fps, 1, 24)));
        if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
        {
            var decoder = new GifBitmapDecoder(new Uri(path),
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            foreach (var frame in decoder.Frames)
            {
                if (frame.CanFreeze)
                    frame.Freeze();
                yield return new PetAssetDraftFrame
                {
                    Image = frame,
                    DelayMs = ReadGifDelay(frame, frameDelay)
                };
            }
        }
        else
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            yield return new PetAssetDraftFrame { Image = bitmap, DelayMs = frameDelay };
        }
    }

    private static int ReadGifDelay(BitmapFrame frame, int fallback)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata)
            {
                var value = metadata.GetQuery("/grctlext/Delay");
                if (value is ushort delay)
                    return Math.Max(10, delay * 10);
                if (value is int intDelay)
                    return Math.Max(10, intDelay * 10);
            }
        }
        catch
        {
            // ignore
        }
        return fallback;
    }

    private void OnSave(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("保存宠物素材包", async () =>
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                StatusText.Text = "请填写素材包名称。";
                return;
            }
            if (_draft.Actions[PetAction.Idle].Frames.Count == 0)
            {
                StatusText.Text = "idle 动作至少需要一帧。";
                return;
            }
            if (!int.TryParse(GlobalFpsBox.Text, out var fps))
                fps = 10;
            _draft.Name = NameBox.Text.Trim();
            _draft.Source = SourceBox.Text.Trim();
            _draft.License = LicenseBox.Text.Trim();
            _draft.Fps = Math.Clamp(fps, 1, 24);
            try
            {
                StatusText.Text = "正在保存…";
                Result = await new PetAssetService().SaveDraftAsync(_draft, _existingId);
                _saved = true;
                _dirty = false;
                _undo.Clear();
                _redo.Clear();
                UpdateUndoButtons();
                StatusText.Text = "已保存素材包。";
                DialogResult = true;
            }
            catch (Exception ex)
            {
                StatusText.Text = "保存失败：" + ex.Message;
            }
        });

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void MarkDirty()
    {
        if (!_saved && _loaded)
            _dirty = true;
    }

    private EditorSnapshot CaptureSnapshot()
    {
        var actions = _draft.Actions.ToDictionary(
            pair => pair.Key,
            pair => new ActionSnapshot(
                pair.Value.Fps,
                pair.Value.Loop,
                pair.Value.Frames.Select(frame => new FrameSnapshot(
                    frame.Image, frame.DelayMs, frame.Label)).ToList()));
        return new EditorSnapshot(_selectedAction, actions);
    }

    private void PushUndo()
    {
        if (_restoring || !_loaded)
            return;
        _undo.Add(CaptureSnapshot());
        if (_undo.Count > 50)
            _undo.RemoveAt(0);
        _redo.Clear();
        UpdateUndoButtons();
    }

    private void UpdateUndoButtons()
    {
        if (UndoButton is not null)
            UndoButton.IsEnabled = _undo.Count > 0;
        if (RedoButton is not null)
            RedoButton.IsEnabled = _redo.Count > 0;
    }

    private void OnUndo(object sender, RoutedEventArgs e) => Undo();
    private void OnRedo(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (_undo.Count == 0)
            return;
        _redo.Add(CaptureSnapshot());
        var snapshot = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        RestoreSnapshot(snapshot);
        UpdateUndoButtons();
    }

    private void Redo()
    {
        if (_redo.Count == 0)
            return;
        _undo.Add(CaptureSnapshot());
        var snapshot = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        RestoreSnapshot(snapshot);
        UpdateUndoButtons();
    }

    private void RestoreSnapshot(EditorSnapshot snapshot)
    {
        _restoring = true;
        _syncing = true;
        try
        {
            foreach (var (action, source) in snapshot.Actions)
            {
                var target = _draft.Actions[action];
                target.Fps = source.Fps;
                target.Loop = source.Loop;
                target.Frames.Clear();
                foreach (var frame in source.Frames)
                {
                    target.Frames.Add(new PetAssetDraftFrame
                    {
                        Image = frame.Image,
                        DelayMs = frame.DelayMs,
                        Label = frame.Label
                    });
                }
            }
            _selectedAction = snapshot.SelectedAction;
            ActionBox.SelectedItem = ActionBox.Items.Cast<ActionItem>()
                .FirstOrDefault(item => item.Action == _selectedAction);
            UpdateActionUi();
            _dirty = true;
        }
        finally
        {
            _syncing = false;
            _restoring = false;
        }
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control)
            return;
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase)
            return;
        if (e.Key == System.Windows.Input.Key.Z)
        {
            Undo();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Y)
        {
            Redo();
            e.Handled = true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!IsVisible || _saved || !_dirty)
            return;
        if (!_dialogs.Confirm("当前素材包尚未保存，确定关闭吗？", "未保存的修改",
                warning: false, defaultNo: true))
            e.Cancel = true;
    }

    private static string ActionLabel(PetAction action) => action switch
    {
        PetAction.Idle => "待机 idle",
        PetAction.Walk => "行走 walk",
        PetAction.Drag => "拖动 drag",
        PetAction.Click => "点击 click",
        PetAction.Sleep => "睡觉 sleep",
        PetAction.Stretch => "伸懒腰 stretch",
        PetAction.Look => "左右看 look",
        PetAction.Work => "工作 work",
        _ => action.ToString()
    };
}
