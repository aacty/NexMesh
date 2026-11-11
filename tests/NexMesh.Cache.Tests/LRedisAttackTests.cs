using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh;
using NexMesh.Core;
using NexMesh.Data;
using NexMesh.Data.Migrations;
using FreeSql;

namespace NexMesh.Cache.Tests;

using System.Net;

/// <summary>
/// 第三轮交叉测试(L):真 Redis 攻击面。
/// 攻击面1:迁移/缓存分布式锁——释放后等待者接管时序、错误 token 续期幂等拒绝、锁内异常后不留死锁;
/// 攻击面2:幂等防重 40901(真 Redis 并发恰好一通 + 完成后重试语义取证);
/// 攻击面4:缓存击穿 single-flight(32 并发、慢回源)。
/// 无 NEXMESH_TEST_REDIS 时软跳过(仓库既有约定)。
/// </summary>
public class LRedisLockAttackTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("NEXMESH_TEST_REDIS");

    /// <summary>独立源实例:L1 关闭(L1Ttl=0),每次调用都走真实 Redis 路径。</summary>
    private static (NexMeshCacheSource Source, StackExchange.Redis.ConnectionMultiplexer Mux) BuildRedisSource(string prefix)
    {
        var mux = StackExchange.Redis.ConnectionMultiplexer.Connect(ConnectionString!);
        var source = new NexMeshCacheSource(
            new CacheSourceOptions { KeyPrefix = prefix, DefaultTtlSeconds = 60, L1TtlSeconds = 0 }, mux);
        return (source, mux);
    }

    // ── 攻击面1a:缓存锁续期的令牌校验 ──────────────────────────────

    [Fact]
    public async Task L缓存锁_陈旧锁对他人锁续期必须幂等拒绝()
    {
        if (ConnectionString is null)
        {
            return;
        }
        var prefix = $"latk:{Guid.NewGuid():N}:";
        var (source, mux) = BuildRedisSource(prefix);
        using var _ = mux;
        var db = mux.GetDatabase();
        const string key = "res";
        var lockKey = prefix + "lock:" + key;

        // A 以 500ms 短租约持锁后"死亡"(不再续期/释放)
        var lockA = await source.TryLockAsync(key, TimeSpan.FromMilliseconds(500));
        Assert.NotNull(lockA);
        await Task.Delay(800); // A 的租约过期
        var lockB = await source.TryLockAsync(key, TimeSpan.FromMilliseconds(800)); // B 以自己的 token 接管
        Assert.NotNull(lockB);

        // 攻击:A 的陈旧锁对象对"B 正持有的锁"续期 30s——错误 token 续期必须失败
        var renewed = await lockA!.RenewAsync(TimeSpan.FromSeconds(30));
        Assert.False(renewed, "陈旧锁对象对他人锁续期必须返回 false(令牌校验)");

        // B 的租约(800ms)不得被 A 的续期人为拉长:锁键 TTL 仍应在 1s 量级,而非 30s
        var ttl = await db.KeyTimeToLiveAsync(lockKey);
        Assert.True(ttl is null || ttl <= TimeSpan.FromSeconds(2),
            $"B 的锁 TTL 被陈旧续期拉长至 {ttl?.TotalSeconds:F1}s——KeyExpireAsync 未校验持有者令牌");

        await Task.Delay(1200); // B 的租约自然过期
        var lockC = await source.TryLockAsync(key, TimeSpan.FromSeconds(10));
        Assert.NotNull(lockC); // A 续不了他人的命 → 无死锁,第三者可接管
        await lockC!.DisposeAsync();
    }

    [Fact]
    public async Task L缓存锁_自有锁续期成功_已过期锁续期拒绝()
    {
        if (ConnectionString is null)
        {
            return;
        }
        var prefix = $"lself:{Guid.NewGuid():N}:";
        var (source, mux) = BuildRedisSource(prefix);
        using var _ = mux;
        var db = mux.GetDatabase();

        // 自有锁:续期成功且 TTL 真实延长
        var own = await source.TryLockAsync("own", TimeSpan.FromSeconds(3));
        Assert.NotNull(own);
        Assert.True(await own!.RenewAsync(TimeSpan.FromSeconds(6)), "自有锁续期必须成功");
        var ttl = await db.KeyTimeToLiveAsync(prefix + "lock:own");
        var min = TimeSpan.FromSeconds(4);
        var max = TimeSpan.FromSeconds(7);
        Assert.True(ttl is not null && ttl > min && ttl <= max,
            $"续期后 TTL={ttl} 应接近 6s");
        await own.DisposeAsync();

        // 已过期(键不存在):续期返回 false
        var gone = await source.TryLockAsync("gone", TimeSpan.FromMilliseconds(300));
        Assert.NotNull(gone);
        await Task.Delay(600);
        Assert.False(await gone!.RenewAsync(TimeSpan.FromSeconds(5)), "锁已不存在时续期必须返回 false");
    }

    // ── 攻击面1b:迁移锁(RedisMigrationLock)时序 ───────────────────

    [Fact]
    public async Task L迁移锁_持有者主动释放后等待者立即接管()
    {
        if (ConnectionString is null)
        {
            return;
        }
        using var redis = StackExchange.Redis.ConnectionMultiplexer.Connect(ConnectionString);
        var key = $"test:L-miglock-takeover:{Guid.NewGuid():N}";
        var holder = new RedisMigrationLock(redis, key, TimeSpan.FromSeconds(10));
        var waiter = new RedisMigrationLock(redis, key, TimeSpan.FromSeconds(10));

        using var lease = await holder.TryAcquireAsync();
        Assert.NotNull(lease);
        Assert.Null(await waiter.TryAcquireAsync()); // 互斥窗口确认

        lease!.Dispose(); // 主动释放
        var sw = Stopwatch.StartNew();
        IMigrationLease? taken = null;
        while ((taken = await waiter.TryAcquireAsync()) is null)
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "持有者已释放,等待者 5s 内未接管");
            await Task.Delay(10);
        }
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500),
            $"释放→接管耗时 {sw.ElapsedMilliseconds}ms(轮询 10ms 间隔,应秒级)");
        taken!.Dispose();
    }

    [Fact]
    public async Task L迁移锁_错误令牌续期不延租_过期后他人可接管()
    {
        if (ConnectionString is null)
        {
            return;
        }
        using var redis = StackExchange.Redis.ConnectionMultiplexer.Connect(ConnectionString);
        var key = $"test:L-miglock-renew:{Guid.NewGuid():N}";
        var db = redis.GetDatabase();
        // 900ms 租约 → 续期周期 450ms;若续期无令牌校验,这个节奏足以把他人锁永久续命
        var holder = new RedisMigrationLock(redis, key, TimeSpan.FromMilliseconds(900));

        using var lease = await holder.TryAcquireAsync();
        Assert.NotNull(lease);

        // 模拟 holder 失去所有权:键值已被换成他人 token(带 1200ms 独立 TTL)
        await db.StringSetAsync(key, "foreign-token", TimeSpan.FromMilliseconds(1200));

        await Task.Delay(2600); // 跨 5 个续期周期 + foreign TTL 自然到期点

        // 若续期 Lua 缺令牌校验(裸 PEXPIRE),foreign 键会被永久续命,这里必拿不到
        var third = new RedisMigrationLock(redis, key, TimeSpan.FromSeconds(10));
        var lease3 = await third.TryAcquireAsync();
        Assert.NotNull(lease3);

        // holder 释放(其 token 已不匹配)不得误删 third 的锁
        lease!.Dispose();
        var fourth = new RedisMigrationLock(redis, key, TimeSpan.FromSeconds(10));
        Assert.Null(await fourth.TryAcquireAsync());
        lease3!.Dispose();
    }

    [Fact]
    public async Task L迁移锁_锁内工作抛异常_finally释放后等待者秒级接管()
    {
        if (ConnectionString is null)
        {
            return;
        }
        using var redis = StackExchange.Redis.ConnectionMultiplexer.Connect(ConnectionString);
        var key = $"test:L-miglock-throw:{Guid.NewGuid():N}";
        var worker = new RedisMigrationLock(redis, key, TimeSpan.FromSeconds(10));
        var waiter = new RedisMigrationLock(redis, key, TimeSpan.FromSeconds(10));

        var lease = await worker.TryAcquireAsync();
        Assert.NotNull(lease);

        // 等待者在锁被持有时进入轮询(与真实多实例 Runner 的 500ms 轮询同构,这里 10ms 加密时序)
        var waiting = Task.Run(async () =>
        {
            while (true)
            {
                var taken = await waiter.TryAcquireAsync();
                if (taken is not null)
                {
                    return taken;
                }
                await Task.Delay(10);
            }
        });

        // 锁内工作抛异常,finally 释放(与 NexMeshMigrationRunner.RunPendingAsync 的 finally 同构)
        var sw = Stopwatch.StartNew();
        var boom = await Record.ExceptionAsync(async () =>
        {
            try
            {
                await Task.Delay(50);
                throw new InvalidOperationException("L:锁内迁移爆炸");
            }
            finally
            {
                lease!.Dispose();
            }
        });
        Assert.IsType<InvalidOperationException>(boom); // 确认异常路径真实走过(而非静默成功)

        var taken2 = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(taken2 is not null, "工作抛异常后锁未释放——等待者 5s 未接管(死锁)");
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500),
            $"异常释放→接管耗时 {sw.ElapsedMilliseconds}ms");
        taken2!.Dispose();
    }

    // ── 攻击面1c:Runner 集成——迁移失败后锁必须随 finally 释放(另一实例立即可执行) ──

    private static Microsoft.Extensions.DependencyInjection.ServiceProvider BuildMigrationProvider(
        StackExchange.Redis.IConnectionMultiplexer redis, string lockKey,
        Action<IServiceCollection> registerMigration)
    {
        var services = new ServiceCollection();
        services.AddNexMesh();
        services.AddNexMeshDb("main", o =>
        {
            o.DataType = DataType.Sqlite;
            o.ConnectionString = $"Data Source=file:ldb-{Guid.NewGuid():N}?mode=memory&cache=shared";
            o.AutoSyncStructure = false;
        });
        registerMigration(services);
        services.AddNexMeshMigrations(new ConfigurationBuilder().AddInMemoryCollection().Build(),
            typeof(NexMeshMigration).Assembly);
        services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(redis);
        services.AddSingleton<IMigrationLock>(_ => new RedisMigrationLock(redis, lockKey, TimeSpan.FromSeconds(10)));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task L迁移Runner_迁移失败抛异常后锁不泄漏_另一实例立即可执行()
    {
        if (ConnectionString is null)
        {
            return;
        }
        using var redis = StackExchange.Redis.ConnectionMultiplexer.Connect(ConnectionString);
        var lockKey = $"test:L-mig-runner-throw:{Guid.NewGuid():N}";

        // 实例1:迁移抛异常(Runner 的 finally 应释放租约)
        var ex = await Record.ExceptionAsync(async () =>
        {
            using var p1 = BuildMigrationProvider(redis, lockKey, s => s.AddNexMeshMigration<LBoomMigration>());
            await p1.GetRequiredService<IMigrationRunner>().RunPendingAsync();
        });
        Assert.NotNull(ex);
        Assert.DoesNotContain("获取迁移分布式锁超时", ex!.Message); // 失败原因必须是迁移本身,不是锁等待超时

        // 实例2:立刻执行——锁必须已被释放(若泄漏将等满 90s 锁超时)
        var sw = Stopwatch.StartNew();
        using var p2 = BuildMigrationProvider(redis, lockKey, s => s.AddNexMeshMigration<LGoodMigration>());
        var applied = await p2.GetRequiredService<IMigrationRunner>().RunPendingAsync();
        Assert.Equal(1, applied);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20),
            $"实例2 等锁耗时 {sw.ElapsedMilliseconds}ms——迁移失败后锁疑似未释放");
    }
}

