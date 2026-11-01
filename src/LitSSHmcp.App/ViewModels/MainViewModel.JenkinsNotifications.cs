namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    private int _jenkinsNotificationCount;

    public event Action<JenkinsBuildCompletedInfo>? JenkinsBuildCompleted;

    public int JenkinsNotificationCount => _jenkinsNotificationCount;

    public bool ShowJenkinsBadge => _jenkinsNotificationCount > 0 && CurrentPage != MainPage.Jenkins;

    public string JenkinsBadgeText => _jenkinsNotificationCount > 9 ? "9+" : _jenkinsNotificationCount.ToString();

    public string JenkinsBadgeTooltip => $"Jenkins：{_jenkinsNotificationCount} 条构建完成通知";

    private void OnJenkinsBuildCompleted(JenkinsBuildCompletedInfo info)
    {
        _jenkinsNotificationCount++;
        OnPropertyChanged(nameof(JenkinsNotificationCount));
        OnPropertyChanged(nameof(ShowJenkinsBadge));
        OnPropertyChanged(nameof(JenkinsBadgeText));
        OnPropertyChanged(nameof(JenkinsBadgeTooltip));
        JenkinsBuildCompleted?.Invoke(info);
    }

    public void ClearJenkinsNotifications()
    {
        if (_jenkinsNotificationCount == 0)
            return;

        _jenkinsNotificationCount = 0;
        OnPropertyChanged(nameof(JenkinsNotificationCount));
        OnPropertyChanged(nameof(ShowJenkinsBadge));
        OnPropertyChanged(nameof(JenkinsBadgeText));
        OnPropertyChanged(nameof(JenkinsBadgeTooltip));
    }
}
