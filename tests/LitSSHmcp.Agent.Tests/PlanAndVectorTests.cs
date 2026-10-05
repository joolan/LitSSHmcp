using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class VectorMathTests
{
    [Fact]
    public void Cosine_of_identical_is_one()
    {
        Assert.Equal(1.0, VectorMath.Cosine(new[] { 1f, 2f, 3f }, new[] { 1f, 2f, 3f }), 3);
    }

    [Fact]
    public void Cosine_of_orthogonal_is_zero()
    {
        Assert.Equal(0.0, VectorMath.Cosine(new[] { 1f, 0f }, new[] { 0f, 1f }), 3);
    }

    [Fact]
    public void Cosine_dimension_mismatch_is_zero()
    {
        Assert.Equal(0.0, VectorMath.Cosine(new[] { 1f }, new[] { 1f, 2f }), 3);
    }
}

public class PlanToolsTests
{
    [Fact]
    public void ParsePlan_reads_checkboxes()
    {
        var items = PlanTools.ParsePlan("- [x] 已完成\n- [ ] 待办\n- 普通项");
        Assert.Equal(3, items.Count);
        Assert.True(items[0].Done);
        Assert.Equal("已完成", items[0].Text);
        Assert.False(items[1].Done);
        Assert.False(items[2].Done);
    }

    [Fact]
    public async Task UpdatePlan_invokes_callback()
    {
        IReadOnlyList<PlanItem>? captured = null;
        var tool = PlanTools.Create(items => captured = items);

        var result = await tool.InvokeAsync(new Dictionary<string, object?> { ["steps"] = "- [x] a\n- [ ] b" }, default);

        Assert.NotNull(captured);
        Assert.Equal(2, captured!.Count);
        Assert.Contains("1/2", result!.ToString());
    }
}
