using LitSSHmcp.Agent;
using LitSSHmcp.App.ViewModels;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class ConsolidationCandidateEntryTests
{
    [Fact]
    public void From_parses_title_preview_and_meta()
    {
        var record = new ConsolidationCandidate(7, "incident",
            """{"title":"磁盘满恢复","symptom":"df 100%"}""", "ssh:web01", "raw:1", 0.8, "pending",
            "source=会话:s1;raw=1", "2026-10-26T00:00:00Z", "2026-10-26T00:00:00Z", "", "");

        var entry = ConsolidationCandidateEntry.From(record);

        Assert.Contains("[pending]", entry.Title);
        Assert.Contains("磁盘满恢复", entry.Title);
        Assert.Contains("raw:1", entry.Preview);
        Assert.Contains("#7", entry.Meta);
        Assert.Contains("ssh:web01", entry.Meta);
        Assert.Contains("0.80", entry.Meta);
        Assert.Equal(record.PayloadJson, entry.PayloadJson);
    }

    [Fact]
    public void From_handles_invalid_payload()
    {
        var record = new ConsolidationCandidate(8, "incident", "{not-json}", "", "", 0.5,
            "applied", "", "", "", "incident:9", "");

        var entry = ConsolidationCandidateEntry.From(record);

        Assert.Contains("payload 解析失败", entry.Title);
        Assert.Contains("incident:9", entry.Meta);
    }
}
