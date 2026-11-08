using System.Windows;

namespace LitSSHmcp.App.Services;

/// <summary>系统“减少动画”偏好；也支持 LITSSH_REDUCE_MOTION=1 便于测试。</summary>
public static class MotionPreferences
{
    private static readonly bool ForcedOff =
        Environment.GetEnvironmentVariable("LITSSH_REDUCE_MOTION") is "1" or "true";

    public static bool AnimationsEnabled => !ForcedOff && SystemParameters.ClientAreaAnimation;
}
