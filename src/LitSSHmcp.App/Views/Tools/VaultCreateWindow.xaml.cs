using System.Windows;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views.Tools;

public partial class VaultCreateWindow : FluentWindow
{
    public VaultCreateWindow()
        : this("新建加密文件柜", string.Empty, "文件柜名称",
            "主密码无法找回。请确认已妥善保存；文件柜中的文件只在本机保持加密。")
    {
    }

    public VaultCreateWindow(string title, string defaultName, string nameLabel, string hint)
    {
        InitializeComponent();
        Title = title;
        TitleBarControl.Title = title;
        NameLabel.Text = nameLabel;
        HintText.Text = hint;
        NameBox.Text = defaultName;
        Loaded += (_, _) => NameBox.Focus();
    }

    public string VaultName => NameBox.Text.Trim();
    public string Password => PasswordBoxInput.Password;

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(VaultName))
        {
            ErrorText.Text = "请填写名称。";
            return;
        }
        if (Password.Length < 8)
        {
            ErrorText.Text = "主密码至少 8 位。";
            return;
        }
        if (!string.Equals(Password, ConfirmBox.Password, StringComparison.Ordinal))
        {
            ErrorText.Text = "两次输入的主密码不一致。";
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
