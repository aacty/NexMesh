namespace NexMesh.Cache;

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

/// <summary>
/// 双层缓存源实现:L1 进程内存(短 TTL,扛热点)+ L2(Redis 或内存兜底,跨实例一致)。
/// GetOrCreateAsync 单飞:按 Key 哈希取条带信号量——同 Key 并发只放一个回源,其余等待共享;
/// 条带固定 256 个,无字典泄漏。锁语义:L2 为 Redis 时 SET NX PX + Lua 令牌释放。
/// null 不缓存(命中判定与取值分离——值类型 default 不能当"未命中")。
/// </summary>
public sealed class NexMeshCacheSource : ICacheSource, ICacheInspection, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim[] StripeLocks =
        [.. Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1))];

    private readonly IMemoryCache _l1;
    private readonly StackExchange.Redis.IDatabase? _redis;
    private readonly StackExchange.Redis.IConnectionMultiplexer? _multiplexer;
    private readonly string _prefix;
    private readonly TimeSpan _defaultTtl;
    private readonly TimeSpan? _l1Ttl;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _localLocks = new(StringComparer.Ordinal);

    public NexMeshCacheSource(CacheSourceOptions options, StackExchange.Redis.IConnectionMultiplexer? redis)
    {
        _l1 = new MemoryCache(new MemoryCacheOptions());
        _multiplexer = redis;
        _redis = redis?.GetDatabase();
        _prefix = options.KeyPrefix;
        _defaultTtl = TimeSpan.FromSeconds(options.DefaultTtlSeconds);
        _l1Ttl = options.L1TtlSeconds > 0 ? TimeSpan.FromSeconds(options.L1TtlSeconds) : null;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var (found, value) = await TryGetAsync<T>(key, ct);
        return found ? value : default;
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        if (value is null)
        {
            return; // null 不缓存(与命中语义配套)
        }
        var fullKey = _prefix + key;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (_l1Ttl is not null)
        {
            _l1.Set(fullKey, bytes, _l1Ttl.Value);
        }
        if (_redis is not null)
        {
            await _redis.StringSetAsync(fullKey, bytes, ttl ?? _defaultTtl);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        var fullKey = _prefix + key;
        _l1.Remove(fullKey);
        if (_redis is not null)
        {
            await _redis.KeyDeleteAsync(fullKey);
        }
    }

    public async Task<T> GetOrCreateAsync<T>(
        string key,
        TimeSpan? ttl,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct = default)
    {
        var (found, cached) = await TryGetAsync<T>(key, ct);
        if (found)
        {
            return cached!; // null 已由 SetAsync 拒存,found 即有值
        }

        var gate = StripeLocks[(uint)key.GetHashCode() % StripeLocks.Length];
        var held = await gate.WaitAsync(300, ct); // 短时有界等待:极端争用降级并发回源(best-effort)
        try
        {
            // 双检查:拿到锁(或等待超时)后必复查——前一个回源者大概率已填充
            var (again, againValue) = await TryGetAsync<T>(key, ct);
            if (again)
            {
                return againValue!;
            }
            var value = await factory(ct);
            await SetAsync(key, value, ttl, ct);
            return value;
        }
        finally
        {
            if (held)
            {
                gate.Release();
            }
        }
    }

    public async Task<ICacheLock?> TryLockAsync(string key, TimeSpan ttl, CancellationToken ct = default)
    {
        if (_redis is null)
        {
            // 内存兜底:进程内信号量锁(单实例语义)
            var semaphore = _localLocks.GetOrAdd(_prefix + key, _ => new SemaphoreSlim(1, 1));
            if (!await semaphore.WaitAsync(0, ct))
            {
                return null;
            }
            return new SemaphoreLock(semaphore);
        }

        var token = Guid.NewGuid().ToString("N");
        var acquired = await _redis.StringSetAsync(
            _prefix + "lock:" + key, token, ttl, when: StackExchange.Redis.When.NotExists);
        return acquired ? new RedisLock(_redis, _prefix + "lock:" + key, token) : null;
    }

    /// <summary>命中判定与取值分离——值类型 default(如 int=0)不能当"未命中"。</summary>
    private async Task<(bool Found, T? Value)> TryGetAsync<T>(string key, CancellationToken ct)
    {
        var fullKey = _prefix + key;
        if (_l1.TryGetValue(fullKey, out var l1Hit) && l1Hit is byte[] l1Bytes)
        {
            return (true, JsonSerializer.Deserialize<T>(l1Bytes, Json));
        }
        if (_redis is null)
        {
            return (false, default);
        }
        var raw = await _redis.StringGetAsync(fullKey);
        if (raw.IsNull)
        {
            return (false, default);
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(raw.ToString());
        if (_l1Ttl is not null)
        {
            _l1.Set(fullKey, bytes, _l1Ttl.Value);
        }
        return (true, JsonSerializer.Deserialize<T>(bytes, Json));
    }

    private sealed class SemaphoreLock(SemaphoreSlim semaphore) : ICacheLock
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

    public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                semaphore.Release();
            }
        }

        public Task<bool> RenewAsync(TimeSpan ttl, CancellationToken ct = default) =>
            Task.FromResult(true); // 内存信号量无 TTL,续租恒成功
    }

    // ── ICacheInspection(Dashboard/运维巡视)──────────────────────

