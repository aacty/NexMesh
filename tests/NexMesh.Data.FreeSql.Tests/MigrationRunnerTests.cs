namespace NexMesh.Data.Migrations.Tests;

using FreeSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NexMesh;
using NexMesh.Core;
using NexMesh.Data;
using Xunit;

/// <summary>测试基座:每用例独立 Provider + SQLite 共享内存库;迁移用显式注册隔离(不扫描测试程序集)。</summary>
public sealed class MigrationHost : IDisposable
{
    public IServiceProvider Services { get; }
    public string DbName { get; }
    private readonly string _hisDbName = $"his-{Guid.NewGuid():N}";

    public MigrationHost(Action<IServiceCollection>? registerMigrations = null, string? sharedDbName = null, bool withHisSource = false)
    {
        DbName = sharedDbName ?? $"db-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddNexMesh();
        services.AddNexMeshDb("main", o =>
        {
            o.DataType = DataType.Sqlite;
            o.ConnectionString = $"Data Source=file:{DbName}?mode=memory&cache=shared";
            o.AutoSyncStructure = false; // 迁移负责建表——证明 Runner 不依赖 AutoSync
        });
        if (withHisSource)
        {
            services.AddNexMeshDb("his", o =>
            {
                o.DataType = DataType.Sqlite;
                o.ConnectionString = $"Data Source=file:{_hisDbName}?mode=memory&cache=shared";
                o.AutoSyncStructure = false;
            });
        }
        registerMigrations?.Invoke(services);
        // 传 Migrations 包自身程序集 = 不扫描任何业务迁移(测试逐个显式注册)
        services.AddNexMeshMigrations(new ConfigurationBuilder().AddInMemoryCollection().Build(),
            typeof(NexMeshMigration).Assembly);
        Services = services.BuildServiceProvider();
    }

    public IMigrationRunner Runner => Services.GetRequiredService<IMigrationRunner>();

    /// <summary>主库(经 IDbFactory 解析,与业务同通道)。</summary>
    public IFreeSql MainDb => Services.GetRequiredService<IDbFactory>().Get("main");

    public IFreeSql HisDb => Services.GetRequiredService<IDbFactory>().Get("his");

    public long Scalar(string sql) => Convert.ToInt64(MainDb.Ado.ExecuteScalar(sql));

    public static bool TableExists(IFreeSql db, string table) =>
        Convert.ToInt64(db.Ado.ExecuteScalar($"SELECT COUNT(*) FROM sqlite_master WHERE name='{table}'")) > 0;

    public void Dispose() => (Services as IDisposable)?.Dispose();
}

// ═══════════════ 测试迁移集(显式注册,互不串扰) ═══════════════

public class V1000_CreateOrders(string source = "main") : NexMeshMigration
{
    public override long Version => 1000;
    public override string Name => "创建订单表";
    public override string Source { get; } = source;
    public override Task UpAsync(IFreeSql db, CancellationToken ct = default)
    {
        db.Ado.ExecuteNonQuery("CREATE TABLE IF NOT EXISTS mig_orders (id INTEGER PRIMARY KEY, note TEXT NOT NULL)");
        db.Ado.ExecuteNonQuery("INSERT INTO mig_orders (note) VALUES ('seed-v1000')");
        return Task.CompletedTask;
    }
}

public class V2000_AddRemark : NexMeshMigration
{
    public override long Version => 2000;
    public override string Name => "订单表加备注列";
    public override Task UpAsync(IFreeSql db, CancellationToken ct = default)
    {
        db.Ado.ExecuteNonQuery("ALTER TABLE mig_orders ADD COLUMN remark TEXT");
        db.Ado.ExecuteNonQuery("UPDATE mig_orders SET remark='v2000' WHERE note='seed-v1000'");
        return Task.CompletedTask;
    }

    public override Task DownAsync(IFreeSql db, CancellationToken ct = default)
        => Task.FromResult(db.Ado.ExecuteNonQuery("ALTER TABLE mig_orders DROP COLUMN remark"));
}

