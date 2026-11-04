namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    /// <summary>主页本机运行看板（CPU / 内存 / 磁盘 / 网络 / 进程）。</summary>
    public HomeDashboardViewModel HomeDashboard { get; }

    public void DisposeHomeDashboard() => HomeDashboard.Dispose();
}
