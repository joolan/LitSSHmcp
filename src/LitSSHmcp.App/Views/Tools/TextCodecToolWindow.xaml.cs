using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LitSSHmcp.App.Services.Toolbox;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views.Tools;

public partial class TextCodecToolWindow : FluentWindow
{
    private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);
    private readonly DispatcherTimer _timestampTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public TextCodecToolWindow()
    {
        InitializeComponent();
        ModeBox.ItemsSource = TextCodecService.Modes;
        ModeBox.SelectedIndex = 0;
        _timestampTimer.Tick += (_, _) => UpdateTimestampInfo();
        Closed += (_, _) => _timestampTimer.Stop();
        Loaded += (_, _) =>
        {
            UpdateModeHint();
            UpdateTimestampPanel();
            UpdateStats();
            InputBox.Focus();
        };
    }

    private TextCodecChoice? CurrentMode => ModeBox.SelectedItem as TextCodecChoice;

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateModeHint();
        UpdateTimestampPanel();
        StatusText.Text = string.Empty;
    }

    private void OnTransform(object sender, RoutedEventArgs e)
    {
        if (CurrentMode is not { } mode)
            return;

        try
        {
            OutputBox.Text = TextCodecService.Transform(mode.Mode, InputBox.Text);
            UpdateStats();
            StatusText.Text = "转换完成。";
        }
        catch (Exception ex)
        {
            OutputBox.Text = string.Empty;
            UpdateStats();
            StatusText.Text = "转换失败：" + ex.Message;
        }
    }

    private void OnSwap(object sender, RoutedEventArgs e)
    {
        if (OutputBox.Text.Length == 0)
            return;

        InputBox.Text = OutputBox.Text;
        OutputBox.Clear();
        UpdateStats();
        StatusText.Text = "已交换输入和输出。";
        InputBox.Focus();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (OutputBox.Text.Length == 0)
            return;

        try
        {
            Clipboard.SetText(OutputBox.Text);
            StatusText.Text = "已复制输出。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        InputBox.Clear();
        OutputBox.Clear();
        UpdateStats();
        StatusText.Text = string.Empty;
        InputBox.Focus();
    }

    private void OnImportFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入文本文件",
            Filter = "文本文件 (*.txt;*.json;*.xml;*.log;*.csv)|*.txt;*.json;*.xml;*.log;*.csv|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            InputBox.Text = File.ReadAllText(dialog.FileName);
            StatusText.Text = "已导入：" + Path.GetFileName(dialog.FileName);
            InputBox.Focus();
        }
        catch (Exception ex)
        {
            StatusText.Text = "导入失败：" + ex.Message;
        }
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e) => UpdateStats();

    private void OnInputDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            || e.Data.GetDataPresent(DataFormats.UnicodeText)
            || e.Data.GetDataPresent(DataFormats.Text)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnInputDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)
                && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                InputBox.Text = string.Join(Environment.NewLine, files.Select(File.ReadAllText));
                StatusText.Text = $"已导入 {files.Length} 个文件。";
            }
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                InputBox.Text = (string?)e.Data.GetData(DataFormats.UnicodeText) ?? string.Empty;
                StatusText.Text = "已导入文本。";
            }
            else if (e.Data.GetDataPresent(DataFormats.Text))
            {
                InputBox.Text = (string?)e.Data.GetData(DataFormats.Text) ?? string.Empty;
                StatusText.Text = "已导入文本。";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "导入失败：" + ex.Message;
        }
        finally
        {
            e.Handled = true;
        }
    }

    private void UpdateTimestampPanel()
    {
        var isTimestamp = CurrentMode?.Mode == TextCodecMode.Timestamp;
        TimestampPanel.Visibility = isTimestamp ? Visibility.Visible : Visibility.Collapsed;
        if (!isTimestamp)
        {
            _timestampTimer.Stop();
            return;
        }

        UpdateTimestampInfo();
        _timestampTimer.Start();
    }

    private void UpdateTimestampInfo()
    {
        var now = DateTimeOffset.UtcNow;
        var beijing = now.ToOffset(BeijingOffset);
        CurrentTimestampText.Text =
            $"当前 Unix：{now.ToUnixTimeSeconds()} 秒 / {now.ToUnixTimeMilliseconds()} 毫秒{Environment.NewLine}" +
            $"北京时间：{beijing:yyyy-MM-dd HH:mm:ss}（UTC+8）{Environment.NewLine}" +
            $"UTC：{now:yyyy-MM-dd HH:mm:ss}";
    }

    private void OnCopyTimestampSeconds(object sender, RoutedEventArgs e)
        => CopyToClipboard(DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), "Unix 秒");

    private void OnCopyTimestampMilliseconds(object sender, RoutedEventArgs e)
        => CopyToClipboard(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(), "Unix 毫秒");

    private void OnCopyBeijingTime(object sender, RoutedEventArgs e)
        => CopyToClipboard(DateTimeOffset.UtcNow.ToOffset(BeijingOffset).ToString("yyyy-MM-dd HH:mm:ss"), "北京时间");

    private void CopyToClipboard(string text, string label)
    {
        try
        {
            Clipboard.SetText(text);
            StatusText.Text = $"已复制{label}。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }

    private void UpdateModeHint() => ModeHint.Text = CurrentMode?.Hint ?? string.Empty;

    private void UpdateStats()
    {
        var input = TextCodecService.Measure(InputBox.Text);
        var output = TextCodecService.Measure(OutputBox.Text);
        InputStatsText.Text = $"{input.Characters} 字符 · {input.Lines} 行 · {input.Utf8Bytes} 字节";
        OutputStatsText.Text = output.Characters == 0
            ? string.Empty
            : $"{output.Characters} 字符 · {output.Lines} 行 · {output.Utf8Bytes} 字节";
    }
}
