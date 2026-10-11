using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Trigger;
using Xunit;

namespace LitSSHmcp.Tests;

public class TriggerRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "litssh-trigger-test-" + Guid.NewGuid().ToString("N"));
    private readonly TriggerStateStore _state;

    public TriggerRunnerTests()
    {
        _state = new TriggerStateStore(Path.Combine(_dir, "state.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 忽略 */ }
    }

    private TriggerConfig NewTrigger(params TriggerAction[] actions) => new()
    {
        Name = "测试触发事件",
        TargetType = TriggerTarget.Local,
        Actions = actions
    };

    private TriggerAction Webhook(TestHttpServer server) => new()
    {
        Type = TriggerActionType.Webhook,
        Url = server.Url,
        BodyTemplate = "{\"t\":\"{{trigger}}\",\"r\":\"{{result}}\",\"n\":\"{{notice}}\"}"
    };

    [Fact]
    public async Task Webhook_only_pipeline_succeeds_and_saves_state()
    {
        using var server = new TestHttpServer();
        var trigger = NewTrigger(Webhook(server));
        var runner = new TriggerRunner(state: _state);

        var result = await runner.RunAsync(trigger, TriggerRunCause.Manual, new AppConfig());

        Assert.True(result.Success, result.Message);
        var step = Assert.Single(result.Steps);
        Assert.True(step.Success);
        Assert.Equal("HTTP 200", step.Output);
        Assert.Equal(1, server.CallCount);

        var state = _state.Get(trigger.Id);
        Assert.NotNull(state);
        Assert.Equal("ok", state!.LastResult);
        var record = Assert.Single(state.Runs);
        Assert.True(record.Success);
        Assert.Equal("手动", record.Cause);

        // payload 带触发事件名与通知正文
        Assert.Contains("测试触发事件", server.LastBody);
    }

    [Fact]
    public async Task Failure_without_continue_stops_pipeline()
    {
        using var server = new TestHttpServer();
        var trigger = NewTrigger(
            new TriggerAction
            {
                Type = TriggerActionType.Backup,
                SourcePath = Path.Combine(_dir, "不存在的源目录"),
                DestDir = Path.Combine(_dir, "out"),
                ContinueOnError = false
            },
            Webhook(server));
        var runner = new TriggerRunner(state: _state);

        var result = await runner.RunAsync(trigger, TriggerRunCause.Manual, new AppConfig());

        Assert.False(result.Success);
        var step = Assert.Single(result.Steps);
        Assert.False(step.Success);
        Assert.Equal(0, server.CallCount); // 后续 webhook 未执行
        Assert.StartsWith("fail:", result.Message);
    }

    [Fact]
    public async Task Continue_on_error_runs_rest_but_run_still_fails()
    {
        using var server = new TestHttpServer();
        var trigger = NewTrigger(
            new TriggerAction
            {
                Type = TriggerActionType.Backup,
                SourcePath = Path.Combine(_dir, "不存在的源目录"),
                DestDir = Path.Combine(_dir, "out"),
                ContinueOnError = true
            },
            Webhook(server));
        var runner = new TriggerRunner(state: _state);

        var result = await runner.RunAsync(trigger, TriggerRunCause.Manual, new AppConfig());

        Assert.False(result.Success);
        Assert.Equal(2, result.Steps.Count);
        Assert.False(result.Steps[0].Success);
        Assert.True(result.Steps[1].Success);
        Assert.Equal(1, server.CallCount);

        var state = _state.Get(trigger.Id);
        Assert.NotNull(state);
        Assert.StartsWith("fail:", state!.LastResult);
        Assert.Contains("备份:✗", state.Runs[0].Summary);
        Assert.Contains("webhook:✓", state.Runs[0].Summary);
    }

    [Fact]
    public async Task Validation_failure_returns_without_steps()
    {
        var trigger = new TriggerConfig { Name = "", Actions = Array.Empty<TriggerAction>() };
        var runner = new TriggerRunner(state: _state);

        var result = await runner.RunAsync(trigger, TriggerRunCause.Schedule, new AppConfig());

        Assert.False(result.Success);
        Assert.Empty(result.Steps);
        Assert.StartsWith("fail:", result.Message);

        var state = _state.Get(trigger.Id);
        Assert.NotNull(state);
        Assert.Equal("定时", state!.Runs[0].Cause);
    }

    [Fact]
    public async Task Remote_target_with_missing_server_fails()
    {
        using var server = new TestHttpServer();
        var trigger = NewTrigger(Webhook(server));
        trigger.TargetType = TriggerTarget.Remote;
        trigger.ServerId = "no-such-id";
        var runner = new TriggerRunner(state: _state);

        var result = await runner.RunAsync(trigger, TriggerRunCause.Manual, new AppConfig());

        Assert.False(result.Success);
        Assert.Contains("服务器", result.Message);
        Assert.Empty(result.Steps);
    }
}

public class TriggerStateStoreTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "litssh-state-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try { File.Delete(_file); } catch { /* 忽略 */ }
    }

    [Fact]
    public void Save_and_get_roundtrip()
    {
        var store = new TriggerStateStore(_file);
        store.Save("t1", new DateTime(2026, 10, 9, 10, 0, 0), "ok", true, "git:✓", "手动");

        var state = store.Get("t1");
        Assert.NotNull(state);
        Assert.Equal("ok", state!.LastResult);
        Assert.Equal(new DateTime(2026, 10, 9, 10, 0, 0), state.LastRunAt);
        Assert.Single(state.Runs);

        // 新实例（同一路径）也能读到
        var reloaded = new TriggerStateStore(_file).Get("t1");
        Assert.Single(reloaded!.Runs);
        Assert.Equal("手动", reloaded.Runs[0].Cause);
    }

    [Fact]
    public void Runs_are_capped_at_20_newest_first()
    {
        var store = new TriggerStateStore(_file);
        for (var i = 0; i < 25; i++)
            store.Save("t1", new DateTime(2026, 1, 1).AddMinutes(i), "ok", true, $"run{i}", "定时");

        var state = store.Get("t1");
        Assert.Equal(20, state!.Runs.Count);
        Assert.Equal("run24", state.Runs[0].Summary);
        Assert.Equal("run5", state.Runs[^1].Summary);
    }
}
