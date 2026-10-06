using Renci.SshNet;

namespace LitSSHmcp.Core.Services.SSH;

/// <summary>
/// 一条交互式 SSH shell（PTY）会话：由 SSH.NET 的 <see cref="ShellStream"/> 承载。
/// 供 App 的终端标签读写字节、调整窗口尺寸；释放时断开底层连接。
/// </summary>
public sealed class SshShellSession : IAsyncDisposable
{
    private readonly SshClient _client;
    private readonly ShellStream _stream;

    public SshShellSession(SshClient client, ShellStream stream)
    {
        _client = client;
        _stream = stream;
    }

    /// <summary>底层双向流（读服务器输出、写键盘输入）。</summary>
    public ShellStream Stream => _stream;

    /// <summary>连接是否仍然可用。</summary>
    public bool IsConnected
    {
        get
        {
            try { return _client.IsConnected && _stream.CanRead; }
            catch { return false; }
        }
    }

    /// <summary>发送文本（键盘输入）。</summary>
    public void Write(string data)
    {
        if (string.IsNullOrEmpty(data))
            return;
        _stream.Write(data);
        _stream.Flush();
    }

    /// <summary>发送原始字节。</summary>
    public void Write(byte[] data, int offset, int count)
    {
        if (count <= 0)
            return;
        _stream.Write(data, offset, count);
        _stream.Flush();
    }

    /// <summary>通知远端终端窗口尺寸变化（vim/htop 等依赖）。</summary>
    public void Resize(uint columns, uint rows)
    {
        try { _stream.ChangeWindowSize(Math.Max(1u, columns), Math.Max(1u, rows), 0, 0); }
        catch { /* 尺寸调整失败不影响主流程 */ }
    }

    public ValueTask DisposeAsync()
    {
        try { _stream.Dispose(); } catch { /* ignore */ }
        try { _client.Dispose(); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }
}
