using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.Cache;
using NexMesh.Core;

namespace NexMesh.Cache.Tests;

/// <summary>内存兜底模式全项(Redis 集成测试由 RealRedisTests 按环境变量门控)。</summary>
public class CacheTests
{
    private static ICacheSource Build(string prefix = "t:")
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:Cache:Sources:main:KeyPrefix"] = prefix,
            ["NexMesh:Cache:Sources:main:DefaultTtlSeconds"] = "60",
        }).Build();
        services.AddNexMeshCache(config);
        return services.BuildServiceProvider().GetRequiredService<ICacheFactory>().Get();
    }

    [Fact]
    public async Task 读写删_往返正确()
    {
        var cache = Build();
        await cache.SetAsync("k1", new Order { Id = 7, Name = "x" });

        var hit = await cache.GetAsync<Order>("k1");
        Assert.NotNull(hit);
        Assert.Equal(7, hit!.Id);

        await cache.RemoveAsync("k1");
        Assert.Null(await cache.GetAsync<Order>("k1"));
    }

    [Fact]
    public async Task 防击穿_并发二十请求只回源一次()
    {
        var cache = Build();
        var calls = 0;

        var tasks = Enumerable.Range(0, 20).Select(_ => cache.GetOrCreateAsync<int>(
            "hot-key", TimeSpan.FromSeconds(60),
            async _ =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(50); // 模拟昂贵回源
                return 42;
            }));
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(42, r));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task 工厂异常不缓存()
    {
        var cache = Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetOrCreateAsync<string>("err-key", TimeSpan.FromMinutes(1),
                _ => throw new InvalidOperationException("boom")));

        Assert.Null(await cache.GetAsync<string>("err-key"));
    }

    [Fact]
    public async Task 分布式锁_互斥且可释放()
    {
        var cache = Build();

        var first = await cache.TryLockAsync("res", TimeSpan.FromSeconds(30));
        Assert.NotNull(first);
        Assert.Null(await cache.TryLockAsync("res", TimeSpan.FromSeconds(30))); // 互斥

        await first!.DisposeAsync();
        var second = await cache.TryLockAsync("res", TimeSpan.FromSeconds(30));
        Assert.NotNull(second); // 释放后可重获
        await second!.DisposeAsync();
    }

    [Fact]
    public async Task 命名源_未注册名报错()
    {
        var services = new ServiceCollection();
        services.AddNexMeshCache(new ConfigurationBuilder().Build());
        var factory = services.BuildServiceProvider().GetRequiredService<ICacheFactory>();

        Assert.NotNull(factory.Get("main"));
        Assert.Throws<InvalidOperationException>(() => factory.Get("nope"));
    }

    private sealed record Order
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
    }
}

/// <summary>幂等防重中间件:同 Key 并发 → 一通一 409。</summary>
public class IdempotencyTests
{
    [Fact]
    public async Task 幂等头_并发重复请求409()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var config = new ConfigurationBuilder().Build();
        builder.Services.AddNexMeshCache(config);
        builder.Services.AddNexMesh();
        var app = builder.Build();
        app.UseNexMeshIdempotency();
        var firstInHandler = new TaskCompletionSource();
        app.MapPost("/api/pay", async () =>
        {
            firstInHandler.TrySetResult(); // 确定性:第一个请求已在处理器内
            await Task.Delay(300); // 制造真实的并发重叠窗口
            return Results.Ok("paid");
        });
        await app.StartAsync();
        var client = app.GetTestClient();

        using var request1 = new HttpRequestMessage(HttpMethod.Post, "/api/pay")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        request1.Headers.Add("Idempotency-Key", "pay-2026-001");
        using var request2 = new HttpRequestMessage(HttpMethod.Post, "/api/pay")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        request2.Headers.Add("Idempotency-Key", "pay-2026-001");

        var first = client.SendAsync(request1);
        await firstInHandler.Task; // 等第一个真正进入处理器(而非拍脑袋延迟)
        var second = client.SendAsync(request2);

        var ok = await first;
        Assert.True(ok.IsSuccessStatusCode);
        var conflict = await second;
        Assert.Equal(System.Net.HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("40901", await conflict.Content.ReadAsStringAsync());
        await app.DisposeAsync();
    }
}

/// <summary>真实 Redis 集成:未设 NEXMESH_TEST_REDIS 整组跳过(仓库既有约定)。</summary>
public class RealRedisTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("NEXMESH_TEST_REDIS");

    [Fact]
    public async Task Redis模式_双层读写与跨实例锁()
    {
        if (ConnectionString is null)
        {
            return; // 无真实 Redis 环境时跳过
        }

        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:Cache:Mode"] = "Redis",
            ["NexMesh:Cache:Sources:main:ConnectionString"] = ConnectionString,
        }).Build();
        services.AddNexMeshCache(config);
        var factory = services.BuildServiceProvider().GetRequiredService<ICacheFactory>();
        var a = factory.Get();
        var b = factory.Get();

        await a.SetAsync("shared", "cross-instance", TimeSpan.FromSeconds(30));
        Assert.Equal("cross-instance", await b.GetAsync<string>("shared")); // L2 跨实例一致

        await using var lockA = await a.TryLockAsync("cross", TimeSpan.FromSeconds(10));
        Assert.NotNull(lockA);
        Assert.Null(await b.TryLockAsync("cross", TimeSpan.FromSeconds(10))); // 跨实例互斥
        await lockA!.DisposeAsync();
    }
}
