namespace NexMesh.Data.Migrations;

using System.Diagnostics;
using FreeSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// 迁移执行器:按源分组校验并应用。
/// 部署约束:单实例执行语义——并发防护(SemaphoreSlim)仅限进程内;
/// 多实例同时启动会竞争应用同一迁移,记录表唯一索引将使后提交实例启动失败(fail-fast)。
/// 滚动部署场景请先完成迁移实例再扩容(或确保仅一个实例执行迁移),分布式锁为后续版本计划。
/// 事务语义:每条迁移 BeginTransaction → UpAsync → 写记录 → Commit,失败 Rollback 并抛出(fail-fast);
/// 单源内顺序执行(单例 IFreeSql 上的事务不并发),多源之间同样顺序(启动期一次性完成)。
/// 防篡改:同 Version 迁移的指纹(Checksum)与历史记录不一致 → 启动报错,已发布迁移不可修改。
/// </summary>
public sealed class NexMeshMigrationRunner(
    IServiceProvider services,
    ILogger<NexMeshMigrationRunner>? logger = null,
    IMigrationLock? distributedLock = null) : IMigrationRunner
{
    /// <summary>并发防护:显式调用与宿主自动执行叠加时串行化,防止同一迁移被双应用。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<int> RunPendingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        // 分布式锁(可选):多实例部署时保证仅一个实例执行;拿不到则等待重试(锁持有者完成后释放)
        IMigrationLease? lease = null;
        if (distributedLock is not null)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            while ((lease = await distributedLock.TryAcquireAsync(cancellationToken)) is null)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new InvalidOperationException(
                        "[NexMesh.Migrations] 获取迁移分布式锁超时(90s)——另一实例可能持锁未放或迁移耗时过长");
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }
        try
        {
            return await RunPendingCoreAsync(cancellationToken);
        }
        finally
        {
            lease?.Dispose();
            _gate.Release();
        }
    }

    private async Task<int> RunPendingCoreAsync(CancellationToken cancellationToken)
    {
        // 每次运行按作用域解析迁移实例(不俘获瞬时实例;迁移类应保持无状态)
        using var scope = services.CreateScope();
        var all = scope.ServiceProvider.GetServices<NexMeshMigration>().ToArray();
        if (all.Length == 0)
        {
            return 0;
        }

        // 校验一:同源内重复版本
        var duplicates = all.GroupBy(m => (m.Source, m.Version))
            .Where(g => g.Count() > 1)
            .ToList();
        if (duplicates.Count > 0)
        {
            var detail = string.Join("; ", duplicates.Select(g =>
                $"Source={g.Key.Source} Version={g.Key.Version}: {string.Join(",", g.Select(m => m.GetType().Name))}"));
            throw new InvalidOperationException($"[NexMesh.Migrations] 存在重复版本号:{detail}");
        }

        var applied = 0;
        foreach (var sourceGroup in all.GroupBy(m => m.Source, StringComparer.OrdinalIgnoreCase))
        {
            var db = sourceGroup.First().ResolveDb(services);
            EnsureRecordTable(db);

            var records = db.Select<MigrationRecordEntity>()
                .Where(r => r.Source == sourceGroup.Key)
                .ToList()
                .ToDictionary(r => r.Version);

            // 校验二:已发布迁移的指纹不得变化(防篡改历史)
            foreach (var migration in sourceGroup)
            {
                if (records.TryGetValue(migration.Version, out var record)
                    && !string.Equals(record.Checksum, migration.Checksum, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"[NexMesh.Migrations] 迁移 {migration.Version}({migration.Name}) 已被发布且被改动(Checksum 不一致);" +
                        "已应用的迁移不可修改,请新增一条迁移修正。");
                }
            }

            foreach (var migration in sourceGroup.OrderBy(m => m.Version))
            {
                if (records.ContainsKey(migration.Version))
                {
                    continue; // 已应用,幂等跳过
                }
                // 逐条之间检查取消:停机/显式取消在应用下一条前生效(UpAsync 内部由迁移自决是否尊重令牌)
                cancellationToken.ThrowIfCancellationRequested();
                var elapsed = Apply(db, sourceGroup.Key, migration, cancellationToken);
                applied++;
                logger?.LogInformation("[NexMesh.Migrations] 已应用 {Source}#{Version} {Name}({Elapsed}ms)",
                    sourceGroup.Key, migration.Version, migration.Name, elapsed);
            }
        }
        if (applied > 0)
        {
            logger?.LogInformation("[NexMesh.Migrations] 本次共应用 {Count} 条迁移", applied);
        }
        return applied;
    }

    public async Task RevertAsync(string source, long version, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await RevertCoreAsync(source, version, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>列出待应用迁移:与写路径同闸(不吐 RunPending 中间态);库不可达源返回空。</summary>
    public async Task<List<MigrationSummary>> ListPendingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var scope = services.CreateScope();
            var migrations = scope.ServiceProvider.GetServices<NexMeshMigration>()
                .OrderBy(m => m.Version)
                .ToList();
            var factory = scope.ServiceProvider.GetService<NexMesh.Data.IDbFactory>();
            if (factory is null)
            {
                return [];
            }
            var result = new List<MigrationSummary>();
            foreach (var sourceGroup in migrations.GroupBy(m => m.Source, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var db = factory.Get(sourceGroup.Key);
                    EnsureRecordTable(db);
                    var applied = db.Select<MigrationRecordEntity>()
                        .Where(r => r.Source == sourceGroup.Key)
                        .ToList()
                        .Select(r => r.Version)
                        .ToHashSet();
                    result.AddRange(sourceGroup
                        .Where(m => !applied.Contains(m.Version))
                        .Select(m => new MigrationSummary(m.Source, m.Version, m.Name, DateTime.MinValue, 0)));
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "[NexMesh.Migrations] 读取源 {Source} 待应用迁移失败(面板降级为空)", sourceGroup.Key);
                }
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>列出已应用迁移(读路径与 RunPending/Revert 共享闸——避免读到 Approved 未 Executed 中间态或建表 DDL 撞写事务;
    /// 注册源 ∪ main 之外的"已卸载迁移的源"不展示(边界由注释固化);库不可达的源降级空)。</summary>
    public async Task<List<MigrationSummary>> ListAppliedAsync(CancellationToken cancellationToken = default)
    {
        // 展示查询毫秒级:与写路径互斥不影响可用性,却保证不吐中间态
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var scope = services.CreateScope();
            var sources = scope.ServiceProvider.GetServices<NexMeshMigration>()
                .Select(m => m.Source)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!sources.Contains("main", StringComparer.OrdinalIgnoreCase))
            {
                sources.Insert(0, "main"); // main 库的记录即便迁移类已卸载也要展示
            }

            var result = new List<MigrationSummary>();
            var factory = scope.ServiceProvider.GetService<NexMesh.Data.IDbFactory>();
            if (factory is null)
            {
                return result; // 无数据层:无迁移记录可读
            }
            foreach (var source in sources)
            {
                try
                {
                    var db = factory.Get(source);
                    EnsureRecordTable(db);
                    var records = db.Select<MigrationRecordEntity>()
                        .Where(r => r.Source == source)
                        .OrderBy(r => r.Version)
                        .ToList();
                    result.AddRange(records.Select(r => new MigrationSummary(r.Source, r.Version, r.Name, r.AppliedAt, r.DurationMs)));
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "[NexMesh.Migrations] 读取源 {Source} 迁移记录失败(面板降级为空)", source);
                }
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RevertCoreAsync(string source, long version, CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var migration = scope.ServiceProvider.GetServices<NexMeshMigration>().FirstOrDefault(m =>
            m.Source.Equals(source, StringComparison.OrdinalIgnoreCase) && m.Version == version)
            ?? throw new InvalidOperationException($"[NexMesh.Migrations] 未注册迁移 Source={source} Version={version}");
        var db = migration.ResolveDb(scope.ServiceProvider);
        EnsureRecordTable(db);

        // 未应用过的迁移不允许回滚:DownAsync 可能误删无关对象(如 DROP 别的表)
        var record = db.Select<MigrationRecordEntity>()
            .Where(r => r.Source == source && r.Version == version)
            .First();
        if (record is null)
        {
            throw new InvalidOperationException($"[NexMesh.Migrations] 迁移 Source={source} Version={version} 尚未应用,不可回滚");
        }
        // 设计取舍:Revert 不做 Checksum 校验——回滚是显式运维动作,操作者对后果负责

        // Transaction(Action):块内异常自动回滚(FreeSql 同步事务的唯一边界形态)
        db.Ado.Transaction(() =>
        {
            migration.DownAsync(db, cancellationToken).GetAwaiter().GetResult();
            db.Delete<MigrationRecordEntity>()
                .Where(r => r.Source == source && r.Version == version)
                .ExecuteAffrows();
        });
        logger?.LogInformation("[NexMesh.Migrations] 已回滚 {Source}#{Version} {Name}", source, version, migration.Name);
    }

    /// <summary>单迁移单事务:UpAsync + 记录写入同事务,失败一并回滚;返回耗时(毫秒,含记录写入)。
    /// 同步方法:FreeSql 事务为同步 API,UpAsync 经 GetAwaiter().GetResult() 并入事务边界
    /// (无同步上下文环境安全;回滚语义由"中途失败_当条整体回滚"测试实证)。</summary>
    private static int Apply(IFreeSql db, string source, NexMeshMigration migration, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        db.Ado.Transaction(() =>
        {
            migration.UpAsync(db, cancellationToken).GetAwaiter().GetResult();
            db.Insert(new MigrationRecordEntity
            {
                Source = source,
                Version = migration.Version,
                Name = migration.Name,
                Checksum = migration.Checksum,
                AppliedAt = DateTime.Now,
                DurationMs = 0, // 以 stop 后回填
            }).ExecuteAffrows();
            clock.Stop();
            // 回填真实耗时(含记录写入)
            db.Update<MigrationRecordEntity>()
                .Set(r => r.DurationMs, (int)clock.ElapsedMilliseconds)
                .Where(r => r.Source == source && r.Version == migration.Version)
                .ExecuteAffrows();
        });
        return (int)clock.ElapsedMilliseconds;
    }

    /// <summary>记录表自建(照 AI.Approval 的 AsyncLazy+SyncStructure 先例)。</summary>
    private static void EnsureRecordTable(IFreeSql db)
    {
        db.CodeFirst.SyncStructure<MigrationRecordEntity>();
    }
}
