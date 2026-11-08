using System.Windows.Input;
using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    /// <summary>主页本机运行看板（CPU / 内存 / 磁盘 / 网络 / 进程）。</summary>
    public HomeDashboardViewModel HomeDashboard { get; }

    public void DisposeHomeDashboard() => HomeDashboard.Dispose();

    private bool _showOnboarding =
        !bool.TryParse(UiPrefs.GetString("home.onboardingDismissed", "false"), out var dismissed) || !dismissed;

    public bool ShowOnboarding => _showOnboarding;

    private RelayCommand? _dismissOnboardingCommand;
    public ICommand DismissOnboardingCommand =>
        _dismissOnboardingCommand ??= new RelayCommand(_ => DismissOnboarding());

    private void DismissOnboarding()
    {
        _showOnboarding = false;
        UiPrefs.SetString("home.onboardingDismissed", "true");
        OnPropertyChanged(nameof(ShowOnboarding));
    }
}