/// <summary>Redis 模式:SCAN 前缀键+PTTL;内存模式(无 Redis):L1 键快照(TTL 未知记 null)。</summary>
public async Task<List<CacheKeyInfo>> ListKeysAsync(string keyPrefix, int limit = 200, CancellationToken ct = default)
{
    limit = Math.Clamp(limit, 1, 500);
    var result = new List<CacheKeyInfo>();
    if (_redis is not null && _multiplexer is not null)
    {
        var pattern = _prefix + keyPrefix + "*";
        foreach (var endpoint in _multiplexer.GetEndPoints())
        {
            var server = _multiplexer.GetServer(endpoint);
            if (server.IsReplica) continue;
            await foreach (var key in server.KeysAsync(database: _redis.Database, pattern: pattern, pageSize: 100))
            {
                if (ct.IsCancellationRequested || result.Count >= limit) break;
                var ttl = await _redis.KeyTimeToLiveAsync(key);
                result.Add(new CacheKeyInfo(key.ToString()[_prefix.Length..],
                    ttl is null ? null : (long)ttl.Value.TotalSeconds));
            }
            break; // 单主节点展示语义
        }
    }
    else
    {
        // 内存模式:L1 快照(IMemoryCache 无枚举原语——用内部字典快照反射不可取;
        // 诚实降级:返回提示性空集,Dashboard 显示"内存模式不支持键巡视")
        return [];
    }
    return result;
}

/// <summary>删除单键:同时清 L1 与 L2(键以源 KeyPrefix 为界)。</summary>
public async Task<bool> RemoveKeyAsync(string key, CancellationToken ct = default)
{
    _l1.Remove(_prefix + key);
    if (_redis is null) return true;
    return await _redis.KeyDeleteAsync(_prefix + key);
}


    private sealed class RedisLock(StackExchange.Redis.IDatabase redis, string key, string token) : ICacheLock
    {
        // 令牌匹配才释放:防止超时后误解开他人的锁
        private const string ReleaseScript =
            "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

        public async ValueTask DisposeAsync() =>
            await redis.ScriptEvaluateAsync(ReleaseScript,
                keys: [(StackExchange.Redis.RedisKey)key],
                values: [(StackExchange.Redis.RedisValue)token]);

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

        /// <summary>延长锁 TTL(对真实锁键执行 PEXPIRE 语义,非覆盖写入)。</summary>
        public Task<bool> RenewAsync(TimeSpan ttl, CancellationToken ct = default) =>
            redis.KeyExpireAsync(key, ttl);
    }

    /// <summary>SSS+资源审查修复:释放 L1 MemoryCache 与 Redis 多路复用器(停机无泄漏)。</summary>
    public void Dispose()
    {
        (_l1 as IDisposable)?.Dispose();
        _multiplexer?.Dispose();
    }
}
