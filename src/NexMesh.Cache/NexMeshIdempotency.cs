namespace NexMesh.Cache;

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 幂等防重中间件:携带 Idempotency-Key 头的写请求,同 Key 并发重复立即 409(40901)——
/// 网络重试/用户双击不可能造成重复扣款。锁带自动续租(默认 20s 周期),长事务不误放行。
/// </summary>
public class IdempotencyMiddleware
{
    private readonly RequestDelegate _next;
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(30);

    public IdempotencyMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ICacheFactory cacheFactory)
    {
        if (!context.Request.Headers.TryGetValue("Idempotency-Key", out var keyValues)
            || string.IsNullOrWhiteSpace(keyValues))
        {
            await _next(context);
            return;
        }

        var cache = cacheFactory.Get();
        var key = $"idem:{context.Request.Method}:{context.Request.Path}:{keyValues}";
        await using var gate = await cache.TryLockAsync(key, LockTtl, context.RequestAborted);
        if (gate is null)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(
                ApiResult<object>.Fail("重复请求处理中(Idempotency-Key 冲突)", 409), JsonSerializerOptions.Web));
            return;
        }

        // SSS+安全修复(第二轮):续租走 ICacheLock.RenewAsync——Redis=KeyExpireAsync 真实锁键,
        // 内存信号量=no-op;彻底取代上一轮"写另一个 key"的假续租
        using var renewal = new CancellationTokenSource();
        var renewTask = Task.Run(async () =>
        {
            try
            {
                while (!renewal.Token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), renewal.Token);
                    await gate.RenewAsync(LockTtl, renewal.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // 请求结束,续租停止——正常路径
            }
        }, CancellationToken.None);

        try
        {
            await _next(context);
        }
        finally
        {
            await renewal.CancelAsync();
            try { await renewTask; } catch (OperationCanceledException) { }
        }
    }
}

public static class NexMeshCacheServiceCollectionExtensions
{
    /// <summary>
    /// 注册缓存:NexMesh:Cache 配置节(Mode=Auto/Redis/Memory;Sources[name].ConnectionString/KeyPrefix/TTL/L1Ttl)。
    /// Auto 模式未配连接串 → 全内存兜底(不装 Redis 也能跑)。StackExchange.Redis 连接多路复用单例。
    /// </summary>
    public static IServiceCollection AddNexMeshCache(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = new NexMeshCacheOptions();
        configuration.GetSection("NexMesh:Cache").Bind(options);
        var forceMemory = options.Mode.Equals("Memory", StringComparison.OrdinalIgnoreCase);

        var sources = new Dictionary<string, ICacheSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, sourceOptions) in options.Sources)
        {
            var useRedis = !forceMemory
                && (options.Mode.Equals("Redis", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(sourceOptions.ConnectionString));

            StackExchange.Redis.IConnectionMultiplexer? redis = null;
            if (useRedis && !string.IsNullOrWhiteSpace(sourceOptions.ConnectionString))
            {
                redis = StackExchange.Redis.ConnectionMultiplexer.Connect(sourceOptions.ConnectionString);
            }
            sources[name] = new NexMeshCacheSource(sourceOptions, redis);
        }

        services.AddSingleton<ICacheFactory>(new CacheFactory(sources));
        return services;
    }

    /// <summary>启用幂等防重(在路由之前注册)。</summary>
    public static IApplicationBuilder UseNexMeshIdempotency(this IApplicationBuilder app)
        => app.UseMiddleware<IdempotencyMiddleware>();

    private sealed class CacheFactory(Dictionary<string, ICacheSource> sources) : ICacheFactory, IDisposable
    {
        public ICacheSource Get(string name = "main") =>
            sources.TryGetValue(name, out var source)
                ? source
                : throw new InvalidOperationException($"缓存源 '{name}' 未注册(NexMesh:Cache:Sources)");

        public IReadOnlyList<string> SourceNames => sources.Keys.ToList();

        /// <summary>SSS+第二轮修复:容器停机时释放全部缓存源(MemoryCache + Redis Multiplexer)。</summary>
        public void Dispose()
        {
            foreach (var source in sources.Values)
            {
                (source as IDisposable)?.Dispose();
            }
        }
    }
}