public class V3000_SeedMore : NexMeshMigration
{
    public static readonly List<long> Order = [];
    public override long Version => 3000;
    public override string Name => "补充数据";
    public override Task UpAsync(IFreeSql db, CancellationToken ct = default)
    {
        Order.Add(Version);
        db.Ado.ExecuteNonQuery("INSERT INTO mig_orders (note, remark) VALUES ('seed-v3000', 'x')");
        return Task.CompletedTask;
    }
}

/// <summary>中途失败:建表+写数后,故意制造约束违反——整体必须回滚(表和行都不得留下)。</summary>
public class V2500_FailMidWay : NexMeshMigration
{
    public override long Version => 2500;
    public override string Name => "中途失败演示";
    public override Task UpAsync(IFreeSql db, CancellationToken ct = default)
    {
        db.Ado.ExecuteNonQuery("CREATE TABLE mig_partial (id INTEGER PRIMARY KEY, val TEXT NOT NULL)");
        db.Ado.ExecuteNonQuery("INSERT INTO mig_partial (val) VALUES ('will-be-rolled-back')");
        db.Ado.ExecuteNonQuery("INSERT INTO mig_partial (id, val) VALUES (NULL, NULL)"); // 违反 NOT NULL → 抛
        return Task.CompletedTask;
    }
}

public class Dup_A : NexMeshMigration
{
    public override long Version => 7000;
    public override string Name => "重复版本A";
    public override Task UpAsync(IFreeSql db, CancellationToken ct = default) => Task.CompletedTask;
}

public class Dup_B : NexMeshMigration
{
    public override long Version => 7000;
    public override string Name => "重复版本B";
    public override Task UpAsync(IFreeSql db, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>与 V1000_CreateOrders 同版本同名的"被篡改版"(类型不同 → Checksum 不同)。</summary>
public class V1000_Tampered : NexMeshMigration
{
    public override long Version => 1000;
    public override string Name => "创建订单表";
    public override Task UpAsync(IFreeSql db, CancellationToken ct = default)
        => throw new InvalidOperationException("篡改版不应被执行");
}

public class His_V100 : NexMeshMigration
{
    public override long Version => 100;
    public override string Name => "历史库建表";
    public override string Source => "his";
    public override Task UpAsync(IFreeSql db, CancellationToken ct = default)
        => Task.FromResult(db.Ado.ExecuteNonQuery("CREATE TABLE IF NOT EXISTS mig_his (id INTEGER PRIMARY KEY)"));
}

/// <summary>宿主自动执行(Enabled=true)与并发守卫。</summary>
public class MigrationHostedServiceTests
{
    private static IHost BuildHost(Action<IServiceCollection> registrations, bool enabled, string dbName)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddNexMesh();
                services.AddNexMeshDb("main", o =>
                {
                    o.DataType = DataType.Sqlite;
                    o.ConnectionString = $"Data Source=file:{dbName}?mode=memory&cache=shared";
                    o.AutoSyncStructure = false;
                });
                registrations(services);
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["NexMesh:Migrations:Enabled"] = enabled ? "true" : "false",
                }).Build();
                services.AddNexMeshMigrations(config, typeof(NexMeshMigration).Assembly);
            })
            .Build();
    }

    [Fact]
    public async Task Enabled为true_宿主启动自动应用迁移()
    {
        var db = $"auto-{Guid.NewGuid():N}";
        using var host = BuildHost(s => s.AddNexMeshMigration<V1000_CreateOrders>(), enabled: true, dbName: db);
        await host.StartAsync();

        var runner = host.Services.GetRequiredService<IMigrationRunner>();
        Assert.Equal(0, await runner.RunPendingAsync()); // 宿主已应用,再跑为零
        var fsql = host.Services.GetRequiredService<IDbFactory>().Get("main");
        Assert.True(MigrationHost.TableExists(fsql, "mig_orders"));
        await host.StopAsync();
    }

    [Fact]
    public async Task 迁移失败_宿主启动被阻止()
    {
        var db = $"fail-{Guid.NewGuid():N}";
        using var host = BuildHost(s => s
            .AddNexMeshMigration<V1000_CreateOrders>()
            .AddNexMeshMigration<V2500_FailMidWay>(), enabled: true, dbName: db);

        // 后台服务默认策略:未处理异常 → StopHost → StartAsync 抛出
        await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync());
    }

    [Fact]
    public async Task 并发触发RunPending_守卫串行化_幂等不双应用()
    {
        using var host = new MigrationHost(s => s.AddNexMeshMigration<V1000_CreateOrders>());
        // 并发两次 RunPendingAsync:守卫串行化 + 记录幂等 → 恰好一条记录、一行种子
        var results = await Task.WhenAll(host.Runner.RunPendingAsync(), host.Runner.RunPendingAsync());

        Assert.Equal(1, results.Sum());
        Assert.Equal(1, host.Scalar("SELECT COUNT(*) FROM nexmesh_migration"));
        Assert.Equal(1, host.Scalar("SELECT COUNT(*) FROM mig_orders"));
    }
}

