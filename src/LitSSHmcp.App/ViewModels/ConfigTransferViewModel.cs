using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Win32;

namespace LitSSHmcp.App.ViewModels;

/// <summary>配置导入 / 导出（从主界面设置入口整合而来）。</summary>
public class ConfigTransferViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService;

    public ConfigTransferViewModel(IConfigService configService)
    {
        _configService = configService;
        ExportCommand = new RelayCommand(_ => Export());
        ImportCommand = new RelayCommand(_ => Import());
    }

    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    private async void Export()
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"litssh-config-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            Filter = "JSON 文件|*.json|所有文件|*.*"
        };

        if (dialog.ShowDialog() != true)
            return;

        var choice = MessageBox.Show(
            "导出内容:\n\n[是] 包含密钥（DPAPI 密文，仅本机当前用户可用）\n[否] 脱敏导出（不含任何密码）",
            "导出配置", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (choice == MessageBoxResult.Cancel)
            return;

        try
        {
            if (choice == MessageBoxResult.Yes)
            {
                File.Copy(_configService.GetConfigPath(), dialog.FileName, overwrite: true);
            }
            else
            {
                var config = await _configService.LoadConfigAsync();
                foreach (var server in config.Servers)
                {
                    server.Password = null;
                    server.KeyFilePassphrase = null;
                    server.SudoPassword = null;
                }
                foreach (var ds in config.DataSources)
                    ds.Password = null;

                var json = JsonSerializer.Serialize(config, AppConfigJson.Options);
                await File.WriteAllTextAsync(dialog.FileName, json);
            }

            StatusMessage = $"已导出配置: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"导出失败: {ex.Message}";
        }
    }

    private async void Import()
    {
        var dialog = new OpenFileDialog { Filter = "JSON 文件|*.json|所有文件|*.*" };
        if (dialog.ShowDialog() != true)
            return;

        if (MessageBox.Show("导入将覆盖当前配置（含服务器/数据源/应用/关系/安全设置），确定继续？",
                "导入配置", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            var json = await File.ReadAllTextAsync(dialog.FileName, Encoding.UTF8);
            var config = JsonSerializer.Deserialize<AppConfig>(json, AppConfigJson.Options);
            if (config == null)
            {
                StatusMessage = "导入失败: 文件内容不是有效的配置";
                return;
            }

            await _configService.SaveConfigAsync(config);
            StatusMessage = $"已导入配置: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"导入失败: {ex.Message}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
