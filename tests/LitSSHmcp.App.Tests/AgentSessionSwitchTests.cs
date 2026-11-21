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

    [Fact]
    public async Task Long_session_loads_recent_page_and_pages_backwards()
    {
        var configService = new ConfigService(
            Path.Combine(_dir, "config.json"),
            new DpapiSecretProtector());
        var store = new ContextStore(Path.Combine(_dir, "agent.db"));
        await store.InitializeAsync();

        var id = await store.CreateSessionAsync("长会话");
        for (var i = 1; i <= 60; i++)
        {
            await store.AppendMessageAsync(id, "user", $"u{i}");
            await store.AppendMessageAsync(id, "assistant", $"a{i}");
        }

        var viewModel = new AgentViewModel(configService, store, Path.Combine(_dir, "skills"));
        try
        {
            await viewModel.LoadSessionAsync(id);

            // 默认只加载最近 30 轮（ColdStartRecentTurns），UI 只显示最后 10 轮。
            Assert.Equal(30, viewModel.Turns.Count);
            Assert.Equal("u31", viewModel.Turns[0].UserText);
            Assert.True(viewModel.HasMoreHistory);

            // 先展开内存中已加载的轮次（20 -> 10 -> 0），不读库。
            await viewModel.LoadOlderHistoryAsync();
            await viewModel.LoadOlderHistoryAsync();
            Assert.Equal(30, viewModel.Turns.Count);

            // 再向数据库翻一页：前 10 轮被前置，序号重新编号。
            await viewModel.LoadOlderHistoryAsync();
            Assert.Equal(40, viewModel.Turns.Count);
            Assert.Equal("u21", viewModel.Turns[0].UserText);
            Assert.Equal(0, viewModel.Turns[0].UiOrdinal);
            Assert.True(viewModel.HasMoreHistory);
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
