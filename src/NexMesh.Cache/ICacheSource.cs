namespace NexMesh.Cache;

/// <summary>缓存源契约:读写删 + 单飞防击穿 + 分布式锁。</summary>
public interface ICacheSource
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// 防击穿读:命中直接返回;未命中时同一 Key 全局只允许一个回源执行(其余短时等待后共享结果),
    /// 回源结果写缓存。工厂异常不缓存。
    /// </summary>
    Task<T> GetOrCreateAsync<T>(
        string key,
        TimeSpan? ttl,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct = default);

    /// <summary>分布式锁:获取失败返回 null(Redis=SET NX PX + Lua 令牌释放;内存=进程内信号量)。</summary>
    Task<ICacheLock?> TryLockAsync(string key, TimeSpan ttl, CancellationToken ct = default);
}

/// <summary>缓存锁:Dispose/DisposeAsync 释放(令牌匹配才真正释放,防误解锁);RenewAsync 延长 TTL。</summary>
public interface ICacheLock : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// 延长锁 TTL(长事务防锁过期):Redis=KeyExpireAsync 真实锁键;内存信号量=no-op(无 TTL 概念)。
    /// 返回 false 表示锁已不存在(过期被他人抢走)。
    /// </summary>
    Task<bool> RenewAsync(TimeSpan ttl, CancellationToken ct = default);
}

/// <summary>命名缓存源工厂:默认 "main"(对应 NexMesh:Cache:Sources 配置)。</summary>
public interface ICacheFactory
{
    ICacheSource Get(string name = "main");

    /// <summary>已注册的缓存源名称(Dashboard 展示)。</summary>
    IReadOnlyList<string> SourceNames { get; }
}

/// <summary>缓存键巡视(Dashboard/运维):按键前缀列键与 TTL(上限防拖垮)。</summary>
public interface ICacheInspection
{
    /// <summary>按前缀列键(上限内返回键+剩余 TTL 秒,null=永不过期)。</summary>
    Task<List<CacheKeyInfo>> ListKeysAsync(string keyPrefix, int limit = 200, CancellationToken ct = default);

    /// <summary>删除单键(运维谨慎操作;键以缓存源 KeyPrefix 为界,防误删外部键)。</summary>
    Task<bool> RemoveKeyAsync(string key, CancellationToken ct = default);
}

/// <summary>缓存键信息。</summary>
public sealed record CacheKeyInfo(string Key, long? TtlSeconds);

/// <summary>NexMesh:Cache 配置节。</summary>
public sealed class NexMeshCacheOptions
{
    /// <summary>Auto(默认):配了连接串用 Redis,否则内存兜底;Redis/Memory 强制指定。</summary>
    public string Mode { get; set; } = "Auto";
    public Dictionary<string, CacheSourceOptions> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["main"] = new(),
    };
}

public sealed class CacheSourceOptions
{
    /// <summary>StackExchange.Redis 连接串(如 localhost:6379,缺省 → Auto 模式下内存兜底)。</summary>
    public string ConnectionString { get; set; } = "";
    /// <summary>Key 前缀(多应用共库隔离)。</summary>
    public string KeyPrefix { get; set; } = "nexmesh:";
    /// <summary>默认 TTL(秒,未指定时使用)。</summary>
    public int DefaultTtlSeconds { get; set; } = 120;
    /// <summary>L1 本地内存层 TTL(秒;0 = 关闭 L1,纯 L2)。</summary>
    public int L1TtlSeconds { get; set; } = 10;
}
