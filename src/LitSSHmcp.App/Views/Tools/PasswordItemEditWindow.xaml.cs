using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LitSSHmcp.App.Services.Toolbox;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views.Tools;

public partial class PasswordItemEditWindow : FluentWindow
{
    private readonly PasswordItem _item;
    private readonly bool _isNew;
    private bool _syncingPassword;

    public PasswordItemEditWindow(PasswordItem? item = null)
    {
        InitializeComponent();
        _isNew = item is null;
        _item = item?.Clone() ?? new PasswordItem();
        TitleBarControl.Title = _isNew ? "新增账号条目" : "编辑账号条目";
        Title = TitleBarControl.Title;

        TypeBox.ItemsSource = new[]
        {
            PasswordItemTypeText.For(PasswordItemType.Login),
            PasswordItemTypeText.For(PasswordItemType.App),
            PasswordItemTypeText.For(PasswordItemType.Email),
            PasswordItemTypeText.For(PasswordItemType.Bank),
            PasswordItemTypeText.For(PasswordItemType.ApiKey),
            PasswordItemTypeText.For(PasswordItemType.SecureNote)
        };
        TypeBox.SelectedIndex = Math.Max(0, (int)_item.Type);

        TitleBox.Text = _item.Title;
        GroupBox.Text = _item.Group;
        UsernameBox.Text = _item.Username;
        PasswordBoxInput.Password = _item.Password;
        PasswordVisibleBox.Text = _item.Password;
        UrlBox.Text = _item.Url;
        PhoneBox.Text = _item.SecurityPhone;
        EmailBox.Text = _item.RecoveryEmail;
        TagsBox.Text = string.Join(", ", _item.Tags);
        NotesBox.Text = _item.Notes;
        CustomFieldsBox.Text = string.Join(Environment.NewLine,
            _item.CustomFields.Select(f => $"{f.Label}={f.Value}"));
        TotpBox.Text = _item.Totp?.Secret ?? string.Empty;
        TotpModeBox.SelectedIndex = _item.Totp?.IsHotp == true ? 1 : 0;
        HotpCounterBox.Text = (_item.Totp?.Counter ?? 0).ToString();
        if (_item.Totp is not null)
            UpdateTotpInfo(_item.Totp);

        Loaded += (_, _) => TitleBox.Focus();
    }

    public PasswordItem Result => _item;

    private void OnTogglePasswordVisibility(object sender, RoutedEventArgs e)
    {
        if (PasswordVisibleBox.Visibility == Visibility.Visible)
        {
            PasswordBoxInput.Password = PasswordVisibleBox.Text;
            PasswordVisibleBox.Visibility = Visibility.Collapsed;
            PasswordBoxInput.Visibility = Visibility.Visible;
            PasswordRevealButton.Content = "显示";
        }
        else
        {
            PasswordVisibleBox.Text = PasswordBoxInput.Password;
            PasswordBoxInput.Visibility = Visibility.Collapsed;
            PasswordVisibleBox.Visibility = Visibility.Visible;
            PasswordRevealButton.Content = "隐藏";
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword || PasswordVisibleBox.Visibility != Visibility.Visible)
            return;
        _syncingPassword = true;
        try { PasswordVisibleBox.Text = PasswordBoxInput.Password; }
        finally { _syncingPassword = false; }
    }

