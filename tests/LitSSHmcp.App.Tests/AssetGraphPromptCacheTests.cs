using System.IO;
using LitSSHmcp.App.Services;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class AssetGraphPromptCacheTests
{
    [Fact]
    public void Cache_hits_same_file_version_and_invalidates_on_change()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "litssh-asset-graph-cache-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{}");
        try
        {
            var stamp = AssetGraphPromptCache.ReadStamp(path);
            AssetGraphPromptCache.Store(path, stamp, "graph-v1");

            Assert.True(AssetGraphPromptCache.TryGet(path, out var cached));
            Assert.Equal("graph-v1", cached);

            // 文件长度变化 → 时间戳不匹配 → 必须重新生成。
            File.AppendAllText(path, " ");
            Assert.False(AssetGraphPromptCache.TryGet(path, out _));

            var newStamp = AssetGraphPromptCache.ReadStamp(path);
            AssetGraphPromptCache.Store(path, newStamp, "graph-v2");
            Assert.True(AssetGraphPromptCache.TryGet(path, out cached));
            Assert.Equal("graph-v2", cached);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }
}