/// <summary>迁移执行语义实测:顺序/幂等/回滚/校验/多源/回退。</summary>
public class MigrationRunnerTests
{
    [Fact]
    public async Task 升序应用_顺序正确_记录齐全()
    {
        using var host = new MigrationHost(s => s
            .AddNexMeshMigration<V3000_SeedMore>()
            .AddNexMeshMigration<V1000_CreateOrders>()
            .AddNexMeshMigration<V2000_AddRemark>());
        var applied = await host.Runner.RunPendingAsync();

        Assert.Equal(3, applied);
        Assert.Equal(1, host.Scalar("SELECT COUNT(*) FROM mig_orders WHERE note='seed-v1000' AND remark='v2000'"));
        Assert.Equal(1, host.Scalar("SELECT COUNT(*) FROM mig_orders WHERE note='seed-v3000'"));
        // 记录表:三条,升序可查
        Assert.Equal(3, host.Scalar("SELECT COUNT(*) FROM nexmesh_migration"));
        Assert.Equal(1000, host.Scalar("SELECT MIN(version) FROM nexmesh_migration"));
    }

    [Fact]
    public async Task 幂等重跑_零应用_数据不重复()
    {
        using var host = new MigrationHost(s => s.AddNexMeshMigration<V1000_CreateOrders>());
        await host.Runner.RunPendingAsync();
        var second = await host.Runner.RunPendingAsync();

        Assert.Equal(0, second);
        Assert.Equal(1, host.Scalar("SELECT COUNT(*) FROM mig_orders"));
        Assert.Equal(1, host.Scalar("SELECT COUNT(*) FROM nexmesh_migration"));
    }

    [Fact]
    public async Task 中途失败_当条整体回滚_前条保留_后条不执行()
    {
        using var host = new MigrationHost(s => s
            .AddNexMeshMigration<V1000_CreateOrders>()
            .AddNexMeshMigration<V2500_FailMidWay>()
            .AddNexMeshMigration<V3000_SeedMore>());
        await Assert.ThrowsAnyAsync<Exception>(() => host.Runner.RunPendingAsync()); // 失败必须抛出(fail-fast)

        // V2500 的表和行都被回滚(异步 UpAsync 真被事务覆盖——本用例即该语义的实证)
        Assert.Equal(0, host.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name='mig_partial'"));
        // V1000 已提交保留;V3000 未执行
        Assert.Equal(1, host.Scalar("SELECT COUNT(*) FROM mig_orders WHERE note='seed-v1000'"));
        Assert.Equal(0, host.Scalar("SELECT COUNT(*) FROM mig_orders WHERE note='seed-v3000'"));
        Assert.Equal(1, host.Scalar("SELECT COUNT(*) FROM nexmesh_migration"));
        Assert.Equal(1000, host.Scalar("SELECT MAX(version) FROM nexmesh_migration"));
    }

    [Fact]
    public async Task 同源重复版本_启动报错()
    {
        using var host = new MigrationHost(s => s
            .AddNexMeshMigration<Dup_A>()
            .AddNexMeshMigration<Dup_B>());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Runner.RunPendingAsync());
        Assert.Contains("重复版本", ex.Message);
    }

