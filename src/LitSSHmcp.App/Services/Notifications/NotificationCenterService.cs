using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.Services.Notifications;

public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error
}

public sealed class NotificationItem : INotifyPropertyChanged
{
    private bool _isRead;

    public string Type { get; init; } = NotificationTypes.Generic;
    public required string Source { get; init; }
    public required string Title { get; init; }
    public required string Message { get; init; }
    public NotificationSeverity Severity { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public bool IsRead
    {
        get => _isRead;
        set
        {
            if (_isRead == value) return;
            _isRead = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRead)));
        }
    }

    public string TimeText => CreatedAt.ToString("MM-dd HH:mm");
    public string TypeText => string.IsNullOrWhiteSpace(Type) ? NotificationTypes.Generic : Type;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 兼容旧调用的通知入口。新代码应发布 NotificationMessage，由 NotificationHub 负责路由和渠道投递。
/// </summary>
public static class NotificationCenterService
{
    private const int MaxItems = 200;

    public static ObservableCollection<NotificationItem> Items { get; } = new();

    public static int UnreadCount => Items.Count(item => !item.IsRead);
    public static bool HasUnread => UnreadCount > 0;

    public static event Action? Changed;

    public static Task InitializeAsync(CancellationToken ct = default) =>
        NotificationHub.Default.InitializeAsync(ct);

    public static void Add(string source, string title, string message,
        NotificationSeverity severity = NotificationSeverity.Info,
        string type = NotificationTypes.Generic)
    {
        _ = NotificationHub.Default.PublishAsync(new NotificationMessage(
            type, source, title, message, severity));
    }

    public static void MarkAllRead()
    {
        foreach (var item in Items)
            item.IsRead = true;
        Changed?.Invoke();
        _ = NotificationHub.Default.PersistStateAsync();
    }

    public static void Clear()
    {
        Items.Clear();
        Changed?.Invoke();
        _ = NotificationHub.Default.PersistStateAsync();
    }

    public static void OnItemsChanged() => Changed?.Invoke();

    internal static void AddCore(NotificationItem item)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(new Action(() => AddCoreCore(item)));
            return;
        }
        AddCoreCore(item);
    }

    private static void AddCoreCore(NotificationItem item)
    {
        Items.Insert(0, item);
        while (Items.Count > MaxItems)
            Items.RemoveAt(Items.Count - 1);
        Changed?.Invoke();
    }
}
