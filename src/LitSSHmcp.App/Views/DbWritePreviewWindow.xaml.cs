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
        PreviewSummary.Text = info.Describe();
        SampleGrid.ItemsSource = info.Sample;
        SampleBorder.Visibility = info.Sample is null ? Visibility.Collapsed : Visibility.Visible;

        WindowLayout.Attach(this, "dbwritepreview");
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
}
