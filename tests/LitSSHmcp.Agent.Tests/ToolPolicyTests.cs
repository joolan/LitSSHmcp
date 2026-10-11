using LitSSHmcp.Agent;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class ToolPolicyTests
{
    [Theory]
    [InlineData("allow", AgentToolPolicy.Allow)]
    [InlineData("Approve", AgentToolPolicy.Allow)]
    [InlineData("deny", AgentToolPolicy.Deny)]
    [InlineData("Rejected", AgentToolPolicy.Deny)]
    [InlineData("default", AgentToolPolicy.Default)]
    [InlineData("", AgentToolPolicy.Default)]
    [InlineData(null, AgentToolPolicy.Default)]
    [InlineData("weird", AgentToolPolicy.Default)]
    public void Parse_maps_values(string? raw, AgentToolPolicy expected)
        => Assert.Equal(expected, AgentToolPolicyParser.Parse(raw));

    [Theory]
    [InlineData(AgentToolPolicy.Allow, "allow")]
    [InlineData(AgentToolPolicy.Deny, "deny")]
    [InlineData(AgentToolPolicy.Default, "default")]
    public void ToName_maps_values(AgentToolPolicy policy, string expected)
        => Assert.Equal(expected, AgentToolPolicyParser.ToName(policy));
}
