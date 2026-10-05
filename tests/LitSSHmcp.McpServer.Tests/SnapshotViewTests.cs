using System.Text.Json;
using System.Text.Json.Nodes;
using LitSSHmcp.McpServer.Tools;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

public class SnapshotViewTests
{
    private static string SampleJson()
    {
        var listeners = new JsonArray();
        for (var i = 0; i < 300; i++)
            listeners.Add(new JsonObject { ["port"] = i, ["process"] = "svc" });

        var data = new JsonObject
        {
            ["collectorVersion"] = 3,
            ["elevated"] = true,
            ["sections"] = new JsonObject
            {
                ["resource"] = new JsonObject
                {
                    ["status"] = "ok",
                    ["durationMs"] = 12.3,
                    ["error"] = null,
                    ["note"] = null,
                    ["data"] = new JsonObject
                    {
                        ["load"] = new JsonObject { ["runnable"] = 1, ["total"] = 4 },
                        ["memory"] = new JsonObject { ["usedPercent"] = 42.5 },
                        ["disks"] = new JsonArray { new JsonObject { ["mount"] = "/", ["usedPercent"] = 80 } },
                        ["topByCpu"] = new JsonArray { new JsonObject { ["pid"] = "1", ["command"] = new string('x', 500) } },
                        ["riskLevel"] = "warn"
                    }
                },
                ["portmap"] = new JsonObject
                {
                    ["status"] = "ok",
                    ["durationMs"] = 5,
                    ["data"] = new JsonObject
                    {
                        ["counts"] = new JsonObject { ["tcp"] = 10, ["udp"] = 2 },
                        ["listeners"] = listeners
                    }
                }
            }
        };
        return data.ToJsonString();
    }

    private static JsonNode View(string? section, bool full) =>
        JsonNode.Parse(JsonSerializer.Serialize(
            SnapshotTools.BuildDataView(SampleJson(), section, full)))!;

    [Fact]
    public void Summary_returns_headline_not_full_data()
    {
        var root = View(null, false);
        Assert.Equal("summary", (string?)root["view"]);

        var resource = root["sections"]!["resource"]!;
        Assert.Null(resource["data"]);                                   // 概览不含完整 data
        Assert.True((int)resource["dataChars"]! > 0);
        Assert.Equal(42.5, (double)resource["headline"]!["memory"]!["usedPercent"]!);
        Assert.Equal("warn", (string?)resource["headline"]!["riskLevel"]);
    }

    [Fact]
    public void Summary_truncates_long_strings_and_arrays()
    {
        var root = View(null, false);
        var headline = root["sections"]!["resource"]!["headline"]!;
        var command = (string)headline["topByCpu"]![0]!["command"]!;
        Assert.True(command.Length <= 201);
        Assert.EndsWith("…", command);
    }

    [Fact]
    public void Unknown_section_reports_available_list()
    {
        var root = View("nginx", false);   // 合法名是 nginx_tls，这里故意传未知维度
        Assert.Equal("section", (string?)root["view"]);
        Assert.NotNull(root["error"]);
        var available = (JsonArray)root["available"]!;
        Assert.Contains(available, n => (string?)n == "resource");
    }

    [Fact]
    public void Section_returns_full_node_and_caps_arrays()
    {
        var root = View("portmap", false);
        Assert.Equal("section", (string?)root["view"]);
        var portmap = root["sections"]!["portmap"]!;
        Assert.NotNull(portmap["data"]);                                 // 单维度含完整 data
        var listeners = (JsonArray)portmap["data"]!["listeners"]!;
        Assert.Equal(201, listeners.Count);                             // 200 条 + 1 条省略提示
        Assert.Contains("省略", (string?)listeners[200]);
    }

    [Fact]
    public void Full_returns_all_sections()
    {
        var root = View(null, true);
        Assert.Equal("full", (string?)root["view"]);
        Assert.NotNull(root["sections"]!["resource"]!["data"]);
        Assert.NotNull(root["sections"]!["portmap"]!["data"]);
    }

    [Fact]
    public void Age_seconds_parses_roundtrip_and_rejects_bad_input()
    {
        Assert.True(SnapshotTools.TryAgeSeconds(DateTime.UtcNow.AddSeconds(-5).ToString("O"), out var age));
        Assert.InRange(age, 0, 30);
        Assert.False(SnapshotTools.TryAgeSeconds(null, out _));
        Assert.False(SnapshotTools.TryAgeSeconds("not-a-date", out _));
    }
}
