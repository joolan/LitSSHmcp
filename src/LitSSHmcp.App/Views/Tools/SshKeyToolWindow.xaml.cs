using System.IO;
using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Toolbox;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views.Tools;

public partial class SshKeyToolWindow : FluentWindow
{
    private readonly SshKeyService _service = new();
    private bool _generating;

    public SshKeyToolWindow()
    {
        InitializeComponent();
        AlgorithmBox.ItemsSource = SshKeyAlgorithms.All;
        AlgorithmBox.SelectedIndex = 0;
        UpdateDefaultPath();
    }

    private SshKeyAlgorithm? SelectedAlgorithm => AlgorithmBox.SelectedItem as SshKeyAlgorithm;

    private void OnAlgorithmChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            UpdateDefaultPath();
    }

    private void UpdateDefaultPath()
    {
        if (string.IsNullOrWhiteSpace(PrivateKeyPathBox.Text) || IsDefaultKeyPath(PrivateKeyPathBox.Text))
            PrivateKeyPathBox.Text = DefaultPathFor(SelectedAlgorithm);
    }

    private void OnChoosePath(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存 SSH 私钥",
            Filter = "所有文件 (*.*)|*.*",
            FileName = SelectedAlgorithm?.DefaultFileName ?? "id_ed25519",
            InitialDirectory = DefaultKeyDirectory()
        };
        if (dialog.ShowDialog(this) == true)
            PrivateKeyPathBox.Text = dialog.FileName;
    }

    private void OnGenerate(object sender, RoutedEventArgs e) => _ = GenerateAsync();

    private async Task GenerateAsync()
    {
        if (_generating || SelectedAlgorithm is not { } algorithm)
            return;

        if (string.IsNullOrWhiteSpace(PrivateKeyPathBox.Text))
        {
            OnChoosePath(sender: this, new RoutedEventArgs());
            if (string.IsNullOrWhiteSpace(PrivateKeyPathBox.Text))
                return;
        }

        _generating = true;
        GenerateButton.IsEnabled = false;
        StatusText.Text = "正在生成密钥...";
        try
        {
            var result = await _service.GenerateAsync(
                PrivateKeyPathBox.Text.Trim(),
                algorithm,
                CommentBox.Text,
                PassphraseBox.Password);
            PublicKeyBox.Text = result.PublicKey;
            FingerprintBox.Text = result.Fingerprint;
            StatusText.Text = $"已生成：{result.PrivateKeyPath}（同时创建 .pub）";
        }
        catch (Exception ex)
        {
            StatusText.Text = "生成失败：" + ex.Message;
        }
        finally
        {
            _generating = false;
            GenerateButton.IsEnabled = true;
        }
    }

    private void OnCopyPublicKey(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PublicKeyBox.Text))
        {
            StatusText.Text = "请先生成或解析公钥。";
            return;
        }

        try
        {
            Clipboard.SetText(PublicKeyBox.Text);
            StatusText.Text = "已复制公钥。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }

    private void OnOpenKeyFolder(object sender, RoutedEventArgs e)
    {
        var path = PrivateKeyPathBox.Text.Trim();
        var directory = string.IsNullOrWhiteSpace(path)
            ? DefaultKeyDirectory()
            : Path.GetDirectoryName(path) ?? DefaultKeyDirectory();
        NativeShell.OpenFolder(directory);
    }

    private void OnImportPublicKey(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 SSH 公钥",
            Filter = "SSH 公钥 (*.pub;*.txt)|*.pub;*.txt|所有文件 (*.*)|*.*",
            InitialDirectory = DefaultKeyDirectory()
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            InspectInputBox.Text = File.ReadAllText(dialog.FileName).Trim();
            InspectPublicKey();
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取失败：" + ex.Message;
        }
    }

    private void OnInspect(object sender, RoutedEventArgs e) => InspectPublicKey();

    private void InspectPublicKey()
    {
        try
        {
            var info = _service.ParsePublicKey(InspectInputBox.Text);
            InspectTypeBox.Text = info.Algorithm;
            InspectFingerprintBox.Text = info.Fingerprint;
            InspectCommentBox.Text = info.Comment;
            InspectKeyBox.Text = info.KeyText;
            StatusText.Text = "公钥解析完成。";
        }
        catch (Exception ex)
        {
            InspectTypeBox.Text = string.Empty;
            InspectFingerprintBox.Text = string.Empty;
            InspectCommentBox.Text = string.Empty;
            InspectKeyBox.Text = string.Empty;
            StatusText.Text = "解析失败：" + ex.Message;
        }
    }

    private void OnCopyInspectKey(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InspectKeyBox.Text))
        {
            StatusText.Text = "没有可复制的公钥。";
            return;
        }

        try
        {
            Clipboard.SetText(InspectKeyBox.Text);
            StatusText.Text = "已复制公钥。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }

    private static string DefaultKeyDirectory()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        try { Directory.CreateDirectory(directory); } catch { /* ignore */ }
        return directory;
    }

    private static string DefaultPathFor(SshKeyAlgorithm? algorithm)
        => Path.Combine(DefaultKeyDirectory(), algorithm?.DefaultFileName ?? "id_ed25519");

    private static bool IsDefaultKeyPath(string path)
    {
        try
        {
            var directory = Path.GetFullPath(DefaultKeyDirectory());
            var candidate = Path.GetFullPath(path);
            return string.Equals(Path.GetDirectoryName(candidate), directory,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
