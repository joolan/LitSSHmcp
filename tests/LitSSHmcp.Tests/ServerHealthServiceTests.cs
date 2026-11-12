using LitSSHmcp.Core.Services.Snapshot;
using Xunit;

namespace LitSSHmcp.Tests;

public class ServerHealthServiceTests
{
    [Fact]
    public void Healthy_snapshot_scores_full_or_near_full()
    {
        var json = """
        {"sections":{
          "resource":{"status":"ok","data":{"memory":{"usedPercent":30,"swapUsedPercent":0},"disks":[{"usedPercent":40}],"load":{"1m":0.2},"cpu":{"cores":4}}},
          "systemd":{"status":"ok","data":{"failed":[]}},
          "security":{"status":"ok","data":{"summary":{"high":0,"medium":0}}},
          "accounts":{"status":"ok","data":{"summary":{"high":0,"medium":0,"riskyUsers":0}}}
        }}
        """;

        var health = ServerHealthService.Evaluate(json, "srv");

        Assert.True(health.Score >= 90);
        Assert.Equal("healthy", health.Level);
        Assert.Empty(health.Findings);
    }

    [Fact]
    public void High_resource_and_security_risks_lower_score()
    {
        var json = """
        {"sections":{
          "resource":{"status":"ok","data":{"memory":{"usedPercent":96,"swapUsedPercent":90},"disks":[{"usedPercent":97}],"load":{"1m":9},"cpu":{"cores":2}}},
          "systemd":{"status":"ok","data":{"failed":[{"unit":"a"},{"unit":"b"}]}},
          "security":{"status":"ok","data":{"summary":{"high":2,"medium":3},"exposedHighRiskPorts":[{"port":3306}]}},
          "accounts":{"status":"ok","data":{"summary":{"high":1,"medium":0,"riskyUsers":2}}}
        }}
        """;

        var health = ServerHealthService.Evaluate(json, "srv");

        Assert.True(health.Score < 70);
        Assert.Equal("critical", health.Level);
        Assert.Contains(health.Findings, f => f.Title.Contains("内存"));
        Assert.Contains(health.Findings, f => f.Title.Contains("磁盘"));
        Assert.Contains(health.Findings, f => f.Title.Contains("安全"));
    }

    [Fact]
    public void Missing_or_broken_snapshot_is_unknown()
    {
        Assert.Equal("unknown", ServerHealthService.Evaluate(null, "srv").Level);
        Assert.Equal("unknown", ServerHealthService.Evaluate("{broken", "srv").Level);
    }
}
