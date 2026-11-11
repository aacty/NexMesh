namespace NexMesh.AI.Tools;

/// <summary>
/// 哈希嵌入兜底(开发/测试):无真实语义,相同文本→相同向量,不同文本→不同向量。
/// 仅保证缓存去重可用(同题不重复调模型/召回确定性);生产请注册真实 ISemanticEmbedder
/// (如 text-embedding-3-small)。位于 AI 核心——ToolSearch 与 SemanticCache 共用同一兜底。
/// </summary>
public sealed class HashEmbedder : ISemanticEmbedder
{
    private const int Dimensions = 256;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        // 确定性嵌入:字符频率 + 内容哈希折叠(相同文本→相同向量)。
        // SSS+交叉审核修复:纯位置分桶在 ≥257 字符可构造逐位相同向量(半块交换/分桶和相等),
        // 折叠 64 位 FNV 内容哈希入前 8 维——任意不同文本向量几乎必然不同。
        var vector = new float[Dimensions];
        var normalized = text.Trim().ToLowerInvariant();
        for (var i = 0; i < normalized.Length; i++)
        {
            vector[i % Dimensions] += normalized[i]; // 字符频率
        }
        ulong hash = 14695981039346656037UL;
        foreach (var ch in normalized)
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }
        for (var k = 0; k < 8; k++)
        {
            vector[k] += ((hash >> (k << 3)) & 0xFF) + 1; // 字节折叠(+1 避免零贡献)
        }
        // L2 归一化(余弦度量下向量长度归一)
        double norm = 0;
        foreach (var v in vector)
        {
            norm += v * v;
        }
        if (norm > 0)
        {
            var scale = 1.0 / Math.Sqrt(norm);
            for (var i = 0; i < Dimensions; i++)
            {
                vector[i] = (float)(vector[i] * scale);
            }
        }
        return Task.FromResult(vector);
    }
}
