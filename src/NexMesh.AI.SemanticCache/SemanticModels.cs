namespace NexMesh.AI.SemanticCache;

/// <summary>语义匹配结果。</summary>
public sealed record SemanticMatch(string Key, string Payload, double Similarity);

/// <summary>
/// 语义向量存储:Add + 余弦相似 Top-K 检索。
/// 内存实现(单实例,开发/测试)/ Redis FT.SEARCH KNN 实现(生产,Redis Stack)。
/// SSS+安全:scope(租户前缀)参与检索隔离——SearchAsync 只匹配同 scope 的条目,杜绝跨租户 PHI 串答。
/// </summary>
public interface ISemanticVectorStore
{
    Task AddAsync(string scope, string key, float[] vector, string payload, TimeSpan? ttl = null, CancellationToken ct = default);
    Task<SemanticMatch?> SearchAsync(string scope, float[] queryVector, double threshold, CancellationToken ct = default);
}

/// <summary>NexMesh:AI:SemanticCache 配置节。</summary>
public sealed class SemanticCacheOptions
{
    public bool Enabled { get; set; } = false;
    /// <summary>余弦相似阈值(0~1):≥ 阈值视为命中。0.85 = 高置信(推荐生产)。</summary>
    public double Threshold { get; set; } = 0.85;
    /// <summary>缓存条目 TTL(秒,0 = 永久)。</summary>
    public int TtlSeconds { get; set; } = 3600;
    /// <summary>Store=Memory(默认)|Redis(Redis Stack FT.SEARCH,需 NexMesh:Cache:Sources 已配连接串)。</summary>
    public string Store { get; set; } = "Memory";
    /// <summary>向量索引前缀(多应用共 Redis 隔离)。</summary>
    public string KeyPrefix { get; set; } = "nexmesh:sem:";
}
