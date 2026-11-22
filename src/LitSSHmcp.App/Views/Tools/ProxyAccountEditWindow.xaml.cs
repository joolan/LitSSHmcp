using System.Windows;
using System.Windows.Media;
using LitSSHmcp.App.Services.Toolbox;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views.Tools;

public sealed record ProxyAccountEditResult(
    string Username,
    string? Password,
    string Note,
    bool Enabled);

public partial class ProxyAccountEditWindow : FluentWindow
{
    private bool _isNew = true;
    private string _generatedPassword = string.Empty;

    public ProxyAccountEditWindow()
    {
        InitializeComponent();
    }

    public ProxyAccountEditResult? Result { get; private set; }

    public void Configure(bool isNew, string username, string note, bool enabled)
    {
        _isNew = isNew;
        Title = isNew ? "新增代理账号" : "编辑代理账号";
        UsernameBox.Text = username;
        NoteBox.Text = note;
        EnabledCheck.IsChecked = enabled;
        PasswordLabel.Text = isNew ? "密码（至少 8 位）" : "新密码（留空表示不修改）";
        HintText.Text = isNew
            ? "密码只保存 PBKDF2 哈希，之后无法查看；请使用随机密码并立即保存。"
            : "用户名可修改；密码留空表示保持原密码不变。";
        if (isNew)
            UsernameBox.Focus();
    }

    private void OnGeneratePassword(object sender, RoutedEventArgs e)
    {
        var generated = RandomPasswordGenerator.Generate(new PasswordGeneratorOptions
        {
            Length = 24,
            IncludeUpper = true,
            IncludeLower = true,
            IncludeDigits = true,
            IncludeSymbols = true,
            SafeSymbolsOnly = true,
            ExcludeAmbiguous = true
        });
        _generatedPassword = generated.Value;
        PasswordBox.Password = generated.Value;
        ConfirmPasswordBox.Password = generated.Value;
        GeneratedPasswordBox.Text = generated.Value;
        GeneratedPanel.Visibility = Visibility.Visible;
        StatusText.Foreground = TryFindResource("AppSuccessBrush") as Brush;
        StatusText.Text = $"已生成随机密码（{generated.StrengthText}，约 {generated.EntropyBits:0} bit）。";
    }

    private void OnCopyGeneratedPassword(object sender, RoutedEventArgs e)
    {
        var value = string.IsNullOrEmpty(_generatedPassword)
            ? PasswordBox.Password
            : _generatedPassword;
        if (string.IsNullOrEmpty(value))
        {
            StatusText.Text = "请先生成或输入密码。";
            return;
        }

        try
        {
            Clipboard.SetText(value);
            StatusText.Text = "已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var username = UsernameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            StatusText.Text = "用户名不能为空。";
            return;
        }

        var password = PasswordBox.Password;
        var confirm = ConfirmPasswordBox.Password;
        string? resultPassword = null;

        if (_isNew || !string.IsNullOrEmpty(password) || !string.IsNullOrEmpty(confirm))
        {
            if (password.Length < 8)
            {
                StatusText.Text = "密码至少 8 位。";
                return;
            }
            if (!string.Equals(password, confirm, StringComparison.Ordinal))
            {
                StatusText.Text = "两次输入的密码不一致。";
                return;
            }
            resultPassword = password;
        }
        else if (!_isNew)
        {
            resultPassword = null;
        }

        Result = new ProxyAccountEditResult(username, resultPassword,
            NoteBox.Text.Trim(), EnabledCheck.IsChecked == true);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
