using System.Net.NetworkInformation;

namespace LitSSHmcp.App.Services;

/// <summary>
/// 本机网络可用性（按网卡状态判断）。仅用于互联网资源的快速失败提示/恢复重试；
/// 内网 Jenkins/K8s 仍应以请求超时和错误退避为准。
/// </summary>
public static class NetworkStatus
{
    private static readonly object Gate = new();
    private static bool _started;

    public static bool IsAvailable { get; private set; } = NetworkInterface.GetIsNetworkAvailable();

    public static event Action<bool>? AvailabilityChanged;

    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_started)
                return;
            _started = true;
            NetworkChange.NetworkAvailabilityChanged += (_, e) => Update(e.IsAvailable);
        }

        Update(NetworkInterface.GetIsNetworkAvailable());
    }

    private static void Update(bool available)
    {
        if (IsAvailable == available)
            return;

        IsAvailable = available;
        try
        {
            AvailabilityChanged?.Invoke(available);
        }
        catch
        {
            // 订阅方异常不影响网络状态更新
        }
    }
}
