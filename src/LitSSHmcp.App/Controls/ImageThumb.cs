using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.Controls;

/// <summary>
/// 图片缩略图：按 <see cref="SourcePath"/> 加载并按 UniformToFill 绘制；加载失败/文件被删则绘制“图片已删除”占位。
/// </summary>
public sealed class ImageThumb : FrameworkElement
{
    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath), typeof(string), typeof(ImageThumb),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSourceChanged));

    private BitmapSource? _bitmap;

    public string? SourcePath
    {
        get => (string?)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    public ImageThumb()
    {
        Loaded += (_, _) => ThemeService.ThemeChanged += InvalidateVisual;
        Unloaded += (_, _) => ThemeService.ThemeChanged -= InvalidateVisual;
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ImageThumb thumb)
            thumb.LoadBitmap();
    }

    private void LoadBitmap()
    {
        _bitmap = null;
        var path = SourcePath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.EndInit();
                bmp.Freeze();
                _bitmap = bmp;
            }
            catch
            {
                _bitmap = null;
            }
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        var rect = new Rect(0, 0, w, h);
        var border = Application.Current?.TryFindResource("AppBorderBrush") as Brush
                     ?? new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        var surface = Application.Current?.TryFindResource("AppSurfaceAltBrush") as Brush
                      ?? new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
        var text = Application.Current?.TryFindResource("AppTextSecondaryBrush") as Brush ?? Brushes.Gray;

        if (_bitmap is not null)
        {
            var scale = Math.Max(w / _bitmap.PixelWidth, h / _bitmap.PixelHeight);
            dc.PushClip(new RectangleGeometry(rect));
            dc.DrawImage(_bitmap, new Rect(
                (w - _bitmap.PixelWidth * scale) / 2,
                (h - _bitmap.PixelHeight * scale) / 2,
                _bitmap.PixelWidth * scale,
                _bitmap.PixelHeight * scale));
            dc.Pop();
            return;
        }

        dc.DrawRectangle(surface, new Pen(border, 1), rect);
        var label = new FormattedText("图片已删除", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei"), Math.Max(9, Math.Min(11, w / 10)), text,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(label, new Point(Math.Max(0, (w - label.Width) / 2), Math.Max(0, (h - label.Height) / 2)));
    }
}
