using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class ImagePreviewWindow : FluentWindow
{
    private double _scale = 1;

    public ImagePreviewWindow(string path)
    {
        InitializeComponent();

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            Preview.Source = bitmap;
            Preview.LayoutTransform = new ScaleTransform(1, 1);
            Caption.Text = $"{Path.GetFileName(path)}  ·  {bitmap.PixelWidth}×{bitmap.PixelHeight}  ·  滚轮缩放，拖拽滚动条平移";
            Title = Path.GetFileName(path);
        }
        catch (Exception ex)
        {
            Caption.Text = "无法加载图片: " + ex.Message;
        }
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (Preview.Source is null)
            return;

        _scale = Math.Clamp(_scale + (e.Delta > 0 ? 0.1 : -0.1), 0.1, 8);
        if (Preview.LayoutTransform is ScaleTransform transform)
        {
            transform.ScaleX = _scale;
            transform.ScaleY = _scale;
        }
        e.Handled = true;
    }
}
