using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views.Tools;

public partial class VaultImagePreviewWindow : FluentWindow
{
    public VaultImagePreviewWindow(string fileName, byte[] content)
    {
        InitializeComponent();
        Title = "图片预览 - " + fileName;
        FileNameText.Text = fileName;
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            PreviewImage.Source = bitmap;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }
}
