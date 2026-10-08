using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Sync;
using Xunit;

namespace LitSSHmcp.Tests;

public class SyncFileFilterTests
{
    private static SyncTaskConfig Task(string[]? include = null, string[]? exclude = null) => new()
    {
        Name = "t",
        IncludePatterns = include ?? Array.Empty<string>(),
        ExcludePatterns = exclude ?? Array.Empty<string>()
    };

    [Fact]
    public void GlobMatch_supports_star_and_question()
    {
        Assert.True(SyncFileFilter.GlobMatch("*.log", "app.log"));
        Assert.True(SyncFileFilter.GlobMatch("*.log", "APP.LOG"));
        Assert.True(SyncFileFilter.GlobMatch("file?.txt", "file1.txt"));
        Assert.False(SyncFileFilter.GlobMatch("*.log", "app.txt"));
        Assert.False(SyncFileFilter.GlobMatch("", "app.txt"));
    }

    [Fact]
    public void IsIncluded_empty_filters_keeps_all()
        => Assert.True(SyncFileFilter.IsIncluded("a/b.txt", Array.Empty<string>(), Array.Empty<string>()));

    [Fact]
    public void IsIncluded_include_matches_relative_path_or_name()
    {
        var inc = new[] { "*.conf" };
        Assert.True(SyncFileFilter.IsIncluded("nginx/nginx.conf", inc, Array.Empty<string>()));
        Assert.True(SyncFileFilter.IsIncluded("app.conf", inc, Array.Empty<string>()));
        Assert.False(SyncFileFilter.IsIncluded("a/b.txt", inc, Array.Empty<string>()));
    }

    [Fact]
    public void IsIncluded_exclude_wins_over_include()
    {
        var inc = new[] { "*.log" };
        var exc = new[] { "debug.log" };
        Assert.False(SyncFileFilter.IsIncluded("debug.log", inc, exc));
        Assert.True(SyncFileFilter.IsIncluded("app.log", inc, exc));
    }

    [Fact]
    public void Apply_filters_dictionary()
    {
        var files = new Dictionary<string, (long, DateTime)>(StringComparer.Ordinal)
        {
            ["a.txt"] = (1, default),
            ["b.log"] = (2, default),
            ["dir/c.log"] = (3, default)
        };
        var filtered = SyncFileFilter.Apply(files, Task(include: new[] { "*.log" }));
        Assert.Equal(2, filtered.Count);
        Assert.Contains("b.log", filtered.Keys);
        Assert.Contains("dir/c.log", filtered.Keys);
    }
}
