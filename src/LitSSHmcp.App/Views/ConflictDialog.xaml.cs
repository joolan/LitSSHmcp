using System.Windows;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

/// <summary>同名冲突处理选择。</summary>
public enum ConflictChoice
{
    Overwrite,
    OverwriteAll,
    Skip,
    SkipAll,
    Cancel
}

/// <summary>上传/下载遇到同名项时的冲突选择对话框。</summary>
public partial class ConflictDialog : FluentWindow
{
    private ConflictChoice _choice = ConflictChoice.Cancel;

    public ConflictDialog(string targetName)
    {
        InitializeComponent();
        NameText.Text = targetName;
    }

    public static ConflictChoice Ask(Window? owner, string targetName)
    {
        var dialog = new ConflictDialog(targetName);
        if (owner is not null)
            dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog._choice;
    }

    private void OnOverwrite(object sender, RoutedEventArgs e) => Close(ConflictChoice.Overwrite);
    private void OnOverwriteAll(object sender, RoutedEventArgs e) => Close(ConflictChoice.OverwriteAll);
    private void OnSkip(object sender, RoutedEventArgs e) => Close(ConflictChoice.Skip);
    private void OnSkipAll(object sender, RoutedEventArgs e) => Close(ConflictChoice.SkipAll);
    private void OnCancel(object sender, RoutedEventArgs e) => Close(ConflictChoice.Cancel);

    private void Close(ConflictChoice choice)
    {
        _choice = choice;
        DialogResult = choice != ConflictChoice.Cancel;
        Close();
    }
}
