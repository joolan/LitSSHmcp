using System.Windows;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class DbWritePreviewWindow : FluentWindow
{
    public DbWritePreviewWindow(WritePreviewInfo info)
    {
        InitializeComponent();

        SqlBox.Text = info.Sql;
        TargetText.Text = BuildTargetText(info);
        OperationText.Text = string.IsNullOrWhiteSpace(info.OperationLabel) ? "写操作" : info.OperationLabel!;
        ImpactText.Text = info.AffectedRows is long rows
            ? $"预计影响 {rows} 行"
            : info.Sample is not null ? "已获取受影响样本" : "无法预估影响行数";
        SensitiveBadge.Visibility = info.IsSensitive ? Visibility.Visible : Visibility.Collapsed;
        NoteText.Text = info.Note ?? string.Empty;
        NoteText.Visibility = string.IsNullOrWhiteSpace(info.Note) ? Visibility.Collapsed : Visibility.Visible;
        SqlExpander.Header = $"将要执行的 SQL（{info.Sql.Length} 字符）";

        SampleGrid.ItemsSource = info.Sample;
        if (info.Sample is null)
        {
            SampleExpander.Visibility = Visibility.Collapsed;
        }
        else
        {
            SampleExpander.Visibility = Visibility.Visible;
            SampleExpander.Header = $"受影响样本行（最多 100 行，{info.Sample.Count} 行）";
        }

        WindowLayout.Attach(this, "dbwritepreview");
    }

    private static string BuildTargetText(WritePreviewInfo info)
    {
        var name = string.IsNullOrWhiteSpace(info.DataSourceName) ? "数据库" : info.DataSourceName!;
        return string.IsNullOrWhiteSpace(info.DatabaseName) ? name : $"{name} · {info.DatabaseName}";
    }

    /// <summary>显示预览确认框；返回 true=用户确认执行。</summary>
    public static bool Show(Window? owner, WritePreviewInfo info)
    {
        var dialog = new DbWritePreviewWindow(info);
        if (owner is not null && owner.IsLoaded)
            dialog.Owner = owner;
        return dialog.ShowDialog() == true;
    }

    private void OnExecute(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnCopySql(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(SqlBox.Text); } catch { /* ignore */ }
    }
}
