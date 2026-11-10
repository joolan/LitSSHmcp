using System.IO;
using LitSSHmcp.Agent;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class AgentSessionSwitchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "litssh-session-switch-" + Guid.NewGuid().ToString("N"));

    public AgentSessionSwitchTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Switching_sessions_does_not_keep_previous_session_turns()
    {
        var configService = new ConfigService(
            Path.Combine(_dir, "config.json"),
            new DpapiSecretProtector());
        var store = new ContextStore(Path.Combine(_dir, "agent.db"));
        await store.InitializeAsync();

        var first = await store.CreateSessionAsync("会话 A");
        await store.AppendMessageAsync(first, "user", "A 的问题");
        await store.AppendMessageAsync(first, "assistant", "A 的回答");

        var second = await store.CreateSessionAsync("会话 B");
        await store.AppendMessageAsync(second, "user", "B 的问题");
        await store.AppendMessageAsync(second, "assistant", "B 的回答");

        var viewModel = new AgentViewModel(configService, store, Path.Combine(_dir, "skills"));
        try
        {
            await viewModel.LoadSessionAsync(first);
            Assert.Contains(viewModel.Turns, turn => turn.UserText == "A 的问题");

            await viewModel.LoadSessionAsync(second);
            Assert.Single(viewModel.Turns);
            Assert.Equal("B 的问题", viewModel.Turns[0].UserText);
            Assert.DoesNotContain(viewModel.Turns, turn => turn.UserText == "A 的问题");
        }
        finally
        {
            await viewModel.DisposeAsync();
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
