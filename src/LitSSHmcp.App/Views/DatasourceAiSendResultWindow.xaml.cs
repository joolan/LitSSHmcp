using System.Windows;
using LitSSHmcp.App.Services.DatasourceAi;

namespace LitSSHmcp.App.Views;

public partial class DatasourceAiSendResultWindow : Wpf.Ui.Controls.FluentWindow
{
    internal DatasourceAiSendResultWindow(int totalRows, int selectedRows, string? sql)
    {
        InitializeComponent();
        SummaryText.Text = $"当前结果 {totalRows:N0} 行，已选中 {selectedRows:N0} 行。" +
                           (string.IsNullOrWhiteSpace(sql) ? string.Empty : " 将同时发送当前 SQL。");
        SelectedRowsHint.Text = selectedRows > 0
            ? $"只发送当前选中的 {selectedRows:N0} 行。"
            : "当前没有选中行；可在结果表中按住 Ctrl/Shift 选择多行。";
        SelectedRowsOption.IsEnabled = selectedRows > 0;
    }

    public DatasourceAiResultRange Range { get; private set; } = DatasourceAiResultRange.First20;

    private void OnSend(object sender, RoutedEventArgs e)
    {
        if (SelectedRowsOption.IsChecked == true)
            Range = DatasourceAiResultRange.SelectedRows;
        else if (CurrentResultOption.IsChecked == true)
            Range = DatasourceAiResultRange.CurrentResult;
        else if (MetadataOnlyOption.IsChecked == true)
            Range = DatasourceAiResultRange.MetadataOnly;
        else
            Range = DatasourceAiResultRange.First20;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
