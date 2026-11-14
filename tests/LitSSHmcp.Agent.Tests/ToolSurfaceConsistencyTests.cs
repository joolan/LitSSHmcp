using LitSSHmcp.Agent;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.Agent.Tests;

public class ToolSurfaceConsistencyTests
{
    [Fact]
    public void Shared_routing_table_filters_by_enabled_groups()
    {
        var all = ToolRoutingCatalog.ForGroups(null).Select(r => r.Tools).ToArray();
        Assert.Contains(all, t => t.Contains("job_start", StringComparison.Ordinal));

        var noJobs = ToolRoutingCatalog.ForGroups(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ToolGroups.Ssh, ToolGroups.Topology
        }).Select(r => r.Tools).ToArray();
        Assert.DoesNotContain(noJobs, t => t.Contains("job_start", StringComparison.Ordinal));
    }

    [Fact]
    public void Prompt_routing_uses_shared_catalog_and_respects_groups()
    {
        var prompt = SystemPromptBuilder.Build(null, null, null,
            availableGroups: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ToolGroups.Ssh, ToolGroups.Jobs
            });
        Assert.Contains("fleet_snapshot_refresh", prompt);
        Assert.Contains("fleet_inventory", prompt);
        Assert.DoesNotContain("mysql_query", prompt);
    }

    [Fact]
    public void Prompt_operation_list_comes_from_job_operation_catalog()
    {
        var prompt = SystemPromptBuilder.Build(null, null, null);
        Assert.All(JobOperationPolicies.All,
            policy => Assert.Contains($"Job `{policy.Operation}`", prompt, StringComparison.Ordinal));
    }

    [Fact]
    public void Hidden_local_tools_are_not_in_ops_or_record_entrypoints()
    {
        var hidden = AgentRuntime.ModelHiddenLocalToolNames;
        Assert.Equal(LocalToolCatalog.HiddenFromModel, hidden);
        Assert.Contains("fleet_targets", hidden);
        Assert.Contains("ai_task_list", hidden);
        Assert.Contains("ops_incident_write", hidden);
        Assert.Contains("ops_incident_list", hidden);
        Assert.Contains("ops_diagnosis_write", hidden);
        Assert.Contains("ops_memory_write", hidden);

        Assert.DoesNotContain("ops_record", hidden);
        Assert.DoesNotContain("ops_memory_list", hidden);
        Assert.DoesNotContain("ops_insight", hidden);
        Assert.True(ToolProfileFilter.AllowsLocal("ops_record", AgentToolProfile.Ops));
        Assert.False(ToolProfileFilter.AllowsLocal("ops_memory_write", AgentToolProfile.Chat));
        Assert.True(LocalToolCatalog.IsHiddenFromModel("ai_task_list"));
        Assert.False(LocalToolCatalog.IsHiddenFromModel("ops_record"));
        Assert.Contains("ops_record", LocalToolCatalog.OpsVisible);
        Assert.DoesNotContain("ai_task_list", LocalToolCatalog.OpsVisible);
        Assert.Contains("ops_insight", LocalToolCatalog.ChatVisible);
        Assert.DoesNotContain("fleet_change", LocalToolCatalog.ChatVisible);
    }
}
