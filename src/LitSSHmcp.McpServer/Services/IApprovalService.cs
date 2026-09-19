using System.Runtime.InteropServices;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;

namespace LitSSHmcp.McpServer.Services;

public interface IApprovalService
{
    Task<bool> RequestApprovalAsync(string serverName, string command, CommandFilterResult filterResult, string? filePath = null);
}

public class DesktopApprovalService : IApprovalService
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_YESNO = 0x00000004;
    private const uint MB_ICONQUESTION = 0x00000020;
    private const uint MB_DEFBUTTON2 = 0x00000100;
    private const int IDYES = 6;

    public Task<bool> RequestApprovalAsync(string serverName, string command, CommandFilterResult filterResult, string? filePath = null)
    {
        var message = $"服务器: {serverName}\n";
        message += $"操作: {(filterResult == CommandFilterResult.Sensitive ? "敏感命令" : "文件传输")}\n";
        message += $"命令: {command}";
        if (filePath != null)
            message += $"\n文件: {filePath}";
        message += "\n\n是否允许执行?";

        var result = MessageBox(IntPtr.Zero, message, "LitSSH MCP - 操作确认", MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON2);

        return Task.FromResult(result == IDYES);
    }
}