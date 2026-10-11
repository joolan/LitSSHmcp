using LitSSHmcp.App.ViewModels;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class DbWorkbenchPreviewTests
{
    [Fact]
    public void Update_with_where_builds_count_and_sample()
    {
        var ok = DbWorkbenchViewModel.TryBuildPreview(
            "UPDATE users SET name='x' WHERE id = 1;", out var count, out var sample);

        Assert.True(ok);
        Assert.Equal("SELECT COUNT(*) AS cnt FROM users WHERE id = 1", count);
        Assert.Equal("SELECT * FROM users WHERE id = 1 LIMIT 100", sample);
    }

    [Fact]
    public void Delete_with_where_builds_preview()
    {
        var ok = DbWorkbenchViewModel.TryBuildPreview(
            "DELETE FROM events WHERE created_at < '2020-01-01'", out var count, out var sample);

        Assert.True(ok);
        Assert.Contains("FROM events WHERE", count);
        Assert.Contains("FROM events WHERE", sample);
        Assert.EndsWith("LIMIT 100", sample);
    }

    [Fact]
    public void Update_without_where_is_not_previewable()
    {
        Assert.False(DbWorkbenchViewModel.TryBuildPreview("UPDATE users SET name='x'", out _, out _));
    }

    [Fact]
    public void Insert_is_not_previewable()
    {
        Assert.False(DbWorkbenchViewModel.TryBuildPreview("INSERT INTO users (id) VALUES (1)", out _, out _));
    }

    [Fact]
    public void Schema_qualified_table_is_preserved()
    {
        var ok = DbWorkbenchViewModel.TryBuildPreview(
            "UPDATE public.users SET a=1 WHERE b=2", out var count, out _);

        Assert.True(ok);
        Assert.Contains("FROM public.users WHERE b=2", count);
    }
}
