using System.IO;
using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class TemporarySshWindow : FluentWindow
{
    private readonly Func<SshServerConfig, Task<ConnectionProbeResult>> _probe;

    public TemporarySshWindow(
        IEnumerable<SshServerConfig> jumpServers,
        Func<SshServerConfig, Task<ConnectionProbeResult>> probe)
    {
        InitializeComponent();
        _probe = probe;

        var jumpCombo = GetJumpCombo();
        jumpCombo.Items.Add(new ComboBoxItem { Content = "不使用跳板机", Tag = null });
        foreach (var server in jumpServers.Where(s => !s.Disabled).OrderBy(s => s.Name))
        {
            jumpCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{server.Name} ({server.Host})",
                Tag = server.Id
            });
        }
        jumpCombo.SelectedIndex = 0;
        GetAuthCombo().SelectionChanged += OnAuthChanged;

        UpdateAuthRows();
        Closed += (_, _) =>
        {
            ClearPassword(PasswordRow);
            ClearPassword(KeyPassRow);
        };
    }

    /// <summary>用户确认后的临时服务器配置；仅存在于内存中。</summary>
    public SshServerConfig? Result { get; private set; }

    private void OnAuthChanged(object sender, SelectionChangedEventArgs e) => UpdateAuthRows();

    private void UpdateAuthRows()
    {
        if (PasswordRow is null || KeyFileRow is null || KeyPassRow is null)
            return;

        var keyMode = GetAuthCombo().SelectedIndex == 1;
        PasswordRow.Visibility = keyMode ? Visibility.Collapsed : Visibility.Visible;
        KeyFileRow.Visibility = keyMode ? Visibility.Visible : Visibility.Collapsed;
        KeyPassRow.Visibility = keyMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTest(object sender, RoutedEventArgs e) => AsyncOperation.Run("测试临时 SSH 连接", async () =>
    {
        var server = BuildServer(out var error);
        if (server is null)
        {
            StatusText.Text = error;
            return;
        }

        TestButton.IsEnabled = false;
        ConnectButton.IsEnabled = false;
        StatusText.Text = "正在测试连接…";
        try
        {
            var result = await _probe(server);
            if (!IsLoaded)
                return;
            StatusText.Text = result.Success
                ? $"连接成功（{result.Duration.TotalMilliseconds:0} ms）"
                : $"连接失败：{DescribeFailure(result)}";
        }
        catch (Exception ex)
        {
            if (IsLoaded)
                StatusText.Text = $"连接失败：{ex.Message}";
        }
        finally
        {
            if (IsLoaded)
            {
                TestButton.IsEnabled = true;
                ConnectButton.IsEnabled = true;
            }
        }
    });

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        var server = BuildServer(out var error);
        if (server is null)
        {
            StatusText.Text = error;
            return;
        }

        Result = server;
        DialogResult = true;
    }

    private SshServerConfig? BuildServer(out string error)
    {
        error = string.Empty;
        var host = GetText(HostRow);
        if (string.IsNullOrWhiteSpace(host))
        {
            error = "请填写主机地址。";
            return null;
        }
        if (!int.TryParse(GetText(PortRow), out var port) || port is < 1 or > 65535)
        {
            error = "端口必须是 1-65535。";
            return null;
        }
        var username = GetText(UserRow);
        if (string.IsNullOrWhiteSpace(username))
        {
            error = "请填写用户名。";
            return null;
        }

        var keyMode = GetAuthCombo().SelectedIndex == 1;
        var keyPath = GetKeyPicker()?.Value?.Trim() ?? string.Empty;
        if (keyMode && string.IsNullOrWhiteSpace(keyPath))
        {
            error = "请选择密钥文件。";
            return null;
        }
        if (keyMode && !File.Exists(keyPath))
        {
            error = "密钥文件不存在。";
            return null;
        }

        var jumpId = (GetJumpCombo().SelectedItem as ComboBoxItem)?.Tag as string;
        var name = GetText(NameRow);
        if (string.IsNullOrWhiteSpace(name))
            name = $"临时 - {host}";

        return new SshServerConfig
        {
            Id = $"temp-{Guid.NewGuid():N}",
            Name = name,
            Host = host,
            Port = port,
            Username = username,
            AuthType = keyMode ? AuthType.KeyFile : AuthType.Password,
            Password = keyMode ? null : GetPassword(PasswordRow),
            KeyFilePath = keyMode ? keyPath : null,
            KeyFilePassphrase = keyMode ? GetPassword(KeyPassRow) : null,
            ProxyJumpServerId = string.IsNullOrWhiteSpace(jumpId) ? null : jumpId,
            Description = "临时连接（不持久化）",
            Group = string.Empty,
            Disabled = false
        };
    }

    private static string GetText(FormRow row)
        => (row.FieldContent as System.Windows.Controls.TextBox)?.Text.Trim() ?? string.Empty;

    private static string GetPassword(FormRow row)
        => (row.FieldContent as System.Windows.Controls.PasswordBox)?.Password ?? string.Empty;

    private static void ClearPassword(FormRow row)
    {
        if (row.FieldContent is System.Windows.Controls.PasswordBox box)
            box.Password = string.Empty;
    }

    private ComboBox GetAuthCombo()
        => AuthRow.FieldContent as ComboBox ?? throw new InvalidOperationException("认证方式控件未加载。");

    private ComboBox GetJumpCombo()
        => JumpRow.FieldContent as ComboBox ?? throw new InvalidOperationException("跳板机控件未加载。");

    private PathPicker? GetKeyPicker() => KeyFileRow.FieldContent as PathPicker;

    private static string DescribeFailure(ConnectionProbeResult result) =>
        result.ErrorKind switch
        {
            "auth" => "认证失败，请检查用户名、密码或密钥。",
            "host_key" => "主机密钥校验失败。请检查 known_hosts 或主机密钥策略。",
            "timeout" => "连接超时。",
            "network" => "网络不可达或端口未开放。",
            _ => string.IsNullOrWhiteSpace(result.Error) ? "未知错误。" : result.Error
        };
}
