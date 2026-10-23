using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class SessionCostBudgetTests
{
    [Fact]
    public void Add_accumulates_concurrently_without_lost_updates()
    {
        var budget = new SessionCostBudget(limit: 1, inputPricePerMTok: 1, outputPricePerMTok: 1);

        Parallel.For(0, 1_000, _ => budget.Add(10, 5));

        Assert.Equal(10_000L, budget.InputTokens);
        Assert.Equal(5_000L, budget.OutputTokens);
    }

    [Fact]
    public void Check_uses_shared_total_cost()
    {
        var budget = new SessionCostBudget(limit: 1, inputPricePerMTok: 1, outputPricePerMTok: 1);

        budget.Add(400_000, 400_000);
        Assert.True(budget.Check());
        Assert.Equal(0.8, budget.Cost, precision: 6);

        budget.Add(300_000, 0);
        Assert.False(budget.Check());
    }
}
