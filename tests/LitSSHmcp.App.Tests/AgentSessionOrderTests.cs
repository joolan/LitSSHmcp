using LitSSHmcp.Agent;
using LitSSHmcp.App.ViewModels;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class AgentSessionOrderTests
{
    private static AgentSessionRow Row(long id, string group = "", double order = 0, string updatedAt = "2026-01-01T00:00:00Z") =>
        new(id, "t" + id, updatedAt, updatedAt, Group: group, SortOrder: order);

    [Fact]
    public void Move_to_empty_group_uses_zero()
    {
        var dragged = Row(1);
        var order = AgentSessionOrder.ComputeMoveOrder(new[] { dragged }, dragged, string.Empty, after: null);

        Assert.Equal(0, order);
    }

    [Fact]
    public void Move_to_front_is_before_first_sibling()
    {
        var dragged = Row(9, group: "a", order: 99);
        var first = Row(1, group: "a", order: 10);
        var second = Row(2, group: "a", order: 20);

        var order = AgentSessionOrder.ComputeMoveOrder(new[] { dragged, first, second }, dragged, "a", after: null);

        Assert.Equal(9, order);
    }

    [Fact]
    public void Move_after_sibling_uses_midpoint()
    {
        var dragged = Row(9);
        var first = Row(1, group: "a", order: 10);
        var second = Row(2, group: "a", order: 20);

        var order = AgentSessionOrder.ComputeMoveOrder(new[] { dragged, first, second }, dragged, "a", after: first);

        Assert.Equal(15, order);
    }

    [Fact]
    public void Move_after_last_sibling_extends_range()
    {
        var dragged = Row(9);
        var first = Row(1, group: "a", order: 10);
        var second = Row(2, group: "a", order: 20);

        var order = AgentSessionOrder.ComputeMoveOrder(new[] { dragged, first, second }, dragged, "a", after: second);

        Assert.Equal(21, order);
    }

    [Fact]
    public void Move_after_unknown_sibling_appends_to_end()
    {
        var dragged = Row(9);
        var first = Row(1, group: "a", order: 10);
        var second = Row(2, group: "a", order: 20);

        var order = AgentSessionOrder.ComputeMoveOrder(
            new[] { dragged, first, second }, dragged, "a", after: Row(99, group: "a"));

        Assert.Equal(21, order);
    }

    [Fact]
    public void Move_ignores_sessions_in_other_groups()
    {
        var dragged = Row(9);
        var other = Row(1, group: "b", order: 1);
        var target = Row(2, group: "a", order: 50);

        var order = AgentSessionOrder.ComputeMoveOrder(new[] { dragged, other, target }, dragged, "a", after: null);

        Assert.Equal(49, order);
    }

    [Fact]
    public void Reorder_groups_inserts_before_target()
    {
        var result = AgentSessionOrder.ReorderGroups(new[] { "a", "b", "c" }, "c", "b");

        Assert.Equal(new[] { "a", "c", "b" }, result);
    }

    [Fact]
    public void Reorder_groups_appends_when_target_missing()
    {
        var result = AgentSessionOrder.ReorderGroups(new[] { "a", "b", "c" }, "a", "missing");

        Assert.Equal(new[] { "b", "c", "a" }, result);
    }

    [Theory]
    [InlineData("", "a")]
    [InlineData("a", "a")]
    [InlineData("missing", "a")]
    public void Reorder_groups_rejects_invalid_input(string dragged, string target)
    {
        Assert.Null(AgentSessionOrder.ReorderGroups(new[] { "a", "b" }, dragged, target));
    }

    [Fact]
    public void Reorder_groups_skips_blank_names()
    {
        var result = AgentSessionOrder.ReorderGroups(new[] { "", "a", " ", "b" }, "b", "a");

        Assert.Equal(new[] { "b", "a" }, result);
    }
}
