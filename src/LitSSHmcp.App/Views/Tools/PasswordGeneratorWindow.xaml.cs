using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Toolbox;
using Wpf.Ui.Controls;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;

namespace LitSSHmcp.App.Views.Tools;

public partial class PasswordGeneratorWindow : FluentWindow
{
    private GeneratedPassword? _current;

    public PasswordGeneratorWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Generate();
        LengthText.Text = ((int)LengthSlider.Value).ToString();
    }

    private void OnLengthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LengthText is not null)
            LengthText.Text = ((int)LengthSlider.Value).ToString();
    }

    private void OnGenerate(object sender, RoutedEventArgs e) => Generate();

    private void Generate()
    {
        try
        {
            var options = new PasswordGeneratorOptions
            {
                Length = (int)LengthSlider.Value,
                IncludeUpper = UpperCheck.IsChecked == true,
                IncludeLower = LowerCheck.IsChecked == true,
                IncludeDigits = DigitsCheck.IsChecked == true,
                IncludeSymbols = SymbolsCheck.IsChecked == true,
                ExcludeAmbiguous = AmbiguousCheck.IsChecked == true
            };

            _current = RandomPasswordGenerator.Generate(options);
            PasswordBox.Text = _current.Value;
            StrengthText.Text = _current.StrengthText;
            StrengthBar.Value = Math.Clamp(_current.EntropyBits / 128.0 * 100, 0, 100);
            DetailText.Text = $"约 {_current.EntropyBits:0} bit 熵 · 长度 {options.Length}";
            SetStrengthBrush(_current.StrengthText);
            StatusText.Text = "已生成新密码。";
        }
        catch (Exception ex)
        {
            PasswordBox.Text = string.Empty;
            StrengthText.Text = "未生成";
            StrengthBar.Value = 0;
            DetailText.Text = string.Empty;
            StatusText.Text = ex.Message;
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("复制密码", CopyAsync);

    private async Task CopyAsync()
    {
        if (_current is null)
            return;

        const int maxAttempts = 10;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(_current.Value, true);
                StatusText.Text = attempt == 1 ? "已复制到剪贴板。" : $"已复制到剪贴板（重试 {attempt} 次）。";
                return;
            }
            catch (COMException)
            {
                if (attempt < maxAttempts)
                    await Task.Delay(40);
            }
            catch (Exception ex)
            {
                StatusText.Text = "复制失败：" + ex.Message;
                return;
            }
        }

        StatusText.Text = "复制失败：剪贴板被其他程序占用，请稍后再试。";
    }

    private void SetStrengthBrush(string strength)
    {
        var key = strength switch
        {
            "很强" or "强" => "AppSuccessBrush",
            "中等" => "AppWarningBrush",
            _ => "AppDangerBrush"
        };
        if (TryFindResource(key) is Brush brush)
        {
            StrengthText.Foreground = brush;
            StrengthBar.Foreground = brush;
        }
    }
}
