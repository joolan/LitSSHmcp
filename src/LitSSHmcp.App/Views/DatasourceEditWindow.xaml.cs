using System.Windows;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class DatasourceEditWindow : FluentWindow
{
    public DatasourceEditWindow()
    {
        InitializeComponent();
    }

    public string GetPassword() => (PasswordRow.FieldContent as System.Windows.Controls.PasswordBox)?.Password ?? string.Empty;

    public void SetViewOnly(bool viewOnly)
    {
        if (!viewOnly)
            return;
        Title = "查看数据源";
        WindowTitleBar.Title = "查看数据源";
    }
}
