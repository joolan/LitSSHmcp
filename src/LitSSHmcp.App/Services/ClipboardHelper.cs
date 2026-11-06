using System.Runtime.InteropServices;
using System.Windows;

namespace LitSSHmcp.App.Services;

/// <summary>剪贴板写入与敏感内容自动清空；复制失败时短暂重试。</summary>
public static class ClipboardHelper
{
    private static string? _lastSensitiveText;

    public static async Task<bool> SetSensitiveTextAsync(string text, TimeSpan? clearAfter = null)
    {
        if (!await SetTextWithRetryAsync(text))
            return false;

        _lastSensitiveText = text;
        if (clearAfter is { } delay && delay > TimeSpan.Zero)
            _ = ClearIfUnchangedAsync(text, delay);
        return true;
    }

    public static void ClearTrackedIfUnchanged()
    {
        var text = _lastSensitiveText;
        _lastSensitiveText = null;
        if (text is null)
            return;
        try
        {
            if (string.Equals(Clipboard.GetText(), text, StringComparison.Ordinal))
                Clipboard.Clear();
        }
        catch
        {
            // 剪贴板被其他程序占用时忽略
        }
    }

    private static async Task<bool> SetTextWithRetryAsync(string text)
    {
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (COMException)
            {
                if (attempt < 10)
                    await Task.Delay(40);
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    private static async Task ClearIfUnchangedAsync(string text, TimeSpan delay)
    {
        await Task.Delay(delay);
        try
        {
            if (string.Equals(Clipboard.GetText(), text, StringComparison.Ordinal))
                Clipboard.Clear();
        }
        catch
        {
            // 剪贴板被其他程序占用时忽略
        }
    }
}
