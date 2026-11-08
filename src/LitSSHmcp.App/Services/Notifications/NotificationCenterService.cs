using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

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

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>应用内通知中心（进程级，UI 线程集合）。</summary>
public static class NotificationCenterService
{
    private const int MaxItems = 200;

    public static ObservableCollection<NotificationItem> Items { get; } = new();

    public static int UnreadCount => Items.Count(item => !item.IsRead);
    public static bool HasUnread => UnreadCount > 0;

    public static event Action? Changed;

    public static void Add(string source, string title, string message,
        NotificationSeverity severity = NotificationSeverity.Info)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => AddCore(source, title, message, severity)));
            return;
        }

        AddCore(source, title, message, severity);
    }

    public static void MarkAllRead()
    {
        foreach (var item in Items)
            item.IsRead = true;
        Changed?.Invoke();
    }

    public static void Clear()
    {
        Items.Clear();
        Changed?.Invoke();
    }

    public static void OnItemsChanged() => Changed?.Invoke();

    private static void AddCore(string source, string title, string message, NotificationSeverity severity)
    {
        Items.Insert(0, new NotificationItem
        {
            Source = string.IsNullOrWhiteSpace(source) ? "应用" : source,
            Title = string.IsNullOrWhiteSpace(title) ? "通知" : title,
            Message = message ?? string.Empty,
            Severity = severity
        });

        while (Items.Count > MaxItems)
            Items.RemoveAt(Items.Count - 1);

        Changed?.Invoke();
    }
}
