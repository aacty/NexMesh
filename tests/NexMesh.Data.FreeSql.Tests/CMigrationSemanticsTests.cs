namespace NexMesh.Data.Migrations.Tests;

using FreeSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.Core;

// ═══════════════ 攻击测试迁移集(仅本文件使用) ═══════════════

/// <summary>
/// 无锁并发重叠迁移:先到者(A)写种子后在未提交事务内悬停;后到者(B)轮询等 A 的记录提交后再写种子——
/// 保证 B 的写入必然落在 A 提交之后,排除 SQLITE_BUSY 干扰变量,B 的失败只可能来自
/// 记录表 (source,version) 唯一索引——即对 Runner 头注释"记录表唯一索引将使后提交实例启动失败(fail-fast)"
/// 这一声称的实证。
/// </summary>
public class CV1000_Overlap : NexMeshMigration
{
    /// <summary>进入 UpAsync 的次序计数:1=先到者(A),2+=后到者(B)。</summary>
    public static int Entries;

    public override long Version => 1000;
    public override string Name => "无锁并发重叠迁移";

    public override async Task UpAsync(IFreeSql db, CancellationToken ct)
    {
        if (Interlocked.Increment(ref Entries) == 1)
        {
            // 先到者(A):写种子,保持事务未提交悬停,给 B 留出"快照已读、事务未提交"的竞态窗口
            db.Ado.ExecuteNonQuery("INSERT INTO mig_c (note) VALUES ('c-seed')");
            await Task.Delay(300, ct);
            return;
        }
        // 后到者(B):轮询等 A 的迁移记录可见(=A 已提交)再写——B 的种子写入后
        // 紧接着的记录 INSERT 必撞唯一索引,整个事务回滚(种子一并回滚)
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Convert.ToInt64(db.Ado.ExecuteScalar(
                   "SELECT COUNT(*) FROM nexmesh_migration WHERE source='main' AND version=1000")) == 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("B 等待 A 提交超时——测试时序假设被破坏(非框架问题)");
            }
            await Task.Delay(20, ct);
        }
        db.Ado.ExecuteNonQuery("INSERT INTO mig_c (note) VALUES ('c-seed')");
    }
}

/// <summary>第一条 UpAsync 执行中途触发取消的迁移(取消信号注入器)。</summary>
public class CV1000_CancelSignal : NexMeshMigration
{
    public static CancellationTokenSource? CancelSource;

    public override long Version => 1000;
    public override string Name => "取消信号注入";

    public override Task UpAsync(IFreeSql db, CancellationToken ct)
    {
        db.Ado.ExecuteNonQuery("CREATE TABLE IF NOT EXISTS mig_cxl (id INTEGER PRIMARY KEY)");
        CancelSource?.Cancel(); // 第一条迁移执行中触发取消
        return Task.CompletedTask;
    }
}

public class CV2000_NeverRun : NexMeshMigration
{
    public static int Executed;

    public override long Version => 2000;
    public override string Name => "取消后不得执行";

    public override Task UpAsync(IFreeSql db, CancellationToken ct)
    {
        Interlocked.Increment(ref Executed);
        return Task.CompletedTask;
    }
}

/// <summary>
/// 攻击者视角(C):Runner 未覆盖语义补测——
/// ① 无 Redis(Noop 锁)时同版本双 Runner 并发:不变量"记录恰好一行、种子恰好一行、恰好一人应用"。
///    Runner 进程内 SemaphoreSlim 只防同一实例;两个独立 ServiceProvider(各自解析到 NoopMigrationLock)
///    才是文档"多实例部署"的对应物,此前仅有 Redis 锁版本覆盖(MigrationLockTests,无 Redis 形态无测试)。
/// ② 逐条间取消语义:已应用的保留提交、后续迁移不执行、以 OperationCanceledException fail-fast。
/// SQLite 用文件库(%TEMP% 唯一名,finally 删除)。
/// </summary>
public class CMigrationNoLockConcurrentRunnerTests : IDisposable
{
    private readonly ServiceProvider _providerA;
    private readonly ServiceProvider _providerB;
    private readonly string _dbPath;

    public CMigrationNoLockConcurrentRunnerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"nexmesh-c-mig-nolock-{Guid.CreateVersion7():N}.db");
        _providerA = BuildProvider();
        _providerB = BuildProvider();
        // 预建表并置于两事务之外(自动提交):迁移内不再做 DDL,排除建表撞锁干扰变量
        var dbA = _providerA.GetRequiredService<IDbFactory>().Get("main");
        dbA.CodeFirst.SyncStructure<MigrationRecordEntity>(); // 与 Runner.EnsureRecordTable 同 API
        dbA.Ado.ExecuteNonQuery("CREATE TABLE IF NOT EXISTS mig_c (id INTEGER PRIMARY KEY, note TEXT NOT NULL)");
    }

    /// <summary>两个独立 ServiceProvider 模拟两实例:未注册 IConnectionMultiplexer → IMigrationLock=NoopMigrationLock(无 Redis 部署形态)。</summary>
    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddNexMesh();
        services.AddNexMeshDb("main", o =>
        {
            o.DataType = DataType.Sqlite;
            o.ConnectionString = $"Data Source={_dbPath}";
            o.AutoSyncStructure = false; // 迁移负责记录表;业务表由测试预建
        });
        services.AddNexMeshMigration<CV1000_Overlap>();
        services.AddNexMeshMigrations(new ConfigurationBuilder().AddInMemoryCollection().Build(),
            typeof(NexMeshMigration).Assembly);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task 无Redis双Runner并发同版本_恰好一行记录一行种子_失败方整体回滚()
    {
        CV1000_Overlap.Entries = 0;
        var dbA = _providerA.GetRequiredService<IDbFactory>().Get("main");

        Exception? exA = null, exB = null;
        int appliedA = 0, appliedB = 0;

        // A 先进事务悬停,B 延迟 150ms 启动(B 的记录快照读必然发生在 A 提交前)
        var taskA = Task.Run(async () =>
        {
            try { appliedA = await _providerA.GetRequiredService<IMigrationRunner>().RunPendingAsync(); }
            catch (Exception ex) { exA = ex; }
        });
        await Task.Delay(150);
        var taskB = Task.Run(async () =>
        {
            try { appliedB = await _providerB.GetRequiredService<IMigrationRunner>().RunPendingAsync(); }
            catch (Exception ex) { exB = ex; }
        });
        await Task.WhenAll(taskA, taskB);

        var records = Convert.ToInt64(dbA.Ado.ExecuteScalar(
            "SELECT COUNT(*) FROM nexmesh_migration WHERE source='main' AND version=1000"));
        var seeds = Convert.ToInt64(dbA.Ado.ExecuteScalar(
            "SELECT COUNT(*) FROM mig_c WHERE note='c-seed'"));

        // 核心不变量:无论竞态交错如何,绝不双应用
        Assert.Null(exA);            // A(先启动、快照必空)必须干净胜出
        Assert.Equal(1, appliedA);
        Assert.True(appliedA + appliedB is 1, // 双应用 = 无 Redis 时唯一索引防线被击穿(高危)
            $"恰好一人应用,实际 A={appliedA} B={appliedB};B 异常={exB?.GetType().Name}:{exB?.Message}");
        Assert.Equal(1, records);    // 记录表 (source,version) 唯一索引防线
        Assert.Equal(1, seeds);      // 失败方事务整体回滚,不得留下半套数据
        Assert.Equal(0, appliedB);   // B 要么 fail-fast 抛出、要么快照晚于提交幂等跳过,均不报告已应用
    }

    public void Dispose()
    {
        (_providerA as IDisposable)?.Dispose();
        (_providerB as IDisposable)?.Dispose();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                if (File.Exists(_dbPath)) File.Delete(_dbPath);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(1000);
            }
        }
    }
}

public class CMigrationCancelBetweenMigrationsTests
{
    [Fact]
    public async Task 逐条间取消_已应用的保留提交_后续迁移不执行_抛取消异常()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"nexmesh-c-mig-cancel-{Guid.CreateVersion7():N}.db");
        CV1000_CancelSignal.CancelSource = null;
        CV2000_NeverRun.Executed = 0;
        var services = new ServiceCollection();
        try
        {
            services.AddNexMesh();
            services.AddNexMeshDb("main", o =>
            {
                o.DataType = DataType.Sqlite;
                o.ConnectionString = $"Data Source={dbPath}";
                o.AutoSyncStructure = false;
            });
            services.AddNexMeshMigration<CV1000_CancelSignal>();
            services.AddNexMeshMigration<CV2000_NeverRun>();
            services.AddNexMeshMigrations(new ConfigurationBuilder().AddInMemoryCollection().Build(),
                typeof(NexMeshMigration).Assembly);
            using var provider = services.BuildServiceProvider();
            var db = provider.GetRequiredService<IDbFactory>().Get("main");

            using var cts = new CancellationTokenSource();
            CV1000_CancelSignal.CancelSource = cts;
            // V1000 的 UpAsync 中途取消令牌:V1000 本身照常提交,循环在 V2000 前抛出(fail-fast)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => provider.GetRequiredService<IMigrationRunner>().RunPendingAsync(cts.Token));

            // 已应用的保留:V1000 记录与业务表均已提交,不因取消回滚
            Assert.Equal(1, Convert.ToInt64(db.Ado.ExecuteScalar(
                "SELECT COUNT(*) FROM nexmesh_migration WHERE source='main' AND version=1000")));
            Assert.Equal(1, Convert.ToInt64(db.Ado.ExecuteScalar(
                "SELECT COUNT(*) FROM sqlite_master WHERE name='mig_cxl'")));
            // 后续迁移不执行:逐条间 ThrowIfCancellationRequested 生效
            Assert.Equal(0, CV2000_NeverRun.Executed);
            Assert.Equal(0, Convert.ToInt64(db.Ado.ExecuteScalar(
                "SELECT COUNT(*) FROM nexmesh_migration WHERE version=2000")));
        }
        finally
        {
            CV1000_CancelSignal.CancelSource = null;
        }
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                if (File.Exists(dbPath)) File.Delete(dbPath);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(1000);
            }
        }
    }
}
