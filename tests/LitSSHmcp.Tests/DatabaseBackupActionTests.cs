using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Trigger;
using Xunit;

namespace LitSSHmcp.Tests;

public class DatabaseBackupActionTests
{
    [Fact]
    public void NormalizeTables_splits_and_dedupes()
    {
        var tables = DatabaseBackupAction.NormalizeTables(new[] { "orders, customers", "orders;logs", "  payments  ", "" });
        Assert.Equal(new[] { "orders", "customers", "logs", "payments" }, tables);
    }

    [Fact]
    public void NormalizeTables_null_returns_empty()
        => Assert.Empty(DatabaseBackupAction.NormalizeTables(null));

    [Fact]
    public void BuildFileName_sanitizes_and_adds_extension()
    {
        var gz = DatabaseBackupAction.BuildFileName("每日备份", "订单库", "shop", gzip: true);
        Assert.EndsWith(".sql.gz", gz);
        Assert.StartsWith("每日备份-订单库-shop-", gz);

        var plain = DatabaseBackupAction.BuildFileName("t", "d", "db", gzip: false);
        Assert.EndsWith(".sql", plain);
        Assert.DoesNotContain(".sql.gz", plain);
    }

    [Fact]
    public void MySqlArgs_whole_db_uses_databases_flag()
    {
        var args = DatabaseBackupAction.BuildMysqlArgs("127.0.0.1", 3306, "root", "shop", Array.Empty<string>());
        Assert.Contains("--databases", args);
        Assert.Contains("shop", args);
        Assert.DoesNotContain("--password", string.Join(" ", args));
    }

    [Fact]
    public void MySqlArgs_specific_tables_are_appended_after_db()
    {
        var args = DatabaseBackupAction.BuildMysqlArgs("h", 3306, "u", "shop", new[] { "orders", "customers" });
        Assert.DoesNotContain("--databases", args);
        var idx = args.ToList().IndexOf("shop");
        Assert.True(idx >= 0);
        Assert.Equal("orders", args[idx + 1]);
        Assert.Equal("customers", args[idx + 2]);
    }

    [Fact]
    public void PgArgs_adds_table_flags_and_no_password()
    {
        var args = DatabaseBackupAction.BuildPgArgs("h", 5432, "u", "shop", new[] { "public.orders" });
        var list = args.ToList();
        var i = list.IndexOf("--table");
        Assert.True(i >= 0);
        Assert.Equal("public.orders", list[i + 1]);
        Assert.Contains("--no-password", list);
        Assert.Equal("shop", list[^1]); // dbname 在最后
    }

    [Fact]
    public void MySqlLiteral_escapes_strings_and_formats_types()
    {
        Assert.Equal("NULL", DatabaseBackupAction.MySqlLiteral(null));
        Assert.Equal("42", DatabaseBackupAction.MySqlLiteral(42L));
        Assert.Equal("1", DatabaseBackupAction.MySqlLiteral(true));
        Assert.Equal("'O''Brien'", DatabaseBackupAction.MySqlLiteral("O'Brien").Replace("\\'", "''"));
        Assert.Equal("'a\\\\b'", DatabaseBackupAction.MySqlLiteral("a\\b"));
        Assert.Equal("0x0A0B", DatabaseBackupAction.MySqlLiteral(new byte[] { 10, 11 }));
    }

    [Fact]
    public void PgLiteral_doubles_single_quotes()
    {
        Assert.Equal("NULL", DatabaseBackupAction.PgLiteral(null));
        Assert.Equal("true", DatabaseBackupAction.PgLiteral(true));
        Assert.Equal("'O''Brien'", DatabaseBackupAction.PgLiteral("O'Brien"));
    }

    [Fact]
    public void Identifiers_are_quoted()
    {
        Assert.Equal("`a``b`", DatabaseBackupAction.MySqlQuote("a`b"));
        Assert.Equal("\"a\"\"b\"", DatabaseBackupAction.PgQuote("a\"b"));
    }

    [Fact]
    public void Remote_mysql_command_uses_env_password_and_gzip_redirect()
    {
        var cmd = DatabaseBackupAction.BuildRemoteDumpCommand(
            "mysql", "mysqldump", "s3cret", "10.0.0.9", 3306, "root", "shop",
            new[] { "orders", "customers" }, "/backup/shop.sql.gz", gzip: true);

        Assert.Contains("MYSQL_PWD='s3cret'", cmd);
        Assert.Contains("mysqldump", cmd);
        Assert.Contains("'--host=10.0.0.9'", cmd);
        Assert.Contains("| gzip > '/backup/shop.sql.gz'", cmd);
        Assert.Contains("'orders'", cmd);
        Assert.Contains("'customers'", cmd);
        Assert.DoesNotContain("--databases", cmd);
    }

    [Fact]
    public void Remote_pg_whole_db_command_has_no_gzip_when_disabled()
    {
        var cmd = DatabaseBackupAction.BuildRemoteDumpCommand(
            "postgres", "pg_dump", null, "db.internal", 5432, "postgres", "shop",
            Array.Empty<string>(), "/backup/shop.sql", gzip: false);

        Assert.DoesNotContain("PGPASSWORD", cmd); // 无密码
        Assert.DoesNotContain("| gzip", cmd);
        Assert.Contains("pg_dump", cmd);
        Assert.Contains("--no-password", cmd);
        Assert.EndsWith("> '/backup/shop.sql'", cmd);
    }
}

public class TriggerDatabaseBackupConfigTests
{
    [Fact]
    public void DatabaseBackup_requires_datasource_and_destdir()
    {
        var c = new TriggerConfig
        {
            Name = "db backup",
            Actions = new[] { new TriggerAction { Type = TriggerActionType.DatabaseBackup } }
        };
        var errors = c.Validate();
        Assert.Contains(errors, e => e.Contains("数据库备份") && e.Contains("数据源"));
        Assert.Contains(errors, e => e.Contains("数据库备份") && e.Contains("输出目录"));
    }

    [Fact]
    public void DatabaseBackup_valid_config_has_no_errors()
    {
        var c = new TriggerConfig
        {
            Name = "db backup",
            Actions = new[]
            {
                new TriggerAction
                {
                    Type = TriggerActionType.DatabaseBackup,
                    DatasourceId = "ds1",
                    Database = "shop",
                    DestDir = "D:\\backup"
                }
            }
        };
        Assert.Empty(c.Validate());
    }

    [Fact]
    public void DatabaseBackup_enum_value_is_appended()
    {
        // 保持向后兼容：新增枚举值应在末尾，旧值不受影响
        Assert.Equal(0, (int)TriggerActionType.GitCommitPush);
        Assert.Equal(1, (int)TriggerActionType.Webhook);
        Assert.Equal(2, (int)TriggerActionType.Backup);
        Assert.Equal(3, (int)TriggerActionType.DatabaseBackup);
    }
}
