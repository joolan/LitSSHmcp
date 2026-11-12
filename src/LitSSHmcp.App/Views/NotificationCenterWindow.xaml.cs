using System.Collections.ObjectModel;
using System.ComponentModel;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Notifications;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class NotificationCenterWindow : FluentWindow
{
    private readonly ICollectionView _view;
    private readonly ObservableCollection<DeliveryRow> _deliveries = new();

    public NotificationCenterWindow()
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(NotificationCenterService.Items);
        _view.Filter = FilterItem;
        NotificationList.ItemsSource = _view;

        TypeFilter.ItemsSource = new[]
        {
            "全部类型", NotificationTypesSnapshot.SnapshotFailed, NotificationTypesSnapshot.HealthWarning,
            NotificationTypesSnapshot.HealthCritical, NotificationTypesSnapshot.AiTaskSucceeded,
            NotificationTypesSnapshot.AiTaskFailed
        };
        TypeFilter.SelectedIndex = 0;
        SeverityFilter.ItemsSource = new[] { "全部级别", "Info", "Success", "Warning", "Error" };
        SeverityFilter.SelectedIndex = 0;
        DeliveryGrid.ItemsSource = _deliveries;

        Loaded += (_, _) => AsyncOperation.Run("加载通知中心", LoadAsync);
        NotificationCenterService.Changed += OnNotificationsChanged;
        Closed += (_, _) => NotificationCenterService.Changed -= OnNotificationsChanged;
    }

    private async Task LoadAsync()
    {
        await NotificationCenterService.InitializeAsync();
        RefreshSummary();
        await RefreshDeliveriesAsync();
    }

    private void OnNotificationsChanged()
    {
        RefreshSummary();
        _view.Refresh();
    }

    private void RefreshSummary()
    {
        var unread = NotificationCenterService.UnreadCount;
        SummaryText.Text = unread == 0
            ? $"共 {NotificationCenterService.Items.Count} 条，已全部阅读"
            : $"共 {NotificationCenterService.Items.Count} 条，{unread} 条未读";
    }

    private bool FilterItem(object item)
    {
        if (item is not NotificationItem notification)
            return false;

        var keyword = SearchBox?.Text?.Trim() ?? string.Empty;
        if (keyword.Length > 0 &&
            !notification.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
            !notification.Source.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
            !notification.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
            !notification.Type.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            return false;

        if (UnreadOnly.IsChecked == true && notification.IsRead)
            return false;

        var type = TypeFilter?.SelectedItem as string;
        if (type is not null && type != "全部类型" && !notification.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
            return false;

        var severity = SeverityFilter?.SelectedItem as string;
        if (severity is not null && severity != "全部级别" &&
            !notification.Severity.ToString().Equals(severity, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => _view.Refresh();

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => _view.Refresh();

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => _view.Refresh();

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

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var window = new NotificationSettingsWindow { Owner = this };
        window.ShowDialog();
    }

    private async Task RefreshDeliveriesAsync()
    {
        try
        {
            _deliveries.Clear();
            foreach (var delivery in await NotificationHub.Default.ListDeliveriesAsync(200))
                _deliveries.Add(new DeliveryRow(delivery));
            DeliveryStatusText.Text = $"最近 {_deliveries.Count} 条投递记录。";
        }
        catch (Exception ex)
        {
            DeliveryStatusText.Text = "加载投递记录失败: " + ex.Message;
        }
    }

    private void OnRefreshDeliveries(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("刷新通知投递记录", RefreshDeliveriesAsync);

    private void OnRetryDelivery(object sender, RoutedEventArgs e)
    {
        if (DeliveryGrid.SelectedItem is not DeliveryRow row)
        {
            DeliveryStatusText.Text = "请先选择一条投递记录。";
            return;
        }
        AsyncOperation.Run("重试通知投递", async () =>
        {
            await NotificationHub.Default.RetryDeliveryAsync(row.Delivery.Id);
            await RefreshDeliveriesAsync();
        });
    }

    private sealed class DeliveryRow
    {
        public DeliveryRow(NotificationDelivery delivery) => Delivery = delivery;
        public NotificationDelivery Delivery { get; }
        public string CreatedText => Delivery.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
        public string Type => Delivery.MessageType;
        public string Channel => Delivery.ChannelId;
        public string Status => Delivery.Status;
        public int Attempts => Delivery.Attempts;
        public string NextAttemptText => Delivery.Status == "pending"
            ? Delivery.NextAttemptAt.ToLocalTime().ToString("MM-dd HH:mm")
            : "-";
        public string Error => Delivery.LastError ?? string.Empty;
    }

    private static class NotificationTypesSnapshot
    {
        public const string SnapshotFailed = LitSSHmcp.Core.Models.NotificationTypes.SnapshotFailed;
        public const string HealthWarning = LitSSHmcp.Core.Models.NotificationTypes.HealthWarning;
        public const string HealthCritical = LitSSHmcp.Core.Models.NotificationTypes.HealthCritical;
        public const string AiTaskSucceeded = LitSSHmcp.Core.Models.NotificationTypes.AiTaskSucceeded;
        public const string AiTaskFailed = LitSSHmcp.Core.Models.NotificationTypes.AiTaskFailed;
    }
}
