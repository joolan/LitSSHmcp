using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using LitSSHmcp.Agent;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class AgentQueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "litssh-queue-" + Guid.NewGuid().ToString("N"));

    public AgentQueueTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Queue_and_steer_commands_capture_input_and_clear_queue()
    {
        var configService = new ConfigService(
            Path.Combine(_dir, "config.json"),
            new DpapiSecretProtector());
        var store = new ContextStore(Path.Combine(_dir, "agent.db"));
        await store.InitializeAsync();
        var viewModel = new AgentViewModel(configService, store, Path.Combine(_dir, "skills"));

        var context = (SessionRunContext)typeof(AgentViewModel)
            .GetProperty("CurrentContext", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel)!;
        context.SetState(SessionRunState.Running);

        viewModel.Input = "第一条排队消息";
        viewModel.QueueCommand.Execute(null);

        var queued = Assert.Single(viewModel.QueuedMessages);
        Assert.False(queued.IsSteering);
        Assert.Equal(string.Empty, viewModel.Input);

        viewModel.Input = "第二条插队消息";
        viewModel.SteerCommand.Execute(null);

        Assert.True(viewModel.QueuedMessages.Count == 2);
        Assert.Single(context.SteeringQueue);

        viewModel.ClearQueueCommand.Execute(null);
        Assert.Empty(viewModel.QueuedMessages);
        Assert.Empty(context.SteeringQueue);
    }
    [Fact]
    public void Queue_store_encrypts_text_at_rest_and_round_trips()
    {
        AgentQueueStore.RootOverride = _dir;
        try
        {
            AgentQueueStore.Save(123, new[] { new QueuedAgentMessage("敏感命令：重启服务", isSteering: false) });

            var file = Assert.Single(Directory.GetFiles(_dir, "q-123.json"));
            var raw = File.ReadAllText(file);
            if (OperatingSystem.IsWindows())
            {
                Assert.StartsWith("enc:", raw);
                Assert.DoesNotContain("敏感命令", raw);
            }

            var loaded = AgentQueueStore.Load(123);
            var item = Assert.Single(loaded);
            Assert.Equal("敏感命令：重启服务", item.Text);
        }
        finally
        {
            AgentQueueStore.RootOverride = null;
        }
    }

    [Fact]
    public async Task Background_session_queues_pump_in_parallel()
    {
        var configService = new ConfigService(
            Path.Combine(_dir, "config.json"),
            new DpapiSecretProtector());
        var store = new ContextStore(Path.Combine(_dir, "agent.db"));
        await store.InitializeAsync();
        var viewModel = new AgentViewModel(configService, store, Path.Combine(_dir, "skills"));

        var first = new SessionRunContext(101);
        var second = new SessionRunContext(102);
        first.PendingMessages.Add(new QueuedAgentMessage("会话一：第一条", false));
        first.PendingMessages.Add(new QueuedAgentMessage("会话一：第二条", false));
        second.PendingMessages.Add(new QueuedAgentMessage("会话二：第一条", false));

        var executed = new ConcurrentBag<string>();
        var completed = new ConcurrentBag<(string Title, int Count)>();
        viewModel.BackgroundQueueCompleted += (title, count) => completed.Add((title, count));
        viewModel.QueueTurnRunnerForTest = (turn, _, _) =>
        {
            executed.Add(turn.UserText);
            turn.MarkDone(DateTime.UtcNow);
            return Task.CompletedTask;
        };

        await Task.WhenAll(
            viewModel.TryPumpQueueAsync(first),
            viewModel.TryPumpQueueAsync(second));

        Assert.Equal(3, executed.Count);
        Assert.Contains("会话一：第二条", executed);
        Assert.Contains("会话二：第一条", executed);
        Assert.Empty(first.PendingMessages);
        Assert.Empty(second.PendingMessages);
        Assert.Equal(2, completed.Count);
        Assert.Contains(completed, item => item.Count == 2);
        Assert.True(viewModel.HasCompletedQueue(first.SessionId));
        Assert.True(viewModel.HasCompletedQueue(second.SessionId));
    }
}
