using System.Globalization;
using System.Windows;
using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.Views;

/// <summary>日志「终端操作」参数对话框：关键字 +（可选）时间范围 + 最多显示行数。</summary>
public partial class LogSearchDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly bool _rangeMode;

    private LogSearchDialog(string title, bool rangeMode)
    {
        InitializeComponent();
        _rangeMode = rangeMode;
        Title = title;
        TitleBar.Title = title;
        RangePanel.Visibility = rangeMode ? Visibility.Visible : Visibility.Collapsed;
        TimeNote.Text = rangeMode
            ? "时间格式：yyyy-MM-dd HH:mm:ss（含起止；支持行首 ISO、yyyy/MM/dd 以及 nginx/Apache 的 [dd/Mon/yyyy:HH:mm:ss，请与远端时区保持一致）。最多扫描最近 200000 行；syslog 格式请改用「最新 N 条」或「关键字上下文」。"
            : "时间范围：最近 1 小时（以远端当前时间为准；支持行首 ISO、yyyy/MM/dd 以及 nginx/Apache 时间戳，最多扫描最近 200000 行；syslog 格式请改用「最新 N 条」或「关键字上下文」）。";

        var now = DateTime.Now;
        StartBox.Text = now.AddHours(-1).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        EndBox.Text = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        MaxLinesBox.Text = LogTerminalCommandBuilder.DefaultMaxLines.ToString(CultureInfo.InvariantCulture);
        Loaded += (_, _) => { KeywordBox.Focus(); KeywordBox.SelectAll(); };
    }

    public string Keyword => KeywordBox.Text.Trim();
    public string StartText => StartBox.Text.Trim();
    public string EndText => EndBox.Text.Trim();
    public int MaxLines => int.TryParse(MaxLinesBox.Text.Trim(), out var n)
        ? LogTerminalCommandBuilder.ClampLines(n)
        : LogTerminalCommandBuilder.DefaultMaxLines;

    public static LogSearchDialog? LastHour(Window? owner)
        => Show(owner, new LogSearchDialog("搜索最近 1 小时日志", rangeMode: false));

    public static LogSearchDialog? Range(Window? owner)
        => Show(owner, new LogSearchDialog("搜索指定时间范围", rangeMode: true));

    private static LogSearchDialog? Show(Window? owner, LogSearchDialog dialog)
    {
        if (owner is not null)
            dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_rangeMode)
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
            StartBox.Text = start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            EndBox.Text = end.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        if (!int.TryParse(MaxLinesBox.Text.Trim(), out var lines) ||
            lines < 1 || lines > LogTerminalCommandBuilder.MaxMaxLines)
        {
            MessageBox.Show($"最多显示行数需为 1-{LogTerminalCommandBuilder.MaxMaxLines} 的整数。", Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }

    private static bool TryNormalizeTime(string text, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return DateTime.TryParse(text.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.None, out value);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
