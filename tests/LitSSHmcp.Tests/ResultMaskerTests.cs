using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using Xunit;

namespace LitSSHmcp.Tests;

public class ResultMaskerTests
{
    private static List<Dictionary<string, object?>> OneRow(string column, object? value) =>
        new() { new Dictionary<string, object?> { [column] = value } };

    [Fact]
    public void Masks_matched_column_with_full_mode_by_default()
    {
        var columns = new[] { "id", "phone" };
        var rows = new List<Dictionary<string, object?>> { new() { ["id"] = 1, ["phone"] = "13800138000" } };

        ResultMasker.Apply(columns, rows, new MaskingConfig { Rules = new[] { new MaskRule { Column = "phone" } } });

        Assert.Equal("***", rows[0]["phone"]);
        Assert.Equal(1, rows[0]["id"]);
    }

    [Fact]
    public void Phone_mode_keeps_last_four()
    {
        var rows = OneRow("phone", "13800138000");
        ResultMasker.Apply(new[] { "phone" }, rows, new MaskingConfig { Rules = new[] { new MaskRule { Column = "phone", Mode = "phone" } } });
        Assert.Equal("*******8000", rows[0]["phone"]);
    }

    [Fact]
    public void Last4_mode_keeps_tail()
    {
        Assert.Equal("********1234", ResultMasker.Mask("id-1234-1234", "last4"));
    }

    [Fact]
    public void Email_mode_masks_local_part()
    {
        Assert.Equal("a***@example.com", ResultMasker.Mask("alice@example.com", "email"));
        Assert.Equal("***", ResultMasker.Mask("not-an-email", "email"));
    }

    [Fact]
    public void Column_matching_is_regex_and_case_insensitive()
    {
        var rows = OneRow("Mobile", "13800138000");
        ResultMasker.Apply(new[] { "Mobile" }, rows,
            new MaskingConfig { Rules = new[] { new MaskRule { Column = "phone|mobile", Mode = "full" } } });
        Assert.Equal("***", rows[0]["Mobile"]);
    }

    [Fact]
    public void No_rules_leaves_values_untouched()
    {
        var rows = OneRow("phone", "13800138000");
        ResultMasker.Apply(new[] { "phone" }, rows, new MaskingConfig());
        Assert.Equal("13800138000", rows[0]["phone"]);
    }

    [Fact]
    public void Null_values_are_untouched()
    {
        var columns = new[] { "phone" };
        var rows = new List<Dictionary<string, object?>> { new() { ["phone"] = null } };
        ResultMasker.Apply(columns, rows, new MaskingConfig { Rules = new[] { new MaskRule { Column = "phone" } } });
        Assert.Null(rows[0]["phone"]);
    }

    [Fact]
    public void Invalid_regex_is_ignored_without_throwing()
    {
        var rows = OneRow("phone", "13800138000");
        ResultMasker.Apply(new[] { "phone" }, rows,
            new MaskingConfig { Rules = new[] { new MaskRule { Column = "(" } } });
        Assert.Equal("13800138000", rows[0]["phone"]);
    }
}
