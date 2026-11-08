using System.Windows;
using System.Windows.Input;
using LitSSHmcp.App.Services.Notifications;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    private RelayCommand? _openNotificationsCommand;

    public ICommand OpenNotificationCenterCommand =>
        _openNotificationsCommand ??= new RelayCommand(_ => OpenNotificationCenter());

    public int NotificationUnreadCount => NotificationCenterService.UnreadCount;
    public bool HasUnreadNotifications => NotificationCenterService.HasUnread;

    private void OpenNotificationCenter()
    {
        var window = _windows.CreateNotificationCenterWindow();
        if (Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, window))
            window.Owner = owner;
        window.Show();
        window.Activate();
    }

    internal void RefreshNotificationState()
    {
        OnPropertyChanged(nameof(NotificationUnreadCount));
        OnPropertyChanged(nameof(HasUnreadNotifications));
    }

    internal void DetachNotifications() => NotificationCenterService.Changed -= RefreshNotificationState;

    /// <summary>打开设置窗口并定位到指定 Tab（命令面板/引导使用）。</summary>
    public void OpenSettingsTab(int index)
    {
        OpenSettings();
        _settingsWindow?.SelectTab(index);
    }
}
