namespace NexMesh.Cache;

using Microsoft.Extensions.DependencyInjection;
using NexMesh.Core;

/// <summary>
/// 缓存数据源基类:业务缓存源(如 RedisSource)继承此基类即获得键值表面——
/// SetString/GetString/Set&lt;T&gt;/Get&lt;T&gt;/Remove/Exists(同步+异步),
/// 底层经 ICacheSource(双层 L1 内存 + L2 Redis)。KeyPrefix 由缓存源自身应用(配置项),
/// 此处键原样透传,避免双重前缀。构造经 ActivatorUtilities 完成,子类可追加构造参数。
/// </summary>
public abstract class CacheSourceBase(ICacheSource cache, CacheSourceOptions options)
{
    protected ICacheSource Cache { get; } = cache;

    protected string KeyPrefix { get; } = options.KeyPrefix ?? "";

    protected virtual string FullKey(string key) => key;

    // ── 同步表面(旧习惯调用形态;内部阻塞执行,业务按需选择异步表面) ──

    public void SetString(string key, string value, int? expireSeconds = null) =>
        Cache.SetAsync(FullKey(key), value, expireSeconds is { } s ? TimeSpan.FromSeconds(s) : null)
            .GetAwaiter().GetResult();

    public string? GetString(string key) =>
        Cache.GetAsync<string>(FullKey(key)).GetAwaiter().GetResult();

    public void Set<T>(string key, T value, int? expireSeconds = null) =>
        Cache.SetAsync(FullKey(key), value, expireSeconds is { } s ? TimeSpan.FromSeconds(s) : null)
            .GetAwaiter().GetResult();

    public T? Get<T>(string key) =>
        Cache.GetAsync<T>(FullKey(key)).GetAwaiter().GetResult();

    public void Remove(string key) =>
        Cache.RemoveAsync(FullKey(key)).GetAwaiter().GetResult();

    public bool Exists(string key) =>
        GetString(key) is not null;

    // ── 异步表面 ──

    public Task SetStringAsync(string key, string value, int? expireSeconds = null, CancellationToken ct = default) =>
        Cache.SetAsync(FullKey(key), value, expireSeconds is { } s ? TimeSpan.FromSeconds(s) : null, ct);

    public Task<string?> GetStringAsync(string key, CancellationToken ct = default) =>
        Cache.GetAsync<string>(FullKey(key), ct);

    public Task SetAsync<T>(string key, T value, int? expireSeconds = null, CancellationToken ct = default) =>
        Cache.SetAsync(FullKey(key), value, expireSeconds is { } s ? TimeSpan.FromSeconds(s) : null, ct);

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) =>
        Cache.GetAsync<T>(FullKey(key), ct);

    public Task RemoveAsync(string key, CancellationToken ct = default) =>
        Cache.RemoveAsync(FullKey(key), ct);
}

/// <summary>缓存数据源注册:构建独立缓存源(配置了连接串即接 Redis,否则内存兜底)并注册业务源类型。</summary>
public static class CacheSourceExtensions
{
    public static NexMeshBuilder AddCacheSource<TSource>(this NexMeshBuilder builder, CacheSourceOptions options)
        where TSource : class
    {
        StackExchange.Redis.IConnectionMultiplexer? redis = null;
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            redis = StackExchange.Redis.ConnectionMultiplexer.Connect(options.ConnectionString);
        }
        var source = new NexMeshCacheSource(options, redis);

        builder.Services.AddSingleton<TSource>(sp =>
            Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<TSource>(sp, source, options));
        return builder;
    }
}
