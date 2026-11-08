using System.Windows;
using LitSSHmcp.App.Services.Notifications;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class NotificationCenterWindow : FluentWindow
{
    public NotificationCenterWindow()
    {
        InitializeComponent();
        NotificationList.ItemsSource = NotificationCenterService.Items;
        Loaded += (_, _) => RefreshSummary();
        NotificationCenterService.Changed += RefreshSummary;
        Closed += (_, _) => NotificationCenterService.Changed -= RefreshSummary;
    }

    private void RefreshSummary()
    {
        var unread = NotificationCenterService.UnreadCount;
        SummaryText.Text = unread == 0
            ? $"共 {NotificationCenterService.Items.Count} 条，已全部阅读"
            : $"共 {NotificationCenterService.Items.Count} 条，{unread} 条未读";
    }

    private void OnMarkAllRead(object sender, RoutedEventArgs e)
    {
        NotificationCenterService.MarkAllRead();
        RefreshSummary();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        NotificationCenterService.Clear();
        RefreshSummary();
    }
}