    [Fact]
    public async Task 已发布迁移被篡改_报错且篡改版不执行()
    {
        var dbName = $"shared-{Guid.NewGuid():N}";
        using (var first = new MigrationHost(s => s.AddNexMeshMigration<V1000_CreateOrders>(), sharedDbName: dbName))
        {
            await first.Runner.RunPendingAsync();
        }
        using (var second = new MigrationHost(s => s.AddNexMeshMigration<V1000_Tampered>(), sharedDbName: dbName))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => second.Runner.RunPendingAsync());
            Assert.Contains("Checksum", ex.Message);
        }
    }

    [Fact]
    public async Task 双数据源_各自独立迁移()
    {
        using var host = new MigrationHost(s => s
            .AddNexMeshMigration<V1000_CreateOrders>()
            .AddNexMeshMigration<His_V100>(), withHisSource: true);
        var applied = await host.Runner.RunPendingAsync();

        Assert.Equal(2, applied);
        Assert.True(MigrationHost.TableExists(host.MainDb, "mig_orders"));
        Assert.True(MigrationHost.TableExists(host.HisDb, "mig_his"));
        // 记录按源隔离(各自落在各自库的记录表里)
        Assert.Equal(1, host.Scalar("SELECT COUNT(*) FROM nexmesh_migration WHERE source='main' AND version=1000"));
        Assert.Equal(1, Convert.ToInt64(host.HisDb.Ado.ExecuteScalar("SELECT COUNT(*) FROM nexmesh_migration WHERE source='his' AND version=100")));
    }

    [Fact]
    public async Task 回退执行Down并删记录_重跑可再应用()
    {
        using var host = new MigrationHost(s => s
            .AddNexMeshMigration<V1000_CreateOrders>()
            .AddNexMeshMigration<V2000_AddRemark>());
        await host.Runner.RunPendingAsync();

        await host.Runner.RevertAsync("main", 2000);
        Assert.Equal(1, host.Scalar("SELECT COUNT(*) FROM nexmesh_migration")); // 只剩 V1000
        var reapplied = await host.Runner.RunPendingAsync();
        Assert.Equal(1, reapplied); // V2000 可再次应用
    }

    [Fact]
    public async Task 无迁移时返回零()
    {
        using var host = new MigrationHost();
        Assert.Equal(0, await host.Runner.RunPendingAsync());
    }
}

/// <summary>SSS+ 评审补充:Revert 语义边界。</summary>
public class RevertGuardTests
{
    [Fact]
    public async Task 回滚未应用的迁移_报错且不执行Down()
    {
        using var host = new MigrationHost(s => s.AddNexMeshMigration<V1000_CreateOrders>());
        // 未 RunPending 即直接 Revert:必须拒绝(Down 若执行会 DROP 未由该迁移创建的表)
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Runner.RevertAsync("main", 1000));
        Assert.Contains("尚未应用", ex.Message);
        Assert.Equal(0, host.Scalar("SELECT COUNT(*) FROM nexmesh_migration"));
    }

    [Fact]
    public async Task 回滚不存在的版本_报错()
    {
        using var host = new MigrationHost(s => s.AddNexMeshMigration<V1000_CreateOrders>());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Runner.RevertAsync("main", 999999));
        Assert.Contains("未注册迁移", ex.Message);
    }
}