/// <summary>L:中途爆炸的迁移( Runner 异常路径专用)。</summary>
public class LBoomMigration(string source = "main") : NexMeshMigration
{
    public override long Version => 9100;
    public override string Name => "L:锁内迁移爆炸";
    public override string Source { get; } = source;
    public override Task UpAsync(IFreeSql db, CancellationToken ct = default)
        => throw new InvalidOperationException("L:锁内迁移爆炸");
}

/// <summary>L:可成功应用的迁移(接管实例专用)。</summary>
public class LGoodMigration(string source = "main") : NexMeshMigration
{
    public override long Version => 9200;
    public override string Name => "L:接管后建表";
    public override string Source { get; } = source;
    public override Task UpAsync(IFreeSql db, CancellationToken ct = default)
    {
        db.Ado.ExecuteNonQuery("CREATE TABLE IF NOT EXISTS l_good (id INTEGER PRIMARY KEY, note TEXT)");
        return Task.CompletedTask;
    }
}

/// <summary>攻击面2:幂等防重 40901(真 Redis)。</summary>
public class LIdempotencyRealRedisTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("NEXMESH_TEST_REDIS");

    private static async Task<WebApplication> BuildIdempotencyAppAsync(Func<string> handlePay)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:Cache:Mode"] = "Redis",
            ["NexMesh:Cache:Sources:main:ConnectionString"] = ConnectionString,
        }).Build();
        builder.Services.AddNexMeshCache(config);
        builder.Services.AddNexMesh();
        var app = builder.Build();
        app.UseNexMeshIdempotency();
        app.MapPost("/api/pay", async () =>
        {
            var tag = handlePay();
            await Task.Delay(300); // 真实并发重叠窗口
            return Results.Ok(tag);
        });
        await app.StartAsync();
        return app;
    }

    private static HttpRequestMessage PayRequest(string idemKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/pay")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", idemKey);
        return request;
    }

    private static (bool success, int code) Parse(string envelope)
    {
        using var doc = JsonDocument.Parse(envelope);
        return (doc.RootElement.GetProperty("success").GetBoolean(),
            doc.RootElement.TryGetProperty("code", out var c) ? c.GetInt32() : 0);
    }

    [Fact]
    public async Task L幂等_真Redis并发同Key_恰好一通其余40901()
    {
        if (ConnectionString is null)
        {
            return;
        }
        var executions = 0;
        var firstInHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = await BuildIdempotencyAppAsync(() =>
        {
            var n = Interlocked.Increment(ref executions);
            firstInHandler.TrySetResult(); // 确定性:首发已持锁并进入处理器
            return releaseHandler.Task.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => $"paid-{n}").GetAwaiter().GetResult();
        });
        try
        {
            var client = app.GetTestClient();
            var idemKey = $"L-idem-{Guid.NewGuid():N}";

            var firstTask = client.SendAsync(PayRequest(idemKey));
            await firstInHandler.Task;

            var conflicts = Enumerable.Range(0, 5)
                .Select(_ => client.SendAsync(PayRequest(idemKey))).ToList();
            Assert.Equal(1, Volatile.Read(ref executions)); // 并发窗口内只有首发进入处理器(挂起中)

            foreach (var conflict in conflicts)
            {
                using var resp = await conflict;
                Assert.Equal(System.Net.HttpStatusCode.Conflict, resp.StatusCode);
                var (success, code) = Parse(await resp.Content.ReadAsStringAsync());
                Assert.False(success);
                Assert.Equal(40901, code); // 业务码 40901,不是裸 409
            }
            releaseHandler.TrySetResult(); // 放行首发 → 完成 → 响应进回放窗
            var first = await firstTask;
            Assert.True(first.IsSuccessStatusCode);
            Assert.Equal(1, Volatile.Read(ref executions)); // 全窗口结束:恰好执行一次

            // 完成后同 Key 重试 → 回放(200 + X-Idempotent-Replay),不再执行
            using var replay = await client.SendAsync(PayRequest(idemKey));
            Assert.True(replay.IsSuccessStatusCode);
            Assert.Equal("true", replay.Headers.GetValues("X-Idempotent-Replay").FirstOrDefault());
            Assert.Equal(1, Volatile.Read(ref executions));
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task L幂等_前次完成后同Key重试_标准回放不再执行()
    {
        // 【修复反转】标准幂等 = 完成态响应回放:24h 窗口内同 Key 重试拿到与首次一致的响应
        // (带 X-Idempotent-Replay 头),不再重新执行——"响应丢失后的网络重试"不再重复扣款。
        if (ConnectionString is null)
        {
            return;
        }
        var executions = 0;
        var app = await BuildIdempotencyAppAsync(() => $"paid-{Interlocked.Increment(ref executions)}");
        try
        {
            var client = app.GetTestClient();
            var key = $"L-idem-seq-{Guid.NewGuid():N}";

            using var r1 = await client.SendAsync(PayRequest(key));
            Assert.True(r1.IsSuccessStatusCode);
            Assert.Contains("paid-1", await r1.Content.ReadAsStringAsync());

            await Task.Delay(200); // 首发已完成、锁已释放
            using var r2 = await client.SendAsync(PayRequest(key)); // "响应丢失后的网络重试"场景

            Assert.True(r2.IsSuccessStatusCode, $"第二次请求状态={r2.StatusCode}");
            Assert.Equal("true", r2.Headers.GetValues("X-Idempotent-Replay").FirstOrDefault());
            Assert.Contains("paid-1", await r2.Content.ReadAsStringAsync()); // 回放首次响应
            Assert.Equal(1, Volatile.Read(ref executions)); // 不再执行
        }
        finally
        {
            await app.DisposeAsync();
        }
    }
}

