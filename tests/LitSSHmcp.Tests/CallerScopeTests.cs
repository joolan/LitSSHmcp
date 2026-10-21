using LitSSHmcp.Core.Services.Security;
using Xunit;

namespace LitSSHmcp.Tests;

public class CallerScopeTests
{
    [Fact]
    public void Non_enforced_desktop_keeps_global_history_and_sessions()
    {
        var scope = new CallerScope(Enforced: false, UserId: "alice", IsAdmin: false);

        var (userIds, historyError) = scope.AuditUserFilter(allUsers: false);
        var (owner, sessionError) = scope.SessionOwnerFilter(allUsers: false);

        Assert.Null(userIds);
        Assert.Null(historyError);
        Assert.Null(owner);
        Assert.Null(sessionError);
    }

    [Fact]
    public void Enforced_user_is_limited_to_self_even_without_allUsers()
    {
        var scope = new CallerScope(Enforced: true, UserId: "alice", IsAdmin: false);

        var (userIds, error) = scope.AuditUserFilter(allUsers: false);
        var (owner, sessionError) = scope.SessionOwnerFilter(allUsers: false);

        Assert.Equal(new[] { "alice" }, userIds);
        Assert.Null(error);
        Assert.Equal("alice", owner);
        Assert.Null(sessionError);
    }

    [Fact]
    public void Enforced_user_cannot_request_all_users()
    {
        var scope = new CallerScope(Enforced: true, UserId: "alice", IsAdmin: false);

        var (userIds, error) = scope.AuditUserFilter(allUsers: true);
        var (owner, sessionError) = scope.SessionOwnerFilter(allUsers: true);

        Assert.Null(userIds);
        Assert.Contains("仅管理员", error);
        Assert.Null(owner);
        Assert.Contains("仅管理员", sessionError);
    }

    [Fact]
    public void Admin_defaults_to_self_and_can_explicitly_see_all()
    {
        var scope = new CallerScope(Enforced: true, UserId: "7", IsAdmin: true);

        Assert.Equal(new[] { "7" }, scope.AuditUserFilter(allUsers: false).UserIds);
        Assert.Equal("7", scope.SessionOwnerFilter(allUsers: false).OwnerUserId);

        Assert.Null(scope.AuditUserFilter(allUsers: true).UserIds);
        Assert.Null(scope.AuditUserFilter(allUsers: true).Error);
        Assert.Null(scope.SessionOwnerFilter(allUsers: true).OwnerUserId);
        Assert.Null(scope.SessionOwnerFilter(allUsers: true).Error);
    }

    [Fact]
    public void Enforced_without_identity_fails_closed()
    {
        var scope = new CallerScope(Enforced: true, UserId: null, IsAdmin: false);

        var (userIds, error) = scope.AuditUserFilter(allUsers: false);
        var (owner, sessionError) = scope.SessionOwnerFilter(allUsers: false);

        Assert.Null(userIds);
        Assert.Contains("无法确定当前用户", error);
        Assert.Null(owner);
        Assert.Contains("无法确定当前用户", sessionError);
    }
}
