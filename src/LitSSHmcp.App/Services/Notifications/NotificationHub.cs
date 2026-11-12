using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using LitSSHmcp.Core.Services.Trigger;

namespace LitSSHmcp.App.Services.Notifications;

public sealed record NotificationMessage(
    string Type,
    string Source,
    string Title,
    string Message,
    NotificationSeverity Severity,
    string? DedupeKey = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// 通知转发站：收件箱持久化 + 消息类型路由 + App/Webhook 渠道 + 基础限频/去重。
/// 业务生产者只发布 NotificationMessage，不直接调用具体渠道。
/// </summary>
public sealed class NotificationHub
{
    public static NotificationHub Default { get; } = new();

    private readonly IConfigService _configService;
    private readonly string _inboxPath;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly SemaphoreSlim _outboxGate = new(1, 1);
    private readonly NotificationOutboxStore _outbox;
    private readonly LitSSHmcp.Core.Services.Notifications.NotificationInboundStore _inbound = new();
    private CancellationTokenSource? _dispatcherCts;
    private Task? _dispatcherTask;
    private readonly ConcurrentDictionary<string, DateTime> _dedupe = new();
    private readonly ConcurrentDictionary<string, List<DateTime>> _rate = new();
    private bool _loaded;

    public NotificationHub(IConfigService? configService = null, string? inboxPath = null, string? outboxPath = null)
    {
        _configService = configService ?? AppServiceFactory.CreateConfigService();
        _inboxPath = string.IsNullOrWhiteSpace(inboxPath)
            ? Path.Combine(ConfigPaths.AppDataDir, "notifications.json")
            : inboxPath;
        _outbox = new NotificationOutboxStore(outboxPath);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _ioGate.WaitAsync(ct);
        try
        {
            if (_loaded)
                return;
            _loaded = true;
            if (!File.Exists(_inboxPath))
                return;

            var json = await File.ReadAllTextAsync(_inboxPath, ct);
            var records = JsonSerializer.Deserialize<List<NotificationRecord>>(json) ?? new();
            foreach (var record in records.OrderBy(r => r.CreatedAt))
                NotificationCenterService.AddCore(record.ToItem());
        }
        catch
        {
            // 收件箱损坏时从空列表开始，不影响应用
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task PublishAsync(NotificationMessage message, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var routeInfo = await MatchRoutesAsync(message, ct);
        var rateLimited = routeInfo
            .Where(route => IsRateLimited(route.Route, route.Channel))
            .Select(route => route.Channel.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (rateLimited.Count > 0 &&
            !routeInfo.Any(r => string.Equals(r.Channel.Type, "app", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        NotificationCenterService.AddCore(new NotificationItem
        {
            Type = message.Type,
            Source = message.Source,
            Title = message.Title,
            Message = message.Message,
            Severity = message.Severity,
            CreatedAt = DateTime.Now
        });
        await PersistAsync(ct);

        foreach (var (route, channel) in routeInfo)
        {
            if (rateLimited.Contains(channel.Id) ||
                string.Equals(channel.Type, "app", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(channel.Type, "desktop", StringComparison.OrdinalIgnoreCase))
                continue;

            if (IsDuplicate(route, message))
                continue;

            try
            {
                await _outbox.EnqueueAsync(message, channel, ct);
                RecordRate(route, channel);
            }
            catch
            {
                // 入队失败不影响其他渠道和收件箱
            }
        }

        _ = ProcessOutboxOnceAsync(CancellationToken.None);
    }

    private async Task<List<(NotificationRouteConfig Route, NotificationChannelConfig Channel)>> MatchRoutesAsync(
        NotificationMessage message, CancellationToken ct)
    {
        AppConfig config;
        try { config = await _configService.LoadConfigAsync(); }
        catch { config = new AppConfig(); }

        var channels = (config.Notifications?.Channels ?? Array.Empty<NotificationChannelConfig>())
            .Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Id))
            .ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        var routes = (config.Notifications?.Routes ?? Array.Empty<NotificationRouteConfig>())
            .Where(r => r.Enabled && MatchesType(r, message.Type))
            .ToList();

        if (routes.Count == 0)
        {
            var app = channels.Values.FirstOrDefault(c => string.Equals(c.Type, "app", StringComparison.OrdinalIgnoreCase))
                      ?? new NotificationChannelConfig { Id = "app", Name = "应用内", Type = "app" };
            return new() { (new NotificationRouteConfig { Id = "default", Name = "默认", Types = new[] { message.Type } }, app) };
        }

        var result = new List<(NotificationRouteConfig, NotificationChannelConfig)>();
        foreach (var route in routes)
        {
            if (!SeverityAllowed(route.MinSeverity, message.Severity))
                continue;
            foreach (var channelId in (route.Channels ?? Array.Empty<string>()).DefaultIfEmpty("app"))
                if (channels.TryGetValue(channelId, out var channel) || channelId == "app")
                    result.Add((route, channels.GetValueOrDefault(channelId) ?? new NotificationChannelConfig
                    {
                        Id = "app", Name = "应用内", Type = "app"
                    }));
        }
        return result;
    }

    private static bool MatchesType(NotificationRouteConfig route, string type)
    {
        if (route.Types is not { Length: > 0 })
            return false;
        return route.Types.Any(pattern =>
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return false;
            if (pattern == "*")
                return true;
            if (pattern.EndsWith(".*", StringComparison.Ordinal))
                return type.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
            return string.Equals(pattern, type, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool SeverityAllowed(string? minSeverity, NotificationSeverity severity) =>
        Rank(severity) >= Rank(ParseSeverity(minSeverity));

    private static NotificationSeverity ParseSeverity(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "success" => NotificationSeverity.Success,
        "warning" or "warn" => NotificationSeverity.Warning,
        "error" or "critical" => NotificationSeverity.Error,
        _ => NotificationSeverity.Info
    };

    private static int Rank(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Error => 3,
        NotificationSeverity.Warning => 2,
        NotificationSeverity.Success => 1,
        _ => 0
    };

    private bool IsDuplicate(NotificationRouteConfig route, NotificationMessage message)
    {
        if (route.DedupeSeconds <= 0 || string.IsNullOrWhiteSpace(message.DedupeKey))
            return false;
        var key = route.Id + "|" + message.Type + "|" + message.DedupeKey;
        var now = DateTime.UtcNow;
        if (_dedupe.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(route.DedupeSeconds))
            return true;
        _dedupe[key] = now;
        return false;
    }

    private bool IsRateLimited(NotificationRouteConfig route, NotificationChannelConfig channel)
    {
        if (route.RateLimitCount <= 0 || route.RateLimitWindowSeconds <= 0)
            return false;
        var key = route.Id + "|" + channel.Id;
        var now = DateTime.UtcNow;
        var window = TimeSpan.FromSeconds(route.RateLimitWindowSeconds);
        var list = _rate.GetOrAdd(key, _ => new List<DateTime>());
        lock (list)
        {
            list.RemoveAll(t => now - t >= window);
            return list.Count >= route.RateLimitCount;
        }
    }

    private void RecordRate(NotificationRouteConfig route, NotificationChannelConfig channel)
    {
        if (route.RateLimitCount <= 0 || route.RateLimitWindowSeconds <= 0)
            return;
        var list = _rate.GetOrAdd(route.Id + "|" + channel.Id, _ => new List<DateTime>());
        lock (list) list.Add(DateTime.UtcNow);
    }

    private async Task ProcessInboundOnceAsync(CancellationToken ct)
    {
        var pending = await _inbound.GetPendingAsync(100, ct);
        foreach (var item in pending)
        {
            try
            {
                await PublishAsync(new NotificationMessage(
                    item.Type, item.Source, item.Title, item.Body,
                    ParseSeverityString(item.Severity), item.DedupeKey), ct);
                await _inbound.MarkConsumedAsync(item.Id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // 保持未消费，下一轮重试
            }
        }
    }

    private static NotificationSeverity ParseSeverityString(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "success" => NotificationSeverity.Success,
        "warning" or "warn" => NotificationSeverity.Warning,
        "error" or "critical" => NotificationSeverity.Error,
        _ => NotificationSeverity.Info
    };
    public Task<List<NotificationDelivery>> ListDeliveriesAsync(int limit = 200, CancellationToken ct = default) =>
        _outbox.ListRecentAsync(limit, ct);

    public async Task RetryDeliveryAsync(long id, CancellationToken ct = default)
    {
        await _outbox.RetryAsync(id, ct);
        _ = ProcessOutboxOnceAsync(CancellationToken.None);
    }
    /// <summary>启动 Outbox 后台投递循环；App 启动时调用。重复调用安全。</summary>
    public void StartDispatcher()
    {
        if (_dispatcherTask is not null)
            return;
        _dispatcherCts = new CancellationTokenSource();
        _dispatcherTask = Task.Run(() => DispatchLoopAsync(_dispatcherCts.Token));
    }

    private async Task DispatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await ProcessInboundOnceAsync(ct); await ProcessOutboxOnceAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch { /* 单轮失败继续下一轮 */ }
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>处理一批待投递消息；失败按 30s/1m/5m/15m/30m 退避，5 次后进入 dead。</summary>
    public async Task ProcessOutboxOnceAsync(CancellationToken ct = default)
    {
        if (!await _outboxGate.WaitAsync(0, ct))
            return;
        try
        {
            var pending = await _outbox.GetPendingAsync(20, ct);
            foreach (var delivery in pending)
            {
                NotificationChannelConfig? channel = null;
                try
                {
                    var config = await _configService.LoadConfigAsync();
                    channel = (config.Notifications?.Channels ?? Array.Empty<NotificationChannelConfig>())
                        .FirstOrDefault(c => string.Equals(c.Id, delivery.ChannelId, StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    // 配置读取失败时使用入队时的渠道快照
                }
                channel ??= JsonSerializer.Deserialize<NotificationChannelConfig>(delivery.ChannelJson);
                if (channel is null)
                {
                    await _outbox.MarkFailedAsync(delivery.Id, delivery.Attempts + 1,
                        "渠道配置不存在", DateTime.UtcNow.AddMinutes(30), dead: true, ct);
                    continue;
                }
                if (string.Equals(channel.Type, "app", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(channel.Type, "desktop", StringComparison.OrdinalIgnoreCase))
                {
                    await _outbox.MarkSentAsync(delivery.Id, ct);
                    continue;
                }

                try
                {
                    await SendWebhookAsync(channel, new NotificationMessage(
                        delivery.MessageType, delivery.Source, delivery.Title, delivery.Body,
                        delivery.Severity, delivery.DedupeKey), ct);
                    await _outbox.MarkSentAsync(delivery.Id, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var attempts = delivery.Attempts + 1;
                    var dead = attempts >= 5;
                    var delay = TimeSpan.FromSeconds(Math.Min(1800, Math.Pow(2, attempts - 1) * 30));
                    await _outbox.MarkFailedAsync(delivery.Id, attempts, ex.Message,
                        DateTime.UtcNow.Add(delay), dead, ct);
                }
            }
        }
        finally
        {
            _outboxGate.Release();
        }
    }
    private static async Task SendWebhookAsync(NotificationChannelConfig channel,
        NotificationMessage message, CancellationToken ct)
    {
        var action = new TriggerAction
        {
            Type = TriggerActionType.Webhook,
            Url = channel.Url,
            Secret = channel.Secret,
            TimeoutSeconds = Math.Max(1, channel.TimeoutSeconds),
            Headers = channel.Headers ?? new Dictionary<string, string>(),
            WebhookTemplate = channel.Type.ToLowerInvariant() switch
            {
                "dingtalk" => TriggerWebhookTemplate.DingTalk,
                "wecom" => TriggerWebhookTemplate.WeCom,
                "feishu" => TriggerWebhookTemplate.Feishu,
                _ => TriggerWebhookTemplate.Generic
            }
        };
        var vars = new Dictionary<string, string>
        {
            ["trigger"] = message.Title,
            ["result"] = message.Severity.ToString(),
            ["cause"] = "notification",
            ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["date"] = DateTime.Now.ToString("yyyy-MM-dd"),
            ["notice"] = message.Message,
            ["steps"] = message.Message
        };
        var context = new TriggerActionContext
        {
            Trigger = new TriggerConfig { Name = message.Source },
            Action = action,
            Vars = vars
        };
        var result = await WebhookSender.SendAsync(context, ct);
        if (!result.Success)
            throw new InvalidOperationException(result.Error ?? "webhook failed");
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        await _ioGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_inboxPath)!);
            var records = NotificationCenterService.Items
                .Select(NotificationRecord.FromItem)
                .ToList();
            await File.WriteAllTextAsync(_inboxPath,
                JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = false }), ct);
        }
        catch
        {
            // 持久化失败不阻断通知展示
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>设置页测试发送：app 渠道直接写收件箱，Webhook 类渠道直接发送。</summary>
    public async Task TestChannelAsync(NotificationChannelConfig channel, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var message = new NotificationMessage(
            NotificationTypes.Generic,
            "通知中心",
            "测试通知",
            $"渠道「{channel.Name}」测试消息，时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            NotificationSeverity.Info,
            "test-" + channel.Id + "-" + DateTime.UtcNow.Ticks);
        if (string.Equals(channel.Type, "app", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(channel.Type, "desktop", StringComparison.OrdinalIgnoreCase))
        {
            NotificationCenterService.AddCore(new NotificationItem
            {
                Type = message.Type,
                Source = message.Source,
                Title = message.Title,
                Message = message.Message,
                Severity = message.Severity,
                CreatedAt = DateTime.Now
            });
            await PersistAsync(ct);
            return;
        }
        await SendWebhookAsync(channel, message, ct);
    }

    internal Task PersistStateAsync(CancellationToken ct = default) => PersistAsync(ct);

    private sealed class NotificationRecord
    {
        public string? Type { get; set; }
        public string? Source { get; set; }
        public string? Title { get; set; }
        public string? Message { get; set; }
        public NotificationSeverity Severity { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsRead { get; set; }

        public static NotificationRecord FromItem(NotificationItem item) => new()
        {
            Type = item.Type,
            Source = item.Source,
            Title = item.Title,
            Message = item.Message,
            Severity = item.Severity,
            CreatedAt = item.CreatedAt,
            IsRead = item.IsRead
        };

        public NotificationItem ToItem() => new()
        {
            Type = Type ?? NotificationTypes.Generic,
            Source = Source ?? "应用",
            Title = Title ?? "通知",
            Message = Message ?? string.Empty,
            Severity = Severity,
            CreatedAt = CreatedAt,
            IsRead = IsRead
        };
    }
}
