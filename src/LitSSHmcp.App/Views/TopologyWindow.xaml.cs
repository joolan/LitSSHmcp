using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Microsoft.Win32;

namespace LitSSHmcp.App.Views;

public partial class TopologyWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly TopologyViewModel _viewModel = new();

    private bool _dragging;
    private bool _dragMoved;
    private bool _resizing;
    private bool _linking;
    private string? _dragId;
    private string? _handle;
    private string? _anchorDrag;
    private Point _last;
    private bool _errorShown;
    private bool _initialFitDone;

    // 无限画布: 缩放 + 平移
    private readonly ScaleTransform _zoom = new(1, 1);
    private readonly TranslateTransform _pan = new(0, 0);
    private readonly DrawingBrush _gridBrush;
    private bool _panning;
    private Point _panStart;
    private double _panStartX;
    private double _panStartY;

    // 关系属性面板拖动
    private bool _panelDragging;
    private Point _panelStart;
    private double _panelStartX;
    private double _panelStartY;

    // 拖动期间帧间隔探针（仅 LITSSH_PERF=1 时启用，区分 VM 耗时 vs 渲染掉帧）
    private bool _renderProbe;
    private long _renderLastTs;
    private long _renderCount;
    private double _renderTotalMs;
    private double _renderMaxMs;
    private int _probeMoveEvents;

    public TopologyWindow()
    {
        InitializeComponent();
#if DEBUG
        foreach (var item in MoreActionsButton.ContextMenu.Items)
        {
            if (item is MenuItem { Tag: "stress" } mi)
                mi.Visibility = Visibility.Visible;
            else if (item is Separator { Tag: "stress" } sep)
                sep.Visibility = Visibility.Visible;
        }
#endif

        var group = new TransformGroup();
        group.Children.Add(_zoom);
        group.Children.Add(_pan);
        GraphCanvas.RenderTransformOrigin = new Point(0, 0);
        GraphCanvas.RenderTransform = group;

        _gridBrush = new DrawingBrush
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 20, 20),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
            Drawing = new GeometryDrawing
            {
                Pen = new Pen(new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC)), 1),
                Geometry = new RectangleGeometry(new Rect(0, 0, 20, 20))
            }
        };
        GridLayer.Fill = _gridBrush;

        _viewModel.Confirm = Confirm;
        _viewModel.GraphLoaded += OnGraphLoaded;
        DataContext = _viewModel;

        Loaded += OnWindowLoaded;
    }

    private bool Confirm(string message) =>
        MessageBox.Show(this, message, "资产拓扑", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Canvas.SetLeft(EdgePanel, Math.Max(8, CanvasHost.ActualWidth - EdgePanel.Width - 16));
        Canvas.SetTop(EdgePanel, 8);
        // 数据可能在窗口显示前/后加载完成，两条路径都尝试"适应"（仅首次）
        TryInitialFit();
    }

    /// <summary>图谱加载完成后回调（数据加载可能晚于窗口 Loaded）。</summary>
    private void OnGraphLoaded() => TryInitialFit();

    /// <summary>首次打开时"适应窗口"：等布局就绪后在后台优先级执行，只做一次（刷新/拖动不打扰用户缩放）。</summary>
    private void TryInitialFit()
    {
        if (_initialFitDone || !_viewModel.HasContent)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_initialFitDone)
                return;
            FitView();
            _initialFitDone = true;
        }));
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _viewModel.Load();

    private void OnDiscoveryReport(object sender, RoutedEventArgs e)
    {
        var window = new Window
        {
            Title = "自动发现报告",
            Width = 760,
            Height = 580,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var box = new TextBox
        {
            Text = _viewModel.DiscoveryReport,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI")
        };
        Grid.SetRow(box, 0);
        grid.Children.Add(box);

        var close = new Button
        {
            Content = "关闭",
            Width = 80,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true,
            IsCancel = true
        };
        close.Click += (_, _) => window.Close();
        Grid.SetRow(close, 1);
        grid.Children.Add(close);

        window.Content = grid;
        window.ShowDialog();
    }

    private void OnMoreActions(object sender, RoutedEventArgs e)
    {
        if (MoreActionsButton.ContextMenu is not { } menu)
            return;
        menu.PlacementTarget = MoreActionsButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>把画布有效区域（全部内容 + 边距，忽略当前缩放/平移）导出为 PNG。</summary>
    private void OnExportImage(object sender, RoutedEventArgs e)
    {
        try
        {
            var b = _viewModel.ContentBounds();
            if (b.Width < 1 || b.Height < 1)
            {
                _viewModel.StatusMessage = "导出失败: 画布没有内容";
                return;
            }

            b.Inflate(24, 24);
            // 超大图退化为 1x，避免位图过大
            var scale = b.Width > 4000 || b.Height > 4000 ? 1 : 2;
            var pixelW = Math.Max(1, (int)Math.Ceiling(b.Width * scale));
            var pixelH = Math.Max(1, (int)Math.Ceiling(b.Height * scale));

            var dlg = new SaveFileDialog
            {
                Filter = "PNG 图片|*.png",
                FileName = $"topology-{DateTime.Now:yyyyMMdd-HHmmss}.png"
            };
            if (dlg.ShowDialog(this) != true)
                return;

            var bmp = new RenderTargetBitmap(pixelW, pixelH, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var savedTransform = GraphCanvas.RenderTransform;
            var savedBackground = GraphCanvas.Background;
            var prevNode = _viewModel.SelectedIdOrNull;
            var prevEdge = _viewModel.SelectedEdgeOrNull;

            try
            {
                // 导出不含选中框/手柄/端口等交互覆盖物，背景用画布底色
                _viewModel.ClearEdgeSelection();
                _viewModel.ClearSelection();
                GraphCanvas.Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
                GraphCanvas.RenderTransform = new TranslateTransform(-b.X, -b.Y);
                bmp.Render(GraphCanvas);
            }
            finally
            {
                GraphCanvas.RenderTransform = savedTransform;
                GraphCanvas.Background = savedBackground;
                if (prevEdge != null)
                    _viewModel.SelectEdge(prevEdge);
                if (prevNode != null)
                    _viewModel.SelectNode(prevNode);
            }

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(dlg.FileName))
                enc.Save(fs);

            _viewModel.StatusMessage = $"已导出图片: {dlg.FileName}";
        }
        catch (Exception ex)
        {
            _viewModel.StatusMessage = $"导出图片失败: {ex.Message}";
        }
    }

    private void OnStress(object sender, RoutedEventArgs e)
    {
        _viewModel.LoadSynthetic();
        FitView();
    }

    private void OnDiscover(object sender, RoutedEventArgs e) => _viewModel.Discover(ServerFilterBox.Text);

    private void OnResetLayout(object sender, RoutedEventArgs e)
    {
        _viewModel.ResetLayout();
        FitView();
    }

    private void OnDeleteEdge(object sender, RoutedEventArgs e) => _viewModel.DeleteSelectedEdge();
    private void OnSaveEdge(object sender, RoutedEventArgs e) => _viewModel.SaveSelectedEdge();

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _viewModel.HasSelectedEdge)
        {
            _viewModel.DeleteSelectedEdge();
            e.Handled = true;
            return;
        }

        // 方向键移动选中节点
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0 &&
            e.Key is Key.Left or Key.Right or Key.Up or Key.Down && _viewModel.HasSelection)
        {
            const double step = 10;
            var (dx, dy) = e.Key switch
            {
                Key.Left => (-step, 0.0),
                Key.Right => (step, 0.0),
                Key.Up => (0.0, -step),
                _ => (0.0, step)
            };
            _viewModel.NudgeSelected(dx, dy);
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key == Key.Z)
            {
                _viewModel.Undo();
                e.Handled = true;
            }
            else if (e.Key == Key.Y)
            {
                _viewModel.Redo();
                e.Handled = true;
            }
            else if (e.Key == Key.D0 || e.Key == Key.NumPad0)
            {
                ResetZoom();
                e.Handled = true;
            }
        }
    }

    // ---- 缩放/平移/网格 ----

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomAt(new Point(CanvasHost.ActualWidth / 2, CanvasHost.ActualHeight / 2), 1.2);
    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomAt(new Point(CanvasHost.ActualWidth / 2, CanvasHost.ActualHeight / 2), 1 / 1.2);
    private void OnZoomReset(object sender, RoutedEventArgs e) => ResetZoom();
    private void OnZoomFit(object sender, RoutedEventArgs e) => FitView();

    private void UpdateGrid()
    {
        var g = new TransformGroup();
        g.Children.Add(new ScaleTransform(_zoom.ScaleX, _zoom.ScaleY));
        g.Children.Add(new TranslateTransform(_pan.X, _pan.Y));
        g.Freeze();
        _gridBrush.Transform = g;
    }

    private void ResetZoom()
    {
        _zoom.ScaleX = _zoom.ScaleY = 1;
        _pan.X = 0;
        _pan.Y = 0;
        UpdateGrid();
    }

    private void OnCanvasWheel(object sender, MouseWheelEventArgs e)
    {
        if (EdgePanel.IsMouseOver)
            return;

        ZoomAt(e.GetPosition(CanvasHost), e.Delta > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    /// <summary>以 host 上的某点为锚点缩放，保持该点下方内容不动。</summary>
    private void ZoomAt(Point hostPoint, double factor)
    {
        using var perf = PerfLog.Scope("Zoom", () => $"z={_zoom.ScaleX:F2}");

        var local = new Point(
            (hostPoint.X - _pan.X) / _zoom.ScaleX,
            (hostPoint.Y - _pan.Y) / _zoom.ScaleY);

        var z = Math.Clamp(_zoom.ScaleX * factor, 0.2, 5.0);
        _zoom.ScaleX = z;
        _zoom.ScaleY = z;
        _pan.X = hostPoint.X - z * local.X;
        _pan.Y = hostPoint.Y - z * local.Y;
        UpdateGrid();
    }

    private void FitView()
    {
        if (CanvasHost.ActualWidth < 1 || CanvasHost.ActualHeight < 1)
            return;

        var b = _viewModel.ContentBounds();
        const double pad = 30;
        var z = Math.Clamp(
            Math.Min((CanvasHost.ActualWidth - 2 * pad) / b.Width, (CanvasHost.ActualHeight - 2 * pad) / b.Height),
            0.2,
            2.0);

        _zoom.ScaleX = z;
        _zoom.ScaleY = z;
        _pan.X = (CanvasHost.ActualWidth - z * b.Width) / 2 - z * b.X;
        _pan.Y = (CanvasHost.ActualHeight - z * b.Height) / 2 - z * b.Y;
        UpdateGrid();
    }

    // ---- 鼠标 ----

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 面板区域内的操作交给面板处理，不触发画布选择/拖动/平移
        if (EdgePanel.IsMouseOver)
            return;

        var point = e.GetPosition(GraphCanvas);

        // 1) 端口 -> 拖拽建边
        if (_viewModel.HasSelection)
        {
            var port = _viewModel.HitPort(point);
            if (port != null && _viewModel.SelectedIdOrNull is { } sourceId)
            {
                _viewModel.StartLink(sourceId, port);
                _linking = true;
                _last = point;
                CanvasHost.CaptureMouse();
                return;
            }
        }

        // 2) 缩放手柄
        var handle = _viewModel.HasSelection ? _viewModel.HitHandle(point) : null;
        if (handle != null)
        {
            _resizing = true;
            _handle = handle;
            _dragId = _viewModel.SelectedIdOrNull;
            if (_dragId != null)
                _viewModel.BeginNodeDrag(_dragId, point);
            _last = point;
            CanvasHost.CaptureMouse();
            return;
        }

        // 2a) 选中节点四边内侧缩放带 -> 拉动该边调整对应侧（角手柄在 2 中优先）
        var edgeHandle = _viewModel.HasSelection ? _viewModel.HitEdgeHandle(point) : null;
        if (edgeHandle != null)
        {
            _resizing = true;
            _handle = edgeHandle;
            _dragId = _viewModel.SelectedIdOrNull;
            if (_dragId != null)
                _viewModel.BeginNodeDrag(_dragId, point);
            _last = point;
            CanvasHost.CaptureMouse();
            return;
        }

        // 2b) 连线端点锚点
        if (_viewModel.HasSelectedEdge)
        {
            var anchor = _viewModel.HitEdgeAnchor(point);
            if (anchor != null)
            {
                _anchorDrag = anchor;
                CanvasHost.CaptureMouse();
                return;
            }
        }

        // 3) 叶子节点（应用/库，含托管子节点）→ 选中并拖动（优先于连线，避免连线盖住节点导致无法拖动）
        var leaf = _viewModel.HitLeaf(point);
        if (leaf != null)
        {
            _viewModel.ClearEdgeSelection();
            _viewModel.SelectNode(leaf);
            _viewModel.BeginNodeDrag(leaf, point);
            _dragging = true;
            _dragId = leaf;
            _last = point;
            CanvasHost.CaptureMouse();
            return;
        }

        // 4) 连线 -> 选中（优先于服务器区块，保证区块内的连线也能选中）
        var edge = _viewModel.HitEdge(point, 4);
        if (edge != null)
        {
            _viewModel.ClearSelection();
            _viewModel.SelectEdge(edge);
            return;
        }

        // 5) 服务器区块 -> 选中并拖动
        var box = _viewModel.HitBox(point);
        if (box != null)
        {
            _viewModel.ClearEdgeSelection();
            _viewModel.SelectNode(box);
            _viewModel.BeginNodeDrag(box, point);
            _dragging = true;
            _dragId = box;
            _last = point;
            CanvasHost.CaptureMouse();
            return;
        }

        // 6) 空白 -> 清除选择并用左键拖动平移画布
        _viewModel.ClearSelection();
        _viewModel.ClearEdgeSelection();
        _panning = true;
        _panStart = e.GetPosition(CanvasHost);
        _panStartX = _pan.X;
        _panStartY = _pan.Y;
        CanvasHost.Cursor = Cursors.SizeAll;
        CanvasHost.CaptureMouse();
    }

    private void OnCanvasRightDown(object sender, MouseButtonEventArgs e)
    {
        if (EdgePanel.IsMouseOver)
            return;

        var id = _viewModel.HitTest(e.GetPosition(GraphCanvas));
        if (id == null)
            return;

        _viewModel.ClearEdgeSelection();
        _viewModel.SelectNode(id);

        var menu = new ContextMenu();
        var relations = new MenuItem { Header = "查看/编辑关系…" };
        relations.Click += (_, _) => OpenNodeRelations(id);
        menu.Items.Add(relations);

        // 待确认节点(disc:)额外提供 确认/删除
        if (id.Contains(":disc:", StringComparison.Ordinal))
        {
            menu.Items.Add(new Separator());

            var confirm = new MenuItem { Header = "确认节点（登记为资产）" };
            confirm.Click += (_, _) => _viewModel.ConfirmNode(id);
            menu.Items.Add(confirm);

            var removeNode = new MenuItem { Header = "删除节点（清理发现边）" };
            removeNode.Click += (_, _) => _viewModel.DeleteDiscoveredNode(id);
            menu.Items.Add(removeNode);
        }

        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OpenNodeRelations(string nodeId)
    {
        var window = new NodeRelationsWindow(nodeId) { Owner = this };
        window.ShowDialog();
        _viewModel.Load();
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_panning)
        {
            var p = e.GetPosition(CanvasHost);
            _pan.X = _panStartX + (p.X - _panStart.X);
            _pan.Y = _panStartY + (p.Y - _panStart.Y);
            UpdateGrid();
            return;
        }

        if (!_dragging && !_resizing && !_linking)
        {
            UpdateCursor(e);
            return;
        }

        BeginFrameProbe();
        if (PerfLog.Enabled)
            _probeMoveEvents++;

        try
        {
            var point = e.GetPosition(GraphCanvas);

            if (_linking)
            {
                _viewModel.UpdateLink(point);
                return;
            }

            var dx = point.X - _last.X;
            var dy = point.Y - _last.Y;
            if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01)
                return;

            _last = point;

            if (_resizing && _handle != null)
            {
                var t0 = Stopwatch.GetTimestamp();
                _viewModel.ResizeSelected(_handle, point);
                _viewModel.TrackFrame(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                _dragMoved = true;
            }
            else if (_dragging && _dragId != null)
            {
                var t0 = Stopwatch.GetTimestamp();
                _viewModel.MoveNode(_dragId, point);
                _viewModel.TrackFrame(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                _dragMoved = true;
            }
        }
        catch (Exception ex)
        {
            ReportDragError(ex);
        }
    }

    /// <summary>根据指针位置切换鼠标样式（缩放手柄/节点/默认）。</summary>
    private void UpdateCursor(MouseEventArgs e)
    {
        if (EdgePanel.IsMouseOver)
        {
            CanvasHost.Cursor = Cursors.Arrow;
            return;
        }

        var point = e.GetPosition(GraphCanvas);
        if (_viewModel.HasSelection && _viewModel.HitHandle(point) is { } h)
        {
            CanvasHost.Cursor = h switch
            {
                "nw" or "se" => Cursors.SizeNWSE,
                "ne" or "sw" => Cursors.SizeNESW,
                "n" or "s" => Cursors.SizeNS,
                _ => Cursors.SizeWE
            };
            return;
        }

        if (_viewModel.HasSelection && _viewModel.HitEdgeHandle(point) is { } eh)
        {
            CanvasHost.Cursor = eh is "n" or "s" ? Cursors.SizeNS : Cursors.SizeWE;
            return;
        }

        if (_viewModel.HasSelection && _viewModel.HitPort(point) != null)
        {
            CanvasHost.Cursor = Cursors.Cross;
            return;
        }

        CanvasHost.Cursor = _viewModel.HitTest(point) != null ? Cursors.SizeAll : Cursors.Arrow;
    }

    /// <summary>开始帧间隔探针（仅 LITSSH_PERF=1；记录 CompositionTarget.Rendering 节奏）。</summary>
    private void BeginFrameProbe()
    {
        if (!PerfLog.Enabled || _renderProbe)
            return;
        _renderProbe = true;
        _renderCount = 0;
        _renderTotalMs = 0;
        _renderMaxMs = 0;
        _probeMoveEvents = 0;
        _renderLastTs = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnTargetRendering;
    }

    private void OnTargetRendering(object? sender, EventArgs e)
    {
        var delta = Stopwatch.GetElapsedTime(_renderLastTs).TotalMilliseconds;
        _renderLastTs = Stopwatch.GetTimestamp();
        _renderCount++;
        _renderTotalMs += delta;
        if (delta > _renderMaxMs)
            _renderMaxMs = delta;
    }

    /// <summary>结束探针并写一行帧节奏（帧数/平均/最大帧间隔 + 鼠标事件数 + 渲染层 tier：0=软件渲染）。</summary>
    private void EndFrameProbe(string kind)
    {
        if (!_renderProbe)
            return;
        _renderProbe = false;
        CompositionTarget.Rendering -= OnTargetRendering;
        var avg = _renderCount > 0 ? _renderTotalMs / _renderCount : 0;
        PerfLog.Write($"FrameProbe({kind}): frames={_renderCount} avg={avg:F1} ms max={_renderMaxMs:F1} ms events={_probeMoveEvents} tier={RenderCapability.Tier}");
    }

    private async void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        var draggedId = (_dragging || _resizing) ? _dragId : null;
        var dragMoved = _dragMoved;
        var wasResizing = _resizing;
        _dragMoved = false;

        try
        {
            if (_anchorDrag != null)
                _viewModel.EndAnchorDrag(_anchorDrag, e.GetPosition(GraphCanvas));
            else if (_linking)
                _viewModel.FinishLink(e.GetPosition(GraphCanvas));
        }
        catch (Exception ex)
        {
            ReportDragError(ex);
        }
        finally
        {
            var probeKind = wasResizing ? "resize" : _dragging ? "move" : "other";
            EndFrameProbe(probeKind);

            _dragging = false;
            _resizing = false;
            _linking = false;
            _panning = false;
            _anchorDrag = null;
            _dragId = null;
            _handle = null;
            CanvasHost.Cursor = Cursors.Arrow;

            if (CanvasHost.IsMouseCaptured)
                CanvasHost.ReleaseMouseCapture();
        }

        try
        {
            if (draggedId != null && dragMoved)
            {
                if (wasResizing)
                    _viewModel.ValidateResize(draggedId);
                else
                    await _viewModel.EndNodeDragAsync(draggedId);
            }
            else if (draggedId != null)
                _viewModel.CancelDrag();
            else if (wasResizing)
                _viewModel.CommitLayout();
        }
        catch (Exception ex)
        {
            ReportDragError(ex);
        }
        finally
        {
            // 落点回退/提交后，确保选中框与当前几何一致
            _viewModel.RefreshSelection();
            _viewModel.FlushDragPerf();
        }
    }

    // ---- 关系属性面板拖动 ----

    private void OnPanelHeaderDown(object sender, MouseButtonEventArgs e)
    {
        _panelDragging = true;
        _panelStart = e.GetPosition(OverlayCanvas);
        _panelStartX = Canvas.GetLeft(EdgePanel);
        _panelStartY = Canvas.GetTop(EdgePanel);
        if (double.IsNaN(_panelStartX)) _panelStartX = 0;
        if (double.IsNaN(_panelStartY)) _panelStartY = 0;
        EdgePanelHeader.CaptureMouse();
        e.Handled = true;
    }

    private void OnPanelHeaderMove(object sender, MouseEventArgs e)
    {
        if (!_panelDragging)
            return;

        var p = e.GetPosition(OverlayCanvas);
        Canvas.SetLeft(EdgePanel, Math.Max(0, _panelStartX + (p.X - _panelStart.X)));
        Canvas.SetTop(EdgePanel, Math.Max(0, _panelStartY + (p.Y - _panelStart.Y)));
    }

    private void OnPanelHeaderUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panelDragging)
            return;

        _panelDragging = false;
        EdgePanelHeader.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ReportDragError(Exception ex)
    {
        _dragging = false;
        _resizing = false;
        _linking = false;

        if (_errorShown)
            return;
        _errorShown = true;

        MessageBox.Show(
            $"操作出错（已终止本次操作）：\n\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
            "拓扑编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
