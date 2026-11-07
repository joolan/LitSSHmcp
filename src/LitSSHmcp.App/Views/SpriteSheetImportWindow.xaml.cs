using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using LitSSHmcp.App.Services.Pet;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class SpriteSheetImportWindow : FluentWindow
{
    private const int MaxFrames = 300;
    private const int MaxFrameSize = 1024;
    private readonly int _delayMs;
    private BitmapSource? _source;

    public SpriteSheetImportWindow(int delayMs = 100)
    {
        InitializeComponent();
        _delayMs = Math.Clamp(delayMs, 10, 5000);
    }

    public IReadOnlyList<PetAssetDraftFrame>? Result { get; private set; }

    private void OnChooseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择精灵图",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(dialog.FileName);
            bitmap.EndInit();
            bitmap.Freeze();
            _source = bitmap;
            PathBox.Text = dialog.FileName;
            PreviewImage.Source = bitmap;
            EmptyHint.Visibility = Visibility.Collapsed;
            StatusText.Text = $"{bitmap.PixelWidth}×{bitmap.PixelHeight}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取图片失败：" + ex.Message;
        }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (_source is null)
        {
            StatusText.Text = "请先选择精灵图。";
            return;
        }
        if (!int.TryParse(ColumnsBox.Text, out var columns) || columns < 1 || columns > 100 ||
            !int.TryParse(RowsBox.Text, out var rows) || rows < 1 || rows > 100)
        {
            StatusText.Text = "列数和行数必须在 1 到 100 之间。";
            return;
        }
        if (columns * rows > MaxFrames)
        {
            StatusText.Text = $"一次最多导入 {MaxFrames} 帧。";
            return;
        }
        if (!TryReadSize(FrameWidthBox.Text, out var frameWidth) ||
            !TryReadSize(FrameHeightBox.Text, out var frameHeight))
        {
            StatusText.Text = "帧宽高必须是非负整数。";
            return;
        }
        if (frameWidth == 0)
            frameWidth = _source.PixelWidth / columns;
        if (frameHeight == 0)
            frameHeight = _source.PixelHeight / rows;
        if (frameWidth <= 0 || frameHeight <= 0 ||
            frameWidth > MaxFrameSize || frameHeight > MaxFrameSize ||
            frameWidth * columns > _source.PixelWidth ||
            frameHeight * rows > _source.PixelHeight)
        {
            StatusText.Text = "帧尺寸无效，或切分范围超出图片。";
            return;
        }

        var frames = new List<PetAssetDraftFrame>(columns * rows);
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var crop = new CroppedBitmap(_source,
                    new Int32Rect(column * frameWidth, row * frameHeight, frameWidth, frameHeight));
                crop.Freeze();
                frames.Add(new PetAssetDraftFrame
                {
                    Image = crop,
                    DelayMs = _delayMs
                });
            }
        }
        Result = frames;
        DialogResult = true;
    }

    private static bool TryReadSize(string text, out int value)
        => int.TryParse(text, out value) && value >= 0;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