/// <summary>攻击面4:缓存击穿 single-flight(真 Redis)。</summary>
public class LStampedeRealRedisTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("NEXMESH_TEST_REDIS");

    [Fact]
    public async Task L击穿防护_真Redis_32并发读同Key恰好回源一次()
    {
        if (ConnectionString is null)
        {
            return;
        }
        var mux = StackExchange.Redis.ConnectionMultiplexer.Connect(ConnectionString);
        using var _ = mux;
        var source = new NexMeshCacheSource(
            new CacheSourceOptions { KeyPrefix = $"lst:{Guid.NewGuid():N}:", DefaultTtlSeconds = 60, L1TtlSeconds = 0 }, mux);
        var calls = 0;

        var tasks = Enumerable.Range(0, 32).Select(_ => source.GetOrCreateAsync<int>(
            "hot-key", TimeSpan.FromSeconds(60),
            async __ =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(80); // 模拟昂贵回源(< 300ms 等待窗)
                return 42;
            }));
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(42, r));
        Assert.Equal(1, calls); // single-flight:恰好一次
    }

    [Fact]
    public async Task L击穿防护_慢回源超等待窗_不得放大为并发回源()
    {
        if (ConnectionString is null)
        {
            return;
        }
        var mux = StackExchange.Redis.ConnectionMultiplexer.Connect(ConnectionString);
        using var _ = mux;
        var source = new NexMeshCacheSource(
            new CacheSourceOptions { KeyPrefix = $"lsl:{Guid.NewGuid():N}:", DefaultTtlSeconds = 60, L1TtlSeconds = 0 }, mux);
        var calls = 0;

        // 600ms 回源 > 300ms 等待窗:等待者超时后必须继续等(复查+续等),而不是齐刷刷放大回源
        var tasks = Enumerable.Range(0, 32).Select(_ => source.GetOrCreateAsync<int>(
            "slow-key", TimeSpan.FromSeconds(60),
            async __ =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(600);
                return 7;
            }));
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(7, r));
        Assert.Equal(1, calls); // 慢回源同样必须单飞(此为待修缺口:当前实现会放大)
    }
}

