using System.Windows;

namespace LitSSHmcp.App.Views;

public partial class TextInputDialog : Wpf.Ui.Controls.FluentWindow
{
    public TextInputDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initial ?? string.Empty;
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    public string Value => ValueBox.Text;

    /// <summary>弹出输入框；用户取消返回 null。</summary>
    public static string? Prompt(Window? owner, string title, string prompt, string initial = "")
    {
        var dialog = new TextInputDialog(title, prompt, initial);
        if (owner is not null)
            dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
