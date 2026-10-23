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
        // 确定性字符频率 → 256 维向量(相同文本→相同向量;完全不同文本→近似正交)
        var vector = new float[Dimensions];
        var normalized = text.Trim().ToLowerInvariant();
        for (var i = 0; i < normalized.Length; i++)
        {
            vector[i % Dimensions] += normalized[i]; // 字符频率
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
