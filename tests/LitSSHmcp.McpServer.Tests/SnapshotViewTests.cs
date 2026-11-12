using System.Text.Json;
using System.Text.Json.Nodes;
using LitSSHmcp.Core.Models;
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

    private static string AccountsJson()
    {
        var users = new JsonArray();
        for (var i = 0; i < 60; i++)
        {
            users.Add(new JsonObject
            {
                ["name"] = $"u{i}",
                ["uid"] = i,
                ["shell"] = "/bin/bash",
                ["riskLevel"] = i % 10 == 0 ? "高风险" : "正常",
                ["riskNotes"] = i % 10 == 0 ? "UID=0 但不是 root" : "",
                ["isPrivileged"] = i % 10 == 0,
                ["canLogin"] = "可能（Shell 登录）",
                ["lastLogin"] = "2026-11-10",
                ["authorizedKeyCount"] = i % 3
            });
        }

        var keys = new JsonArray();
        for (var i = 0; i < 30; i++)
        {
            keys.Add(new JsonObject
            {
                ["user"] = $"u{i}",
                ["path"] = $"/home/u{i}/.ssh/authorized_keys",
                ["keyCount"] = 2,
                ["modifiedAt"] = "2026-11-10T00:00:00Z",
                ["mode"] = "600",
                ["keys"] = new JsonArray { new JsonObject { ["fingerprint"] = $"SHA256:{i}" } }
            });
        }

        var findings = new JsonArray();
        for (var i = 0; i < 30; i++)
            findings.Add(new JsonObject { ["severity"] = "high", ["id"] = $"f{i}", ["title"] = "risk" });

        return new JsonObject
        {
            ["collectorVersion"] = 2,
            ["sections"] = new JsonObject
            {
                ["accounts"] = new JsonObject
                {
                    ["status"] = "ok",
                    ["durationMs"] = 10,
                    ["data"] = new JsonObject
                    {
                        ["summary"] = new JsonObject { ["users"] = 60, ["riskyUsers"] = 6 },
                        ["shadowReadable"] = true,
                        ["users"] = users,
                        ["authorizedKeys"] = keys,
                        ["findings"] = findings
                    }
                }
            }
        }.ToJsonString();
    }

    private static JsonNode AccountsView(bool full, string? account = null, string? riskLevel = null) =>
        JsonNode.Parse(JsonSerializer.Serialize(
            SnapshotTools.BuildDataView(AccountsJson(), "accounts", full, account, riskLevel)))!;

    [Fact]
    public void Accounts_section_default_returns_compact_summary_not_all_users()
    {
        var data = AccountsView(false)["sections"]!["accounts"]!["data"]!;
        Assert.NotNull(data["summary"]);
        Assert.Null(data["users"]);
        Assert.Null(data["authorizedKeys"]);
        Assert.True(((JsonArray)data["riskyUsers"]!).Count <= 21);
        Assert.True(((JsonArray)data["keyUsers"]!).Count <= 21);
        Assert.Contains("detail", (string?)data["hint"]);
    }

    [Fact]
    public void Accounts_section_full_contains_full_lists()
    {
        var data = AccountsView(true)["sections"]!["accounts"]!["data"]!;
        Assert.Equal(60, ((JsonArray)data["users"]!).Count);
        Assert.Equal(30, ((JsonArray)data["authorizedKeys"]!).Count);
    }

    [Fact]
    public void Accounts_filter_by_user_returns_only_matching_user()
    {
        var data = AccountsView(false, account: "u0")["sections"]!["accounts"]!["data"]!;
        var users = (JsonArray)data["users"]!;
        Assert.Single(users);
        Assert.Equal("u0", (string?)users[0]!["name"]);
        Assert.Null(data["riskyUsers"]);
    }

    private static JsonNode NginxView(bool full) =>
        JsonNode.Parse(JsonSerializer.Serialize(SnapshotTools.BuildDataView(
            new JsonObject
            {
                ["collectorVersion"] = 2,
                ["sections"] = new JsonObject
                {
                    ["nginx_tls"] = new JsonObject
                    {
                        ["status"] = "ok",
                        ["data"] = new JsonObject
                        {
                            ["serverType"] = "Tengine",
                            ["configHash"] = "abc",
                            ["configPreview"] = "preview",
                            ["effectiveConfig"] = "full-config",
                            ["domains"] = new JsonArray()
                        }
                    }
                }
            }.ToJsonString(), "nginx_tls", full)))!;

    [Fact]
    public void Nginx_section_default_omits_full_effective_config()
    {
        var data = NginxView(false)["sections"]!["nginx_tls"]!["data"]!;
        Assert.Null(data["effectiveConfig"]);
        Assert.Equal("abc", (string?)data["configHash"]);
        Assert.Equal("preview", (string?)data["configPreview"]);
        Assert.Contains("detail", (string?)data["hint"]);
    }

    [Fact]
    public void Nginx_section_full_keeps_effective_config()
    {
        var data = NginxView(true)["sections"]!["nginx_tls"]!["data"]!;
        Assert.Equal("full-config", (string?)data["effectiveConfig"]);
    }

    [Fact]
    public void Accounts_filter_by_high_risk_returns_only_high_risk()
    {
        var data = AccountsView(false, riskLevel: "high")["sections"]!["accounts"]!["data"]!;
        var users = (JsonArray)data["users"]!;
        Assert.Equal(6, users.Count);
        Assert.All(users, u => Assert.Equal("高风险", (string?)u!["riskLevel"]));
    }

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
        Assert.Equal(51, listeners.Count);                              // 默认摘要 50 条 + 1 条省略提示
        Assert.Contains("省略", (string?)listeners[50]);
        Assert.Contains("detail", (string?)portmap["data"]!["hint"]);
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
    public void Summary_build_dto_limits_events_and_recent_history()
    {
        var server = new SshServerConfig { Id = "s", Name = "srv", Host = "host" };
        var snapshot = new ServerSnapshot
        {
            Id = 1,
            ServerId = "s",
            Status = SnapshotStatus.Succeeded,
            DataJson = """{"sections":{}}"""
        };
        var events = new List<SnapshotEvent>
        {
            new() { Kind = "started" },
            new() { Kind = "collector_started" },
            new() { Kind = "collector_completed" },
            new() { Kind = "completed" }
        };
        for (var i = 0; i < 20; i++)
            events.Add(new SnapshotEvent { Kind = "collector_started" });
        var recent = Enumerable.Range(1, 10)
            .Select(i => new ServerSnapshot { Id = i, Status = SnapshotStatus.Succeeded })
            .ToList();

        var dto = SnapshotTools.BuildDto(server, snapshot, events, recent, true, "ok");

        Assert.True(dto.Events!.Count <= 8);
        Assert.DoesNotContain(dto.Events!, e => e.Kind == "collector_started");
        Assert.Equal(5, dto.Recent!.Count);
        Assert.Contains("summary", dto.Hint!);
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