/// <summary>
/// SSS+交叉审核(L-F4 修复):标准幂等 = 完成态响应回放(内存模式,无 Redis 依赖)。
/// 同 Key 重试在 24h 回放窗内拿到与首次一致的响应(不再重复执行);
/// 5xx 不回放(服务端不确定态,重试拿新鲜尝试);4xx 回放(确定性客户端错误);
/// 不同 Key 独立执行。
/// </summary>
public class LIdempotencyReplayTests
{
    private static async Task<WebApplication> BuildAppAsync(Func<string> handlePay, Func<string>? handleFail = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var config = new ConfigurationBuilder().Build();
        builder.Services.AddNexMeshCache(config);
        builder.Services.AddNexMesh();
        var app = builder.Build();
        app.UseNexMeshIdempotency();
        app.MapPost("/api/pay", () => Results.Ok(handlePay()));
        app.MapPost("/api/fail", () => Results.BadRequest(handleFail?.Invoke() ?? "bad"));
        app.MapPost("/api/boom", () => Results.StatusCode(500)); // 确定性 5xx(5xx 不进回放窗)
        await app.StartAsync();
        return app;
    }

    private static HttpRequestMessage PayRequest(string key) =>
        new(HttpMethod.Post, "/api/pay")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            Headers = { { "Idempotency-Key", key } },
        };

    [Fact]
    public async Task 同Key完成后重试_回放首次响应_不再执行()
    {
        var executions = 0;
        var app = await BuildAppAsync(() => $"paid-{Interlocked.Increment(ref executions)}");
        try
        {
            var client = app.GetTestClient();
            var key = $"replay-{Guid.NewGuid():N}";

            using var r1 = await client.SendAsync(PayRequest(key));
            Assert.True(r1.IsSuccessStatusCode);
            Assert.Contains("paid-1", await r1.Content.ReadAsStringAsync());

            using var r2 = await client.SendAsync(PayRequest(key));
            Assert.True(r2.IsSuccessStatusCode);
            Assert.Equal("true", r2.Headers.GetValues("X-Idempotent-Replay").FirstOrDefault()); // 回放标记
            Assert.Contains("paid-1", await r2.Content.ReadAsStringAsync()); // 与首次响应一致
            Assert.Equal(1, Volatile.Read(ref executions)); // 不再执行
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task 不同Key_独立执行()
    {
        var executions = 0;
        var app = await BuildAppAsync(() => $"paid-{Interlocked.Increment(ref executions)}");
        try
        {
            var client = app.GetTestClient();
            using var r1 = await client.SendAsync(PayRequest($"k1-{Guid.NewGuid():N}"));
            using var r2 = await client.SendAsync(PayRequest($"k2-{Guid.NewGuid():N}"));
            Assert.Equal(2, Volatile.Read(ref executions)); // 不同 Key 各自执行
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task 客户端错误4xx_同样回放()
    {
        var executions = 0;
        var app = await BuildAppAsync(() => "", handleFail: () => $"bad-{Interlocked.Increment(ref executions)}");
        try
        {
            var client = app.GetTestClient();
            var key = $"replay-4xx-{Guid.NewGuid():N}";

            HttpRequestMessage FailRequest() => new(HttpMethod.Post, "/api/fail")
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
                Headers = { { "Idempotency-Key", key } },
            };
            using var r1 = await client.SendAsync(FailRequest());
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, r1.StatusCode);

            using var r2 = await client.SendAsync(FailRequest());
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, r2.StatusCode);
            Assert.Equal("true", r2.Headers.GetValues("X-Idempotent-Replay").FirstOrDefault()); // 回放
            Assert.Equal(1, Volatile.Read(ref executions)); // 不重复执行
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task 服务端5xx_不回放_重试拿新鲜尝试()
    {
        var app = await BuildAppAsync(() => "", handleFail: null);
        try
        {
            var client = app.GetTestClient();
            var key = $"replay-5xx-{Guid.NewGuid():N}";
            HttpRequestMessage BoomRequest() => new(HttpMethod.Post, "/api/boom")
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
                Headers = { { "Idempotency-Key", key } },
            };

            // /api/boom = Results.StatusCode(500):确定性 5xx 响应(5xx 不进回放窗)
            using var r1 = await client.SendAsync(BoomRequest());
            Assert.Equal(HttpStatusCode.InternalServerError, r1.StatusCode);

            using var r2 = await client.SendAsync(BoomRequest());
            Assert.Equal(HttpStatusCode.InternalServerError, r2.StatusCode); // 重试真实到达端点(非缓存回放)
            Assert.False(r2.Headers.Contains("X-Idempotent-Replay"), "5xx 不应回放(重试应拿新鲜尝试)");
        }
        finally
        {
            await app.DisposeAsync();
        }
    }
}
