using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Notifications;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

/// <summary>通知渠道与消息路由配置。修改后保存到 config.json，secret 由 ConfigService 做 DPAPI 加密。</summary>
public partial class NotificationSettingsWindow : FluentWindow
{
    private readonly IConfigService _configService;
    private readonly ObservableCollection<NotificationChannelConfig> _channels = new();
    private readonly ObservableCollection<NotificationRouteConfig> _routes = new();
    private AppConfig _config = new();
    private NotificationChannelConfig? _editingChannel;
    private NotificationRouteConfig? _editingRoute;

    public NotificationSettingsWindow(IConfigService? configService = null)
    {
        InitializeComponent();
        _configService = configService ?? AppServiceFactory.CreateConfigService();
        ChannelList.ItemsSource = _channels;
        RouteList.ItemsSource = _routes;
        Loaded += (_, _) => AsyncOperation.Run("加载通知设置", LoadAsync);
    }

    private async Task LoadAsync()
    {
        try
        {
            _config = await _configService.LoadConfigAsync();
            _channels.Clear();
            foreach (var channel in _config.Notifications?.Channels ?? Array.Empty<NotificationChannelConfig>())
                _channels.Add(channel);
            _routes.Clear();
            foreach (var route in _config.Notifications?.Routes ?? Array.Empty<NotificationRouteConfig>())
                _routes.Add(route);

            if (_channels.Count == 0)
                _channels.Add(new NotificationChannelConfig { Id = "app", Name = "应用内", Type = "app" });
            if (_routes.Count == 0)
                _routes.Add(new NotificationRouteConfig
                {
                    Id = "default",
                    Name = "默认通知",
                    Types = new[] { "*" },
                    Channels = new[] { "app" }
                });

            ChannelList.SelectedIndex = 0;
            RouteList.SelectedIndex = 0;
            StatusText.Text = $"渠道 {_channels.Count} 个，规则 {_routes.Count} 条。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "加载失败: " + ex.Message;
        }
    }

    private void OnAddChannel(object sender, RoutedEventArgs e)
    {
        var channel = new NotificationChannelConfig
        {
            Id = "channel-" + (_channels.Count + 1),
            Name = "新通知渠道",
            Type = "webhook"
        };
        _channels.Add(channel);
        ChannelList.SelectedItem = channel;
    }

    private void OnDeleteChannel(object sender, RoutedEventArgs e)
    {
        if (ChannelList.SelectedItem is NotificationChannelConfig channel)
            _channels.Remove(channel);
    }

    private void OnTestChannel(object sender, RoutedEventArgs e)
    {
        if (ChannelList.SelectedItem is not NotificationChannelConfig channel)
            return;
        AsyncOperation.Run("测试通知渠道", async () =>
        {
            try
            {
                await NotificationHub.Default.TestChannelAsync(channel);
                StatusText.Text = $"已通过「{channel.Name}」发送测试通知。";
            }
            catch (Exception ex)
            {
                StatusText.Text = "测试失败: " + ex.Message;
            }
        });
    }

    private void OnChannelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChannelList.SelectedItem is not NotificationChannelConfig channel)
            return;
        if (_editingChannel is not null && !ReferenceEquals(_editingChannel, channel))
            ApplyChannel(_editingChannel);
        _editingChannel = channel;
        ChannelTypeBox.SelectedValue = channel.Type;
        ChannelTemplateBox.SelectedValue = channel.Template;
        ChannelTimeoutBox.Text = channel.TimeoutSeconds.ToString();
        ChannelSecretBox.Password = channel.Secret ?? string.Empty;
    }

    private void OnAddRoute(object sender, RoutedEventArgs e)
    {
        var route = new NotificationRouteConfig
        {
            Id = "route-" + (_routes.Count + 1),
            Name = "新通知规则",
            Types = new[] { NotificationTypes.Generic },
            Channels = new[] { "app" }
        };
        _routes.Add(route);
        RouteList.SelectedItem = route;
    }

    private void OnDeleteRoute(object sender, RoutedEventArgs e)
    {
        if (RouteList.SelectedItem is NotificationRouteConfig route)
            _routes.Remove(route);
    }

    private void OnRouteSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RouteList.SelectedItem is not NotificationRouteConfig route)
            return;
        if (_editingRoute is not null && !ReferenceEquals(_editingRoute, route))
            ApplyRoute(_editingRoute);
        _editingRoute = route;
        RouteTypesBox.Text = string.Join(", ", route.Types);
        RouteChannelsBox.Text = string.Join(", ", route.Channels);
        RouteSeverityBox.SelectedValue = route.MinSeverity;
        RouteRateCountBox.Text = route.RateLimitCount.ToString();
        RouteRateWindowBox.Text = route.RateLimitWindowSeconds.ToString();
        RouteDedupeBox.Text = route.DedupeSeconds.ToString();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        AsyncOperation.Run("保存通知设置", async () =>
        {
            try
            {
                if (_editingChannel is not null) ApplyChannel(_editingChannel);
                if (_editingRoute is not null) ApplyRoute(_editingRoute);

                _config.Notifications = new NotificationConfig
                {
                    Channels = _channels.ToArray(),
                    Routes = _routes.ToArray()
                };
                await _configService.SaveConfigAsync(_config);
                StatusText.Text = "通知设置已保存。";
            }
            catch (Exception ex)
            {
                StatusText.Text = "保存失败: " + ex.Message;
            }
        });
    }

    private void ApplyChannel(NotificationChannelConfig channel)
    {
        channel.Type = (ChannelTypeBox.SelectedValue as string) ?? channel.Type;
        channel.Template = (ChannelTemplateBox.SelectedValue as string) ?? channel.Template;
        if (int.TryParse(ChannelTimeoutBox.Text, out var timeout))
            channel.TimeoutSeconds = Math.Clamp(timeout, 1, 120);
        if (!string.IsNullOrWhiteSpace(ChannelSecretBox.Password))
            channel.Secret = ChannelSecretBox.Password;
    }

    private void ApplyRoute(NotificationRouteConfig route)
    {
        route.Types = Split(RouteTypesBox.Text);
        route.Channels = Split(RouteChannelsBox.Text);
        route.MinSeverity = (RouteSeverityBox.SelectedValue as string) ?? route.MinSeverity;
        if (int.TryParse(RouteRateCountBox.Text, out var count)) route.RateLimitCount = Math.Max(0, count);
        if (int.TryParse(RouteRateWindowBox.Text, out var window)) route.RateLimitWindowSeconds = Math.Max(0, window);
        if (int.TryParse(RouteDedupeBox.Text, out var dedupe)) route.DedupeSeconds = Math.Max(0, dedupe);
    }
    private static string[] Split(string? text) =>
        (text ?? string.Empty)
            .Split(new[] { ',', '，', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
