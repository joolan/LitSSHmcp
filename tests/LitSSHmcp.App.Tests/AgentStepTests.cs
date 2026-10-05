using LitSSHmcp.App.ViewModels;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class AgentStepTests
{
    [Fact]
    public void Tail_reflects_completion_and_raises_notification()
    {
        var step = new AgentStep("ssh_download_file", "{}");
        Assert.Equal("调用中…", step.Tail);

        var changed = new List<string?>();
        step.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        step.Complete(true, "ok", 123);

        Assert.False(step.IsRunning);
        Assert.Equal("123 ms", step.Tail);                 // 完成后不再停留在“调用中…”
        Assert.Contains(nameof(AgentStep.Tail), changed);  // 且发出了 Tail 变更通知（回归：此前缺失）
    }

    [Fact]
    public void RetryVisibility_only_when_failed()
    {
        var step = new AgentStep("t", "");
        step.Complete(true, "ok", 1);
        Assert.Equal(System.Windows.Visibility.Collapsed, step.RetryVisibility);

        step.Complete(false, "err", 1);
        Assert.Equal(System.Windows.Visibility.Visible, step.RetryVisibility);
    }

    [Fact]
    public void Restart_resets_to_running()
    {
        var step = new AgentStep("t", "{}");
        step.Complete(false, "err", 5);
        step.Restart();
        Assert.True(step.IsRunning);
        Assert.Equal("调用中…", step.Tail);
    }
}
