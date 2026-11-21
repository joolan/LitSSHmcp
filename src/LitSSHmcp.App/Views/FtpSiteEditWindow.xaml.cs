using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using Microsoft.Win32;

namespace LitSSHmcp.App.Views;

public partial class FtpSiteEditWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly IUserDialogService _dialogs = new UserDialogService();
    private readonly string? _existingPassword;
    private readonly string? _existingClientCertPassword;
    private readonly string? _existingProxyPassword;
    private bool _initializing;

    public FtpSiteEditWindow(FileTransferSiteConfig? site)
    {
        InitializeComponent();
        Site = site ?? new FileTransferSiteConfig();
        _existingPassword = Site.Password;
        _existingClientCertPassword = Site.ClientCertificatePassword;
        _existingProxyPassword = Site.ProxyPassword;
        _initializing = true;
        try
        {
            NameBox.Text = Site.Name;
            SelectByTag(ProtocolBox, Site.Protocol.ToString());
            SelectByTag(FtpsModeBox, Site.FtpsMode.ToString());
            HostBox.Text = Site.Host;
            PortBox.Text = Site.Port.ToString();
            SelectByTag(AuthBox, Site.AuthType.ToString());
            UsernameBox.Text = Site.Username;
            PasswordBox.Password = Site.Password ?? string.Empty;
            InitialPathBox.Text = Site.InitialPath;
            SelectByTag(EncodingBox, Site.Encoding);
            TimeoutBox.Text = Site.TimeoutSeconds.ToString();
            PassiveBox.IsChecked = Site.Passive;
            PassiveFallbackBox.IsChecked = Site.PassiveFallback;
            ValidateCertificateBox.IsChecked = Site.ValidateCertificate;
            SelectByTag(TlsVersionBox, Site.TlsVersion);
            CaPathBox.Text = Site.CaCertificatePath ?? string.Empty;
            PinsBox.Text = string.Join(", ", Site.CertificatePins);
            ClientCertPathBox.Text = Site.ClientCertificatePath ?? string.Empty;
            ClientKeyPathBox.Text = Site.ClientCertificateKeyPath ?? string.Empty;
            ClientCertPasswordBox.Password = Site.ClientCertificatePassword ?? string.Empty;
            ConcurrentBox.Text = Site.MaxConcurrentTransfers.ToString();
            SpeedLimitBox.Text = Site.SpeedLimitKBps.ToString();
            KeepAliveBox.Text = Site.KeepAliveSeconds.ToString();
            AutoReconnectBox.IsChecked = Site.AutoReconnect;
            SelectByTag(ConflictPolicyBox, Site.ConflictPolicy.ToString());
            SelectByTag(ProxyTypeBox, Site.ProxyType.ToString());
            ProxyHostBox.Text = Site.ProxyHost ?? string.Empty;
            ProxyPortBox.Text = Site.ProxyPort > 0 ? Site.ProxyPort.ToString() : string.Empty;
            ProxyUsernameBox.Text = Site.ProxyUsername ?? string.Empty;
            ProxyPasswordBox.Password = Site.ProxyPassword ?? string.Empty;
            GroupBox.Text = Site.Group;
            TagsBox.Text = Site.TagsText;
            DescriptionBox.Text = Site.Description ?? string.Empty;
        }
        finally
        {
            _initializing = false;
        }

        TitleBarControl.Title = site is null ? "添加 FTP / FTPS 站点" : "编辑 FTP / FTPS 站点";
        UpdateAuthState();
        UpdateFtpsState();
        Loaded += (_, _) => NameBox.Focus();
    }

    public FileTransferSiteConfig Site { get; }

    private void OnProtocolChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        UpdateFtpsState();
        if (ProtocolBox.SelectedItem is ComboBoxItem { Tag: "Ftp" })
        {
            if (PortBox.Text is "21" or "990")
                PortBox.Text = "21";
        }
        else if (PortBox.Text is "21" or "990")
        {
            PortBox.Text = FtpsModeBox.SelectedItem is ComboBoxItem { Tag: "Implicit" } ? "990" : "21";
        }
    }

    private void OnFtpsModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || ProtocolBox.SelectedItem is not ComboBoxItem { Tag: "Ftps" })
            return;
        if (PortBox.Text is "21" or "990")
            PortBox.Text = FtpsModeBox.SelectedItem is ComboBoxItem { Tag: "Implicit" } ? "990" : "21";
    }

    private void OnAuthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing)
            UpdateAuthState();
    }

    private void UpdateAuthState()
    {
        var anonymous = AuthBox.SelectedItem is ComboBoxItem { Tag: "Anonymous" };
        UsernameBox.IsEnabled = !anonymous;
        PasswordBox.IsEnabled = !anonymous;
        if (anonymous && string.IsNullOrWhiteSpace(UsernameBox.Text))
            UsernameBox.Text = "anonymous";
    }

    private void UpdateFtpsState()
    {
        var ftps = ProtocolBox.SelectedItem is ComboBoxItem { Tag: "Ftps" };
        FtpsModeBox.IsEnabled = ftps;
        ValidateCertificateBox.IsEnabled = ftps;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var protocol = ProtocolBox.SelectedItem is ComboBoxItem { Tag: "Ftps" }
            ? FileTransferProtocol.Ftps
            : FileTransferProtocol.Ftp;
        var auth = AuthBox.SelectedItem is ComboBoxItem { Tag: "Anonymous" }
            ? FileTransferAuthType.Anonymous
            : FileTransferAuthType.Password;

        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            _dialogs.Warn("请填写站点名称。", "文件传输");
            return;
        }
        if (string.IsNullOrWhiteSpace(HostBox.Text))
        {
            _dialogs.Warn("请填写主机地址。", "文件传输");
            return;
        }
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            _dialogs.Warn("端口必须是 1-65535 之间的数字。", "文件传输");
            return;
        }
        if (auth == FileTransferAuthType.Password && string.IsNullOrWhiteSpace(UsernameBox.Text))
        {
            _dialogs.Warn("账号密码认证需要填写用户名。", "文件传输");
            return;
        }
        if (!int.TryParse(TimeoutBox.Text.Trim(), out var timeout) || timeout is < 5 or > 600)
        {
            _dialogs.Warn("超时必须是 5-600 秒。", "文件传输");
            return;
        }
        if (!int.TryParse(ConcurrentBox.Text.Trim(), out var concurrency) || concurrency is < 1 or > 8)
        {
            _dialogs.Warn("并发传输数必须是 1-8。", "文件传输");
            return;
        }
        if (!int.TryParse(SpeedLimitBox.Text.Trim(), out var speedLimit) || speedLimit < 0)
        {
            _dialogs.Warn("限速必须是 0 或正整数 KB/s。", "文件传输");
            return;
        }
        if (!int.TryParse(KeepAliveBox.Text.Trim(), out var keepAlive) || keepAlive is < 0 or > 3600)
        {
            _dialogs.Warn("Keep-alive 必须是 0-3600 秒。", "文件传输");
            return;
        }

        var proxyType = ParseTag(ProxyTypeBox, FtpProxyType.None);
        var proxyPort = 0;
        if (proxyType != FtpProxyType.None)
        {
            if (string.IsNullOrWhiteSpace(ProxyHostBox.Text))
            {
                _dialogs.Warn("启用代理时必须填写代理主机。", "文件传输");
                return;
            }
            if (!int.TryParse(ProxyPortBox.Text.Trim(), out proxyPort) || proxyPort is < 1 or > 65535)
            {
                _dialogs.Warn("代理端口必须是 1-65535。", "文件传输");
                return;
            }
        }
        if (protocol == FileTransferProtocol.Ftp &&
            !_dialogs.Confirm("FTP 是明文协议，账号密码和文件内容都不加密。\n\n仍要保存该站点？", "不安全的 FTP", warning: true))
        {
            return;
        }

        Site.Name = NameBox.Text.Trim();
        Site.Protocol = protocol;
        Site.FtpsMode = FtpsModeBox.SelectedItem is ComboBoxItem { Tag: "Implicit" }
            ? FtpsMode.Implicit
            : FtpsMode.Explicit;
        Site.Host = HostBox.Text.Trim();
        Site.Port = port;
        Site.AuthType = auth;
        Site.Username = auth == FileTransferAuthType.Anonymous
            ? string.IsNullOrWhiteSpace(UsernameBox.Text) ? "anonymous" : UsernameBox.Text.Trim()
            : UsernameBox.Text.Trim();
        var typedPassword = PasswordBox.Password;
        Site.Password = auth == FileTransferAuthType.Anonymous
            ? null
            : string.IsNullOrEmpty(typedPassword) ? _existingPassword : typedPassword;
        Site.InitialPath = string.IsNullOrWhiteSpace(InitialPathBox.Text) ? "/" : InitialPathBox.Text.Trim();
        Site.Encoding = (EncodingBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "utf-8";
        Site.TimeoutSeconds = timeout;
        Site.Passive = PassiveBox.IsChecked == true;
        Site.PassiveFallback = PassiveFallbackBox.IsChecked == true;
        Site.ValidateCertificate = ValidateCertificateBox.IsChecked == true;
        Site.TlsVersion = (TlsVersionBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        Site.CaCertificatePath = CaPathBox.Text.Trim();
        Site.CertificatePins = PinsBox.Text
            .Split(new[] { ',', '，', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Site.ClientCertificatePath = ClientCertPathBox.Text.Trim();
        Site.ClientCertificateKeyPath = ClientKeyPathBox.Text.Trim();
        Site.ClientCertificatePassword = string.IsNullOrEmpty(ClientCertPasswordBox.Password)
            ? _existingClientCertPassword
            : ClientCertPasswordBox.Password;
        Site.MaxConcurrentTransfers = concurrency;
        Site.SpeedLimitKBps = speedLimit;
        Site.KeepAliveSeconds = keepAlive;
        Site.AutoReconnect = AutoReconnectBox.IsChecked == true;
        Site.ConflictPolicy = ParseTag(ConflictPolicyBox, FileTransferConflictPolicy.Ask);
        Site.ProxyType = proxyType;
        Site.ProxyHost = ProxyHostBox.Text.Trim();
        Site.ProxyPort = proxyPort;
        Site.ProxyUsername = ProxyUsernameBox.Text.Trim();
        Site.ProxyPassword = string.IsNullOrEmpty(ProxyPasswordBox.Password)
            ? _existingProxyPassword
            : ProxyPasswordBox.Password;
        Site.Group = GroupBox.Text.Trim();
        Site.Tags = TagsBox.Text
            .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Site.Description = DescriptionBox.Text.Trim();
        Site.Normalize();
        DialogResult = true;
    }

    private void OnBrowseCa(object sender, RoutedEventArgs e) => Browse(CaPathBox,
        "证书文件|*.pem;*.crt;*.cer|所有文件|*.*");

    private void OnBrowseClientCert(object sender, RoutedEventArgs e) => Browse(ClientCertPathBox,
        "证书文件|*.pfx;*.p12;*.pem;*.crt|所有文件|*.*");

    private void OnBrowseClientKey(object sender, RoutedEventArgs e) => Browse(ClientKeyPathBox,
        "私钥文件|*.pem;*.key|所有文件|*.*");

    private static void Browse(TextBox box, string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (dialog.ShowDialog() == true)
            box.Text = dialog.FileName;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private static T ParseTag<T>(ComboBox box, T fallback) where T : struct, Enum
        => box.SelectedItem is ComboBoxItem { Tag: string value } &&
           Enum.TryParse<T>(value, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items)
        {
            if (item is ComboBoxItem { Tag: string value } &&
                string.Equals(value, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0)
            box.SelectedIndex = 0;
    }
}