    private void OnPasswordTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncingPassword || PasswordBoxInput.Visibility == Visibility.Visible)
            return;
        _syncingPassword = true;
        try { PasswordBoxInput.Password = PasswordVisibleBox.Text; }
        finally { _syncingPassword = false; }
    }

    private void OnGeneratePassword(object sender, RoutedEventArgs e)
    {
        try
        {
            var length = int.TryParse(PasswordLengthBox.Text.Trim(), out var parsed) ? parsed : 20;
            var generated = RandomPasswordGenerator.Generate(new PasswordGeneratorOptions
            {
                Length = length,
                IncludeUpper = IncludeUpperBox.IsChecked == true,
                IncludeLower = IncludeLowerBox.IsChecked == true,
                IncludeDigits = IncludeDigitsBox.IsChecked == true,
                IncludeSymbols = IncludeSymbolsBox.IsChecked == true,
                SafeSymbolsOnly = SafeSymbolsBox.IsChecked == true,
                ExcludeAmbiguous = ExcludeAmbiguousBox.IsChecked == true
            });

            _syncingPassword = true;
            try
            {
                PasswordBoxInput.Password = generated.Value;
                PasswordVisibleBox.Text = generated.Value;
            }
            finally
            {
                _syncingPassword = false;
            }

            PasswordBoxInput.Visibility = Visibility.Collapsed;
            PasswordVisibleBox.Visibility = Visibility.Visible;
            PasswordRevealButton.Content = "隐藏";
            GeneratedHintText.Text = $"已生成 {generated.Value.Length} 位密码（{generated.StrengthText}）。";
            ErrorText.Text = string.Empty;
        }
        catch (ArgumentException ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private void OnImportQrCode(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择包含 TOTP 二维码的图片",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        if (!QrCodeReader.TryReadOtpauth(dialog.FileName, out var uri, out var config, out var error))
        {
            ErrorText.Text = error ?? "二维码识别失败。";
            return;
        }

        ApplyQrResult(uri, config);
    }

    private void OnImportQrCodeFromClipboard(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not { } image)
            {
                ErrorText.Text = "剪贴板中没有图片。";
                return;
            }

            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            var stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            if (!QrCodeReader.TryDecodeOtpauth(pixels, width, height,
                    out var uri, out var config, out var error))
            {
                ErrorText.Text = error ?? "二维码识别失败。";
                return;
            }

            ApplyQrResult(uri, config);
        }
        catch (Exception ex)
        {
            ErrorText.Text = "读取剪贴板图片失败：" + ex.Message;
        }
    }

    private void ApplyQrResult(string? uri, PasswordTotpConfig? config)
    {
        TotpBox.Text = uri ?? config?.Secret ?? string.Empty;
        if (config is not null)
        {
            TotpModeBox.SelectedIndex = config.IsHotp ? 1 : 0;
            HotpCounterBox.Text = config.Counter.ToString();
            UpdateTotpInfo(config);
        }
        ErrorText.Text = "二维码已识别，保存后生效。";
    }

    private void OnTotpModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (HotpPanel is not null)
            HotpPanel.Visibility = TotpModeBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTotpInfo(PasswordTotpConfig config)
    {
        var mode = config.IsHotp ? "HOTP" : "TOTP";
        var counter = config.IsHotp ? $" / 计数器 {config.Counter}" : $" / {config.Period} 秒";
        TotpInfoText.Text = $"已配置 {mode}：{config.Issuer} / {config.Account} / {config.Algorithm} / {config.Digits} 位{counter}";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            ErrorText.Text = "请填写标题。";
            return;
        }

        _item.Title = TitleBox.Text.Trim();
        _item.Type = (PasswordItemType)Math.Clamp(TypeBox.SelectedIndex, 0, 5);
        _item.Group = GroupBox.Text.Trim();
        _item.Username = UsernameBox.Text.Trim();
        _item.Password = PasswordBoxInput.Password;
        _item.Url = UrlBox.Text.Trim();
        _item.SecurityPhone = PhoneBox.Text.Trim();
        _item.RecoveryEmail = EmailBox.Text.Trim();
        _item.Notes = NotesBox.Text.Trim();
        _item.Tags = TagsBox.Text
            .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _item.CustomFields = ParseCustomFields(CustomFieldsBox.Text);
        _item.Totp = ParseTotp(TotpBox.Text.Trim());

        if (_isNew)
        {
            _item.CreatedAt = DateTimeOffset.Now;
            _item.UpdatedAt = _item.CreatedAt;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private List<PasswordCustomField> ParseCustomFields(string text)
    {
        var result = new List<PasswordCustomField>();
        foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            var index = line.IndexOf('=');
            if (index <= 0)
                continue;
            var label = line[..index].Trim();
            var value = line[(index + 1)..].Trim();
            var existing = _item.CustomFields.FirstOrDefault(f =>
                string.Equals(f.Label, label, StringComparison.CurrentCultureIgnoreCase));
            result.Add(new PasswordCustomField
            {
                Label = label,
                Value = value,
                IsSecret = existing?.IsSecret ?? false
            });
        }
        return result;
    }

    private PasswordTotpConfig? ParseTotp(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (TotpGenerator.TryParseUri(text, out var parsed) && parsed is not null)
            return parsed;

        var secret = TotpGenerator.NormalizeSecret(text);
        if (secret.Length == 0)
            return null;

        var isHotp = TotpModeBox.SelectedIndex == 1;
        var counter = long.TryParse(HotpCounterBox.Text.Trim(), out var parsedCounter)
            ? Math.Max(0, parsedCounter)
            : 0;

        if (_item.Totp is not null &&
            _item.Totp.IsHotp == isHotp &&
            string.Equals(TotpGenerator.NormalizeSecret(_item.Totp.Secret), secret,
                StringComparison.OrdinalIgnoreCase))
        {
            _item.Totp.Counter = isHotp ? counter : 0;
            return _item.Totp;
        }

        return new PasswordTotpConfig
        {
            Secret = secret,
            Issuer = _item.Title,
            Account = _item.Username,
            Algorithm = "SHA1",
            Digits = 6,
            Period = 30,
            IsHotp = isHotp,
            Counter = isHotp ? counter : 0
        };
    }
}
