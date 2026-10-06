using System.ComponentModel;
using System.Runtime.CompilerServices;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

/// <summary>外观设置（全局）：界面主题（跟随系统/浅色/深色）与强调色；写入 <c>ui.theme</c> / <c>ui.accent</c>，切换即时生效。</summary>
public class AppearanceSettingsViewModel : INotifyPropertyChanged
{
    private readonly IConfigService _configService = new ConfigService();

    public AppearanceSettingsViewModel() => Load();

    /// <summary>界面主题（全局）：system / light / dark；切换即时生效。</summary>
    public IReadOnlyList<string> Themes { get; } = new[] { "system", "light", "dark" };

    private string _theme = "system";
    public string Theme
    {
        get => _theme;
        set
        {
            if (string.Equals(_theme, value, StringComparison.Ordinal))
                return;
            Set(ref _theme, value);
            ThemeService.Apply(value, AccentToHex(Accent));
        }
    }

    /// <summary>强调色选项。</summary>
    public IReadOnlyList<string> Accents { get; } = new[] { "系统默认", "蓝色", "紫色", "绿色", "橙色", "红色" };

    private string _accent = "系统默认";
    public string Accent
    {
        get => _accent;
        set
        {
            if (string.Equals(_accent, value, StringComparison.Ordinal))
                return;
            Set(ref _accent, value);
            ThemeService.Apply(Theme, AccentToHex(value));
        }
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    private static string AccentToHex(string? label) => label switch
    {
        "蓝色" => "#0078D4",
        "紫色" => "#8B5CF6",
        "绿色" => "#10B981",
        "橙色" => "#F59E0B",
        "红色" => "#EF4444",
        _ => string.Empty
    };

    private static string HexToAccent(string? hex) => (hex ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "#0078D4" => "蓝色",
        "#8B5CF6" => "紫色",
        "#10B981" => "绿色",
        "#F59E0B" => "橙色",
        "#EF4444" => "红色",
        _ => "系统默认"
    };

    public async void Load()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            Theme = string.IsNullOrWhiteSpace(config.Ui?.Theme) ? "system" : config.Ui!.Theme;
            Accent = HexToAccent(config.Ui?.Accent);
            StatusMessage = "已加载";
        }
        catch (Exception ex)
        {
            StatusMessage = "加载失败: " + ex.Message;
        }
    }

    public async void Save()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            config.Ui ??= new UiConfig();
            config.Ui.Theme = string.IsNullOrWhiteSpace(Theme) ? "system" : Theme;
            config.Ui.Accent = AccentToHex(Accent);
            await _configService.SaveConfigAsync(config);
            StatusMessage = "已保存（切换即时生效）";
        }
        catch (Exception ex)
        {
            StatusMessage = "保存失败: " + ex.Message;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
