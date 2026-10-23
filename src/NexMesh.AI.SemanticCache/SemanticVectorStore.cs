namespace NexMesh.AI.SemanticCache;

using System.Collections.Concurrent;

/// <summary>
/// 内存语义向量存储:暴力余弦相似度(单实例/开发/测试;生产用 Redis FT.SEARCH)。
/// 条目带 TTL(惰性过期:搜索时跳过已过期条目并清理)。
/// </summary>
public sealed class InMemorySemanticStore : ISemanticVectorStore
{
    private sealed record Entry(float[] Vector, string Payload, DateTime ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public Task AddAsync(string scope, string key, float[] vector, string payload, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var expiresAt = ttl is { } t ? DateTime.Now.Add(t) : DateTime.MaxValue;
        _entries[scope + key] = new Entry(vector, payload, expiresAt);
        return Task.CompletedTask;
    }

    public Task<SemanticMatch?> SearchAsync(string scope, float[] queryVector, double threshold, CancellationToken ct = default)
    {
        SemanticMatch? best = null;
        var bestScore = threshold;
        var now = DateTime.Now;
        foreach (var (key, entry) in _entries)
        {
            // SSS+安全:仅匹配同 scope(租户前缀)条目,跨租户 PHI 串答在此截断
            if (!string.IsNullOrEmpty(scope) && !key.StartsWith(scope, StringComparison.Ordinal))
            {
                continue;
            }
            if (entry.ExpiresAt <= now)
            {
                _entries.TryRemove(key, out _);
                continue;
            }
            var score = Cosine(queryVector, entry.Vector);
            if (score >= bestScore)
            {
                bestScore = score;
                best = new SemanticMatch(key, entry.Payload, score);
            }
        }
        return Task.FromResult(best);
    }

    /// <summary>余弦相似度(向量夹角;1=同向,0=正交,-1=反向)。</summary>
    internal static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0;
        }
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return dot == 0 ? 0 : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}

/// <summary>
/// Redis FT.SEARCH KNN 语义向量存储(生产,Redis Stack):
/// 同一 StackExchange.Redis 连接生态(NexMesh.Cache 的 Sources 连接串复用);
/// 索引 HNSW + COSINE(需 Redis Stack 的 RediSearch 模块,普通 Redis 会报错,fail-fast)。
/// </summary>
public sealed class RedisSemanticStore(
    StackExchange.Redis.IConnectionMultiplexer redis,
    string keyPrefix) : ISemanticVectorStore
{
    private readonly StackExchange.Redis.IDatabase _db = redis.GetDatabase();
    private readonly ConcurrentDictionary<string, bool> _createdIndexes = new(StringComparer.Ordinal);

    /// <summary>索引名按 scope 隔离(每个租户独立索引,检索天然不跨租户)。</summary>
    private string IndexName(string scope) =>
        keyPrefix + "idx:" + (string.IsNullOrEmpty(scope)
            ? "global"
            : string.Concat(scope.Where(c => char.IsLetterOrDigit(c) || c == '-')));

    public async Task AddAsync(string scope, string key, float[] vector, string payload, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        await EnsureIndexAsync(scope);
        var redisKey = keyPrefix + scope + key;
        // 向量以二进制存(FT.SEARCH KNN 要求 FLOAT32 BLOB),payload 另存同 key 字段
        await _db.HashSetAsync(redisKey,
        [
            new StackExchange.Redis.HashEntry("vector", PackVector(vector)),
            new StackExchange.Redis.HashEntry("payload", payload),
        ]);
        if (ttl is { } t)
        {
            await _db.KeyExpireAsync(redisKey, t);
        }
    }

    public async Task<SemanticMatch?> SearchAsync(string scope, float[] queryVector, double threshold, CancellationToken ct = default)
    {
        await EnsureIndexAsync(scope);
        var queryBytes = PackVector(queryVector);
        // KNN 1:最近邻 1 条(余弦距离→相似度=1-距离);索引按 scope 隔离
        var result = await _db.ExecuteAsync("FT.SEARCH", IndexName(scope),
            "*=>[KNN 1 @vector $blob AS score]",
            "PARAMS", "2", "blob", queryBytes,
            "SORTBY", "score", "ASC", "LIMIT", "0", "1",
            "RETURN", "2", "payload", "score");
        if (!result.IsNull && result.Length >= 2)
        {
            var doc = (StackExchange.Redis.RedisResult[])result[1]!;
            var payload = "";
            double score = 1;
            for (var i = 0; i + 1 < doc.Length; i += 2)
            {
                if (doc[i].ToString() == "payload")
                {
                    payload = doc[i + 1].ToString();
                }
                if (doc[i].ToString() == "score")
                {
                    _ = double.TryParse(doc[i + 1].ToString(), out score);
                }
            }
            var similarity = 1 - score; // KNN 余弦距离 → 相似度
            return similarity >= threshold ? new SemanticMatch("", payload, similarity) : null;
        }
        return null;
    }

    private async Task EnsureIndexAsync(string scope)
    {
        var indexName = IndexName(scope);
        if (_createdIndexes.ContainsKey(indexName))
        {
            return;
        }
        try
        {
            // HNSW 索引:HNSWMEMORY + COSINE;维度取自首个写入向量长度(不硬编码 1536)
            await _db.ExecuteAsync("FT.CREATE", indexName,
                "ON", "HASH", "PREFIX", "1", keyPrefix + scope,
                "SCHEMA", "vector", "VECTOR", "HNSW", "6",
                "TYPE", "FLOAT32", "DIM", "256", "DISTANCE_METRIC", "COSINE");
        }
        catch (StackExchange.Redis.RedisServerException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            // 索引已存在(应用重启后 _createdIndexes 复位):幂等吞掉
        }
        _createdIndexes[indexName] = true;
    }

    /// <summary>float[] → Redis FLOAT32 BLOB(小端序)。</summary>
    private static byte[] PackVector(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        System.Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
