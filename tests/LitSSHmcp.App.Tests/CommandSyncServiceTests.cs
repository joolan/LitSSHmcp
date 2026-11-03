using System.Collections.ObjectModel;
using System.ComponentModel;
using LitSSHmcp.App.Services;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class CommandSyncServiceTests
{
    private sealed class FakeSession : ICommandSyncParticipant
    {
        private bool _enabled;
        private bool _connected = true;

        public bool IsSyncEnabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSyncEnabled)));
            }
        }

        public bool IsConnected
        {
            get => _connected;
            set
            {
                if (_connected == value) return;
                _connected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsConnected)));
            }
        }

        public List<string> Received { get; } = new();

        public void SendReplicatedInput(string text) => Received.Add(text);

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    [Fact]
    public void Broadcast_reaches_other_enabled_connected_sessions_only()
    {
        var sessions = new ObservableCollection<object>();
        var source = new FakeSession { IsSyncEnabled = true };
        var target = new FakeSession { IsSyncEnabled = true };
        var disabled = new FakeSession();
        var disconnected = new FakeSession { IsSyncEnabled = true, IsConnected = false };
        sessions.Add(source);
        sessions.Add(target);
        sessions.Add(disabled);
        sessions.Add(disconnected);

        var service = new CommandSyncService(sessions);
        service.Broadcast(source, "uptime\r");

        Assert.Equal(new[] { "uptime\r" }, target.Received);
        Assert.Empty(source.Received);
        Assert.Empty(disabled.Received);
        Assert.Empty(disconnected.Received);
    }

    [Fact]
    public void Disabled_source_does_not_broadcast()
    {
        var sessions = new ObservableCollection<object>();
        var source = new FakeSession();
        var target = new FakeSession { IsSyncEnabled = true };
        sessions.Add(source);
        sessions.Add(target);

        var service = new CommandSyncService(sessions);
        service.Broadcast(source, "whoami\r");

        Assert.Empty(target.Received);
    }

    [Fact]
    public void Effective_count_excludes_disconnected_and_removed_sessions()
    {
        var sessions = new ObservableCollection<object>();
        var connected = new FakeSession { IsSyncEnabled = true };
        var disconnected = new FakeSession { IsSyncEnabled = true, IsConnected = false };
        var disabled = new FakeSession();
        sessions.Add(connected);
        sessions.Add(disconnected);
        sessions.Add(disabled);

        var service = new CommandSyncService(sessions);

        Assert.Equal(2, service.EnabledCount);
        Assert.Equal(1, service.EffectiveCount);
        Assert.Equal(3, service.ParticipantCount);

        sessions.Remove(connected);
        Assert.Equal(0, service.EffectiveCount);
    }

    [Fact]
    public void Set_all_toggles_every_participant()
    {
        var sessions = new ObservableCollection<object>();
        var first = new FakeSession();
        var second = new FakeSession();
        sessions.Add(first);
        sessions.Add(second);

        var service = new CommandSyncService(sessions);
        service.SetAll(true);
        Assert.True(first.IsSyncEnabled);
        Assert.True(second.IsSyncEnabled);

        service.SetAll(false);
        Assert.False(first.IsSyncEnabled);
        Assert.False(second.IsSyncEnabled);
    }
}
