using System.IO;
using System.Text;

namespace LitSSHmcp.App.Services;

/// <summary>
/// 读取正在录制的会话日志。记录端（<see cref="SessionLogger"/>）会一直持有写句柄，
/// 因此这里必须用 <see cref="FileShare.ReadWrite"/> 打开，否则 Windows 会报「文件正被另一个进程使用」。
/// </summary>
public static class SessionLogReader
{
    public static string ReadAllText(string path)
    {
        using var stream = OpenShared(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static string[] ReadAllLines(string path)
    {
        using var stream = OpenShared(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines.ToArray();
    }

    private static FileStream OpenShared(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
