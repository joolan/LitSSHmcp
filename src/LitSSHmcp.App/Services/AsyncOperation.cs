namespace LitSSHmcp.App.Services;

/// <summary>
/// UI 事件/命令的异步边界（P3-3）：统一捕获异常并交给 App 全局错误处理，
/// 避免 async void 事件处理器把异常抛到 Dispatcher 造成未观察任务/重复弹窗。
/// </summary>
public static class AsyncOperation
{
    public static void Run(string operation, Func<Task> action, Action<Exception>? onError = null)
        => _ = RunAsync(operation, action, onError);

    public static async Task RunAsync(string operation, Func<Task> action, Action<Exception>? onError = null)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭/任务取消属正常路径。
        }
        catch (Exception ex)
        {
            if (onError is not null)
            {
                try { onError(ex); } catch { /* 错误展示失败不掩盖原始异常 */ }
            }
            else
            {
                App.ReportError(ex, operation);
            }
        }
    }
}