/// <summary>分布式锁语义(真实 Redis,无 NEXMESH_TEST_REDIS 时软跳过)。</summary>
public class MigrationLockTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("NEXMESH_TEST_REDIS");

    [Fact]
    public async Task 互斥_同一key仅一个实例持锁()
    {
        if (ConnectionString is null) return;
        using var redis = StackExchange.Redis.ConnectionMultiplexer.Connect(ConnectionString);
        var key = $"test:mig-lock:{Guid.NewGuid():N}";
        var lock1 = new RedisMigrationLock(redis, key, TimeSpan.FromSeconds(10));
        var lock2 = new RedisMigrationLock(redis, key, TimeSpan.FromSeconds(10));

        using var lease1 = await lock1.TryAcquireAsync();
        Assert.NotNull(lease1);
        Assert.Null(await lock2.TryAcquireAsync()); // 互斥:第二实例拿不到

        lease1!.Dispose(); // 释放
        var lease2 = await lock2.TryAcquireAsync(); // 接管
        Assert.NotNull(lease2);
        lease2!.Dispose();
    }

    [Fact]
    public async Task 进程崩溃语义_租约过期后其他实例可接管()
    {
        if (ConnectionString is null) return;
        using var redis = StackExchange.Redis.ConnectionMultiplexer.Connect(ConnectionString);
        var key = $"test:mig-lock:{Guid.NewGuid():N}";
        var survivor = new RedisMigrationLock(redis, key, TimeSpan.FromSeconds(10));

        // 模拟崩溃残留:持有者进程已死(续期循环随之终止),Redis 中只剩无人续期的短租约锁
        await redis.GetDatabase().StringSetAsync(key, "crashed-owner-token", TimeSpan.FromMilliseconds(600));
        Assert.Null(await survivor.TryAcquireAsync()); // 租约未过期:拿不到

        await Task.Delay(TimeSpan.FromMilliseconds(900)); // 租约自然过期
        var taken = await survivor.TryAcquireAsync();
        Assert.NotNull(taken); // survivor 接管——无死锁
        taken!.Dispose();
    }

    [Fact]
    public async Task 续期保活_长迁移期间锁不丢()
    {
        if (ConnectionString is null) return;
        using var redis = StackExchange.Redis.ConnectionMultiplexer.Connect(ConnectionString);
        var key = $"test:mig-lock:{Guid.NewGuid():N}";
        // 短租约(1.2s)+续期(0.6s 周期);持锁 2.5s(跨多个租约期)后他人仍拿不到
        var holder = new RedisMigrationLock(redis, key, TimeSpan.FromMilliseconds(1200));
        var other = new RedisMigrationLock(redis, key, TimeSpan.FromSeconds(10));

        using var lease = await holder.TryAcquireAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(2500)); // 无续期早过期了
        Assert.Null(await other.TryAcquireAsync()); // 续期保活:锁仍属 holder
    }

    [Fact]
    public async Task Runner集成_双实例并发迁移_恰好应用一次()
    {
        if (ConnectionString is null) return;
        var dbName = $"lock-runner-{Guid.NewGuid():N}";
        using var redis = StackExchange.Redis.ConnectionMultiplexer.Connect(ConnectionString);
        var lockKey = $"test:mig-runner:{Guid.NewGuid():N}";

        // 两个独立 ServiceProvider 模拟两实例,共享同一 SQLite 共享内存库与 Redis 锁
        var tasks = new List<Task<int>>();
        for (var i = 0; i < 2; i++)
        {
            var services = new ServiceCollection();
            services.AddNexMesh();
            services.AddNexMeshDb("main", o =>
            {
                o.DataType = DataType.Sqlite;
                o.ConnectionString = $"Data Source=file:{dbName}?mode=memory&cache=shared";
                o.AutoSyncStructure = false;
            });
            services.AddNexMeshMigration<V1000_CreateOrders>();
            var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
            services.AddNexMeshMigrations(config, typeof(NexMeshMigration).Assembly);
            services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(redis);
            services.AddSingleton<IMigrationLock>(_ => new RedisMigrationLock(redis, lockKey, TimeSpan.FromSeconds(10)));
            var provider = services.BuildServiceProvider();
            tasks.Add(Task.Run(() => provider.GetRequiredService<IMigrationRunner>().RunPendingAsync()));
        }
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Sum()); // 恰好一个实例应用(另一实例幂等跳过)
    }
}

/// <summary>SSS+ 审查补充:读路径与写路径互斥(ListApplied 不吐中间态)。</summary>
public class ListAppliedConcurrencyTests
{
    [Fact]
    public async Task ListApplied与RunPending并发_读路径过闸_不吐中间态且恰好一次()
    {
        using var host = new MigrationHost(s => s.AddNexMeshMigration<V1000_CreateOrders>());
        // 并发:一面跑迁移,一面反复查记录——闸保证查询要么见 0 条(迁移前)要么 1 条(完成后),绝无中间态(半行/重复)
        var querying = Task.Run(async () =>
        {
            var counts = new HashSet<int>();
            for (var i = 0; i < 200; i++)
            {
                var applied = await host.Runner.ListAppliedAsync();
                counts.Add(applied.Count);
                await Task.Delay(2);
            }
            return counts;
        });
        var migrated = await host.Runner.RunPendingAsync();
        var seen = await querying;

        Assert.Equal(1, migrated);
        // 只允许两种观察值:0(迁移前)或 1(迁移后)——不允许任何中间值
        Assert.All(seen, c => Assert.True(c is 0 or 1, $"观察到中间态计数:{c}"));
        Assert.Contains(1, seen); // 最终必然可见
    }
}
