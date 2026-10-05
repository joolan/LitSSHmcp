namespace LitSSHmcp.Agent;

/// <summary>向量相似度工具（纯函数，便于测试）。</summary>
public static class VectorMath
{
    /// <summary>余弦相似度；维度不一致或含零向量时返回 0。</summary>
    public static double Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a.Count == 0 || a.Count != b.Count)
            return 0;

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0 || normB == 0)
            return 0;
        return dot / Math.Sqrt(normA * normB);
    }
}
