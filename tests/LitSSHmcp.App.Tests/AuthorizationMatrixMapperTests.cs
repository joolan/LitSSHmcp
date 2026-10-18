using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>
/// 「安全设置 → Web 授权矩阵」行 ↔ <c>AuthorizationConfig</c> 映射（保存/加载路径）的针对性测试。
/// 行→规则：空用户草稿行丢弃、目标混合分隔符分割、级别按索引映射（宁严勿松）；
/// 规则→行：目标空格连接、exec 索引1；往返保持语义。
/// </summary>
public class AuthorizationMatrixMapperTests
{
    [Fact]
    public void ToRules_MapsRowFields()
    {
        var rules = SecuritySettingsViewModel.ToRules(new[]
        {
            new AuthzRuleRow { UserText = "admin", TargetsText = "ssh:* ds:ds-1", LevelIndex = 1 },
            new AuthzRuleRow { UserText = "alice", TargetsText = "ssh:s-1", LevelIndex = 0 }
        });

        Assert.Equal(2, rules.Length);
        Assert.Equal("admin", rules[0].User);
        Assert.Equal(new[] { "ssh:*", "ds:ds-1" }, rules[0].Targets);
        Assert.Equal("exec", rules[0].Level);
        Assert.Equal("read", rules[1].Level);
    }

    [Fact]
    public void ToRules_DropsBlankUserRows()
    {
        var rules = SecuritySettingsViewModel.ToRules(new[]
        {
            new AuthzRuleRow { UserText = "", TargetsText = "ssh:*" },
            new AuthzRuleRow { UserText = "   ", TargetsText = "ssh:*" },
            new AuthzRuleRow { UserText = "bob", TargetsText = "ssh:s-1" }
        });

        Assert.Single(rules);
        Assert.Equal("bob", rules[0].User);
    }

    [Fact]
    public void ToRules_SplitsTargetsByMixedSeparators()
    {
        var rules = SecuritySettingsViewModel.ToRules(new[]
        {
            new AuthzRuleRow { UserText = "u", TargetsText = "ssh:a,ds:b; ssh:c\t ds:d" }
        });

        Assert.Equal(new[] { "ssh:a", "ds:b", "ssh:c", "ds:d" }, rules[0].Targets);
    }

    [Fact]
    public void ToRules_UnknownLevelIndexFallsBackToRead()
    {
        var rules = SecuritySettingsViewModel.ToRules(new[]
        {
            new AuthzRuleRow { UserText = "u", TargetsText = "*", LevelIndex = 7 }
        });

        Assert.Equal("read", rules[0].Level);
    }

    [Fact]
    public void ToRows_MapsRulesBackToEditableRows()
    {
        var rows = SecuritySettingsViewModel.ToRows(new AuthorizationConfig
        {
            Enabled = true,
            Users = new[]
            {
                new AuthorizationUserRule { User = "admin", Targets = new[] { "ssh:*", "ds:d-1" }, Level = "exec" },
                new AuthorizationUserRule { User = "bob", Targets = new[] { "ssh:s-1" }, Level = "read" },
                new AuthorizationUserRule { User = "x", Targets = new string[0], Level = "bogus" }
            }
        });

        Assert.Equal(3, rows.Length);
        Assert.Equal("ssh:* ds:d-1", rows[0].TargetsText);
        Assert.Equal(1, rows[0].LevelIndex);
        Assert.Equal(0, rows[1].LevelIndex);
        Assert.Equal(string.Empty, rows[2].TargetsText);
        Assert.Equal(0, rows[2].LevelIndex); // 未知级别按 read（宁严勿松）
    }

    [Fact]
    public void ToRows_NullPolicyOrEmptyUsersYieldEmpty()
    {
        Assert.Empty(SecuritySettingsViewModel.ToRows(null));
        Assert.Empty(SecuritySettingsViewModel.ToRows(new AuthorizationConfig()));
    }

    [Fact]
    public void RoundTrip_KeepsRuleSemantics()
    {
        var original = new[]
        {
            new AuthzRuleRow { UserText = "admin", TargetsText = "ssh:* ds:*", LevelIndex = 1 },
            new AuthzRuleRow { UserText = "alice", TargetsText = "ssh:s-1 ds:d-1", LevelIndex = 0 }
        };

        var rules = SecuritySettingsViewModel.ToRules(original);
        var rows = SecuritySettingsViewModel.ToRows(new AuthorizationConfig { Users = rules });
        var again = SecuritySettingsViewModel.ToRules(rows);

        Assert.Equal(rules.Select(r => (r.User, r.Level, string.Join(' ', r.Targets))),
                     again.Select(r => (r.User, r.Level, string.Join(' ', r.Targets))));
    }
}
