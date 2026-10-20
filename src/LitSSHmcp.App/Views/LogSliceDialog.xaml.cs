using System.Globalization;
using System.Windows;

namespace LitSSHmcp.App.Views;

/// <summary>日志切片参数：关键字、可选时间范围、扫描行数与远端输出文件。</summary>
public partial class LogSliceDialog : Wpf.Ui.Controls.FluentWindow
{
    private const int MaxScanLines = 100_000_000;
    private readonly string _sourcePath;

    private LogSliceDialog(string sourcePath, string defaultOutput)
    {
        InitializeComponent();
        _sourcePath = sourcePath;
        SourceText.Text = sourcePath;
        OutputBox.Text = defaultOutput;

        var now = DateTime.Now;
        StartBox.Text = now.AddHours(-1).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        EndBox.Text = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Loaded += (_, _) => { KeywordBox.Focus(); KeywordBox.SelectAll(); };
    }

    public string Keyword { get; private set; } = string.Empty;
    public bool FilterByTime { get; private set; }
    public string StartText { get; private set; } = string.Empty;
    public string EndText { get; private set; } = string.Empty;
    public int ScanLines { get; private set; }
    public string OutputPath { get; private set; } = string.Empty;

    public static LogSliceDialog? Show(Window? owner, string sourcePath, string defaultOutput)
    {
        var dialog = new LogSliceDialog(sourcePath, defaultOutput);
        if (owner is not null)
            dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog : null;
    }

    private void OnTimeCheckChanged(object sender, RoutedEventArgs e)
    {
        if (RangePanel is null)
            return;
        var isChecked = sender is System.Windows.Controls.CheckBox { IsChecked: true };
        RangePanel.Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLimitCheckChanged(object sender, RoutedEventArgs e)
    {
        if (ScanLinesBox is null)
            return;
        ScanLinesBox.IsEnabled = sender is System.Windows.Controls.CheckBox { IsChecked: true };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var filterByTime = TimeCheck.IsChecked == true;
        if (filterByTime)
        {
            if (!TryNormalizeTime(StartBox.Text, out var start) || !TryNormalizeTime(EndBox.Text, out var end))
            {
                MessageBox.Show("时间格式无效，请使用 yyyy-MM-dd HH:mm:ss。", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (start > end)
            {
                MessageBox.Show("开始时间不能晚于结束时间。", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            StartText = start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            EndText = end.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        var scanLines = 0;
        if (LimitCheck.IsChecked == true)
        {
            if (!int.TryParse(ScanLinesBox.Text.Trim(), out scanLines) ||
                scanLines < 1 || scanLines > MaxScanLines)
            {
                MessageBox.Show($"扫描行数需为 1-{MaxScanLines} 的整数。", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var output = OutputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(output) || !output.StartsWith('/') || output.EndsWith('/'))
        {
            MessageBox.Show("输出文件请填写远端绝对路径（以 / 开头，并包含文件名）。", Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.Equals(output.TrimEnd('/'), _sourcePath.TrimEnd('/'), StringComparison.Ordinal))
        {
            MessageBox.Show("输出文件不能与源日志相同。", Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var keyword = KeywordBox.Text.Trim();
        if (!filterByTime && string.IsNullOrWhiteSpace(keyword))
        {
            var confirm = MessageBox.Show(
                "未设置关键字且未启用时间过滤，将复制整个日志文件。继续？", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
                return;
        }

        Keyword = keyword;
        FilterByTime = filterByTime;
        ScanLines = scanLines;
        OutputPath = output;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static bool TryNormalizeTime(string text, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return DateTime.TryParse(text.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.None, out value);
    }
}
