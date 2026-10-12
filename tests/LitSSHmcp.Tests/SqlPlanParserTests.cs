using LitSSHmcp.Core.Services.Sql;
using Xunit;

namespace LitSSHmcp.Tests;

public class SqlPlanParserTests
{
    [Fact]
    public void Parses_postgres_json_plan_tree()
    {
        const string json =
            "[{\"Plan\":{\"Node Type\":\"Seq Scan\",\"Relation Name\":\"users\",\"Total Cost\":35.5," +
            "\"Plan Rows\":1000,\"Plans\":[{\"Node Type\":\"Sort\",\"Total Cost\":40.0,\"Plan Rows\":1000}]}}]";

        var nodes = SqlPlanParser.ParsePostgresJson(json);

        Assert.Single(nodes);
        Assert.Contains("Seq Scan", nodes[0].Name);
        Assert.Contains("users", nodes[0].Name);
        Assert.Contains("cost=35.5", nodes[0].Detail);
        Assert.Contains("rows=1000", nodes[0].Detail);
        Assert.Single(nodes[0].Children);
        Assert.Contains("Sort", nodes[0].Children[0].Name);
    }

    [Fact]
    public void Invalid_or_empty_returns_empty()
    {
        Assert.Empty(SqlPlanParser.ParsePostgresJson("not json"));
        Assert.Empty(SqlPlanParser.ParsePostgresJson(""));
        Assert.Empty(SqlPlanParser.ParsePostgresJson(null));
    }
}
