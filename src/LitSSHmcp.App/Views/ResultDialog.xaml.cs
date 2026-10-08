using System.Windows;
using System.Windows.Media;
using LitSSHmcp.App.Services;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

/// <summary>模态结果弹窗：正文可选中/复制（用于展示多行命令、安装提示等）。</summary>
public partial class ResultDialog : FluentWindow
{
    public ResultDialog(string title, string headline, string message, bool isError)
    {
        InitializeComponent();
        Title = title;
        Bar.Title = title;
        Headline.Text = headline;
        Body.Text = message;
        Glyph.Symbol = isError ? SymbolRegular.ErrorCircle24 : SymbolRegular.CheckmarkCircle24;
        Glyph.Foreground = isError
            ? (Brush)FindResource("AppDangerBrush")
            : (Brush)FindResource("AppAccentBrush");
        WindowLayout.Attach(this, "result-dialog");
    }

    public static void Show(Window? owner, string title, string headline, string message, bool isError)
    {
        var dialog = new ResultDialog(title, headline, message, isError) { Owner = owner };
        dialog.ShowDialog();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Body.Text ?? string.Empty); } catch { /* 剪贴板偶发占用 */ }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
