using LitSSHmcp.Core.Services.Security;
using Xunit;

namespace LitSSHmcp.Tests;

public class SqlFilterServiceTests
{
    private static SqlFilterService Create() => new(new FakeSecurityOptions());

    [Theory]
    [InlineData("SELECT * FROM users")]
    [InlineData("SHOW FULL PROCESSLIST")]
    [InlineData("EXPLAIN SELECT 1")]
    [InlineData("WITH t AS (SELECT 1) SELECT * FROM t")]
    public void Readonly_statements_allowed(string sql)
    {
        Assert.Equal(SqlFilterResult.Allowed, Create().CheckReadOnly(sql));
    }

    [Theory]
    [InlineData("DELETE FROM users")]
    [InlineData("UPDATE users SET a = 1")]
    [InlineData("INSERT INTO users VALUES (1)")]
    [InlineData("SELECT 1; DROP TABLE users")]
    public void Non_readonly_statements_blocked_in_readonly_mode(string sql)
    {
        Assert.Equal(SqlFilterResult.Blocked, Create().CheckReadOnly(sql));
    }

    [Fact]
    public void Select_into_outfile_is_blocked()
    {
        Assert.Equal(SqlFilterResult.Blocked,
            Create().CheckReadOnly("SELECT * FROM users INTO OUTFILE '/tmp/x'"));
    }

    [Fact]
    public void Update_without_where_is_blocked()
    {
        Assert.Equal(SqlFilterResult.Blocked, Create().CheckWrite("UPDATE users SET a = 1"));
    }

    [Fact]
    public void Delete_without_where_is_blocked()
    {
        Assert.Equal(SqlFilterResult.Blocked, Create().CheckWrite("DELETE FROM users"));
    }

    [Fact]
    public void Update_with_where_is_sensitive()
    {
        Assert.Equal(SqlFilterResult.Sensitive, Create().CheckWrite("UPDATE users SET a = 1 WHERE id = 1"));
    }

    [Fact]
    public void Insert_is_sensitive()
    {
        Assert.Equal(SqlFilterResult.Sensitive, Create().CheckWrite("INSERT INTO users (id) VALUES (1)"));
    }

    [Fact]
    public void Drop_table_is_blocked()
    {
        Assert.Equal(SqlFilterResult.Blocked, Create().CheckWrite("DROP TABLE users"));
    }

    [Fact]
    public void Readonly_sql_in_write_mode_is_blocked()
    {
        Assert.Equal(SqlFilterResult.Blocked, Create().CheckWrite("SELECT * FROM users"));
    }

    [Fact]
    public void Multi_statement_is_blocked()
    {
        Assert.Equal(SqlFilterResult.Blocked, Create().CheckWrite("UPDATE users SET a=1 WHERE id=1; DROP TABLE users"));
    }
}
