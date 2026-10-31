using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LitSSHmcp.App.Services;

/// <summary>Windows 外壳相关调用。</summary>
public static class NativeShell
{
    private const int OAIF_ALLOW_REGISTRATION = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENASINFO
    {
        public string pcszFile;
        public string pcszClass;
        public int oaifInFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHOpenWithDialog(IntPtr hwndParent, ref OPENASINFO poainfo);

    /// <summary>在资源管理器中打开目录（不存在时自动创建）。</summary>
    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    /// <summary>用系统默认程序打开文件。</summary>
    public static void OpenFile(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    /// <summary>调起系统「打开方式」对话框，让用户选择程序。返回是否成功弹出。</summary>
    public static bool OpenWith(Window? owner, string path)
    {
        try
        {
            var info = new OPENASINFO
            {
                pcszFile = path,
                pcszClass = string.Empty,
                oaifInFlags = OAIF_ALLOW_REGISTRATION
            };
            var hwnd = owner is not null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero;
            return SHOpenWithDialog(hwnd, ref info) == 0;
        }
        catch
        {
            return false;
        }
    }
}
