namespace NexMesh.Auth;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

/// <summary>
/// 登录限流计数器抽象:固定窗口原子计数——同 key 在窗口内第 N 次尝试,N ≤ permitLimit 放行。
/// 实现必须保证原子性(内存 AddOrUpdate / Redis Lua),多实例部署用 Redis 实现保持全局一致。
/// </summary>
public interface ILoginRateLimitCounter
{
    Task<bool> TryPassAsync(string key, int permitLimit, int windowSeconds, CancellationToken cancellationToken = default);
}

/// <summary>内存固定窗口计数(单实例/开发;窗口语义与旧版 LoginRateLimiter 一致)。
/// 过期清扫:每 SweepEvery 次调用摊销触发一次全表清扫,移除窗口已过期的 Key——
/// 防御慢速假 IP 扫描对内存的无限占用(实测 100 万唯一 Key ≈ 141MB)。</summary>
public sealed class MemoryLoginRateLimitCounter(TimeProvider? timeProvider = null) : ILoginRateLimitCounter
{
    private const int SweepEvery = 4096;

    private sealed record Window(DateTimeOffset Started, long Count); // long:单窗口 21.4 亿次后 int 溢出会击穿限流(100 亿审计发现)

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.OrdinalIgnoreCase);
    private int _callsSinceSweep;

    public Task<bool> TryPassAsync(string key, int permitLimit, int windowSeconds, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var passed = _windows.AddOrUpdate(key,
            _ => new Window(now, 1),
            (_, existing) =>
                now - existing.Started > TimeSpan.FromSeconds(windowSeconds)
                    ? new Window(now, 1) // 窗口过期,重新计数
                    : existing with { Count = existing.Count + 1 });

        // 摊销清扫:过期 Key(距窗口起点超过 2 个窗口期)移除,防字典无限增长
        if (Interlocked.Increment(ref _callsSinceSweep) >= SweepEvery)
        {
            Interlocked.Exchange(ref _callsSinceSweep, 0);
            Sweep(now, windowSeconds);
        }
        return Task.FromResult(passed.Count <= permitLimit);
    }

    /// <summary>移除已过期超过一个窗口期的 Key(保守双窗口,避免清扫到活跃窗口)。</summary>
    private void Sweep(DateTimeOffset now, int windowSeconds)
    {
        var threshold = TimeSpan.FromSeconds(windowSeconds * 2L);
        foreach (var (key, window) in _windows)
        {
            if (now - window.Started > threshold)
            {
                _windows.TryRemove(key, out _);
            }
        }
    }
}

/// <summary>
/// Redis 固定窗口计数(多实例生产):Lua 原子 INCR + 首次 PEXPIRE——窗口从首次计数起算,无竞态。
/// Redis 故障策略:FailOpen=true(默认,可用性优先,放行并记 Error 日志)/ false(拒绝该次尝试)。
/// </summary>
public sealed class RedisLoginRateLimitCounter(
    IConnectionMultiplexer redis,
    string keyPrefix = "nexmesh:login-rate:",
    bool failOpen = true,
    ILogger<RedisLoginRateLimitCounter>? logger = null) : ILoginRateLimitCounter
{
    private const string WindowScript =
        """
        local c = redis.call('INCR', KEYS[1])
        if c == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end
        return c
        """;

    public async Task<bool> TryPassAsync(string key, int permitLimit, int windowSeconds, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = redis.GetDatabase();
            var count = (long)await db.ScriptEvaluateAsync(
                WindowScript,
                new RedisKey[] { (RedisKey)$"{keyPrefix}{key}" },
                new RedisValue[] { windowSeconds * 1000 });
            return count <= permitLimit;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "[LoginRateLimit] Redis 计数失败(key={Key}),按 FailOpen={FailOpen} 处理", key, failOpen);
            return failOpen; // Open=放行(可用性优先);Closed=拒绝(安全优先)
        }
    }
}
