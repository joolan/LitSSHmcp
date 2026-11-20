using System.Windows;

namespace LitSSHmcp.App.Views;

public enum RemoteFileConflictDecision
{
    Cancel,
    Overwrite,
    Reload
}

public partial class RemoteFileConflictWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly string _leftText;
    private readonly string _rightText;
    private readonly string _path;

    internal RemoteFileConflictWindow(string path, string leftText, string rightText)
    {
        InitializeComponent();
        _path = path;
        _leftText = leftText;
        _rightText = rightText;
        PathText.Text = path;
    }

    public RemoteFileConflictDecision Decision { get; private set; } = RemoteFileConflictDecision.Cancel;

    public static RemoteFileConflictDecision Show(
        Window? owner, string path, string leftText, string rightText)
    {
        var window = new RemoteFileConflictWindow(path, leftText, rightText) { Owner = owner };
        window.ShowDialog();
        return window.Decision;
    }

    private void OnDiff(object sender, RoutedEventArgs e)
    {
        RemoteFileDiffWindow.Show(
            this,
            "保存冲突差异 - " + System.IO.Path.GetFileName(_path),
            "编辑器当前内容",
            "服务器当前版本",
            _leftText,
            _rightText);
    }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        Decision = RemoteFileConflictDecision.Reload;
        DialogResult = true;
        Close();
    }

    private void OnOverwrite(object sender, RoutedEventArgs e)
    {
        Decision = RemoteFileConflictDecision.Overwrite;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Decision = RemoteFileConflictDecision.Cancel;
        DialogResult = false;
        Close();
    }
}
