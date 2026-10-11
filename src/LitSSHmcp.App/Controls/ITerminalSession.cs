namespace LitSSHmcp.App.Controls;

/// <summary>
/// 终端视图（<see cref="TerminalView"/>）所需的会话契约，与后端传输无关
/// （SSH 交互式 shell、本机 ConPTY 等均可实现）。
/// </summary>
public interface ITerminalSession
{
    /// <summary>终端屏幕模型（解析 ANSI/UTF-8，供视图绘制）。</summary>
    TerminalModel Model { get; }

    /// <summary>首次显示时按实际控件尺寸启动后端；已启动则仅同步尺寸。</summary>
    Task EnsureStartedAsync(int cols, int rows);

    /// <summary>把用户键盘输入回写到后端。</summary>
    void SendInput(string text);

    /// <summary>通知后端终端尺寸变化。</summary>
    void Resize(int cols, int rows);
}
