using System.Windows;
using LitSSHmcp.App.Services.DatasourceAi;

namespace LitSSHmcp.App.Views;

public partial class DatasourceAiSqlConfirmWindow : Wpf.Ui.Controls.FluentWindow
{
    internal DatasourceAiSqlConfirmWindow(string sql, string target, string reason)
    {
        InitializeComponent();
        SqlBox.Text = sql;
        TargetText.Text = target;
        ReasonText.Text = string.IsNullOrWhiteSpace(reason)
            ? "每次 AI 生成的只读查询都需要确认。"
            : reason;
    }

    public DatasourceAiSqlDecision Decision { get; private set; } = DatasourceAiSqlDecision.Cancel;

    public static DatasourceAiSqlDecision Show(
        Window? owner, string sql, string target, string reason = "")
    {
        var window = new DatasourceAiSqlConfirmWindow(sql, target, reason)
        {
            Owner = owner,
            Topmost = false
        };
        window.ShowDialog();
        return window.Decision;
    }

    private void OnCopySql(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(SqlBox.Text))
                Clipboard.SetText(SqlBox.Text);
        }
        catch
        {
            // 剪贴板被占用时忽略
        }
    }

    private void OnInsert(object sender, RoutedEventArgs e)
    {
        Decision = DatasourceAiSqlDecision.Insert;
        DialogResult = true;
        Close();
    }

    private void OnExecute(object sender, RoutedEventArgs e)
    {
        Decision = DatasourceAiSqlDecision.Execute;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Decision = DatasourceAiSqlDecision.Cancel;
        DialogResult = false;
        Close();
    }
}
