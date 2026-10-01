using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.Services;

/// <summary>删除资产时用于检查/描述引用该节点的关系记录。</summary>
public static class RelationHelper
{
    public static RelationConfig[] Referencing(AppConfig config, string nodeId) =>
        config.Relations.Where(r => r.From == nodeId || r.To == nodeId).ToArray();

    public static string Describe(RelationConfig[] relations, int max = 6)
    {
        var lines = relations.Take(max).Select(r => $"  {r.From}  --{r.Type}-->  {r.To}").ToList();
        if (relations.Length > max)
            lines.Add($"  … 其余 {relations.Length - max} 条");
        return string.Join(Environment.NewLine, lines);
    }
}
