namespace NexMesh.Data.FreeSql.Tests;

using global::FreeSql.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexMesh.Core;
using NexMesh.Core.Context;

/// <summary>
/// 攻击者视角(C):软删+租户全局过滤的缺口补测。既有 TenantFilterTests 覆盖
/// "FillCreate 填充/跨租户读不可见/后台快照租户",本文件只补未覆盖的三类缺口:
///   ① TenantHelper.Current == null(无租户上下文)时读路径 fail-closed:租户行一律不可见,不降级为"不过滤";
///   ② 写路径 UPDATE 是否同样被全局过滤拦截(过滤只挂 Select 则跨租户直改可行=高危越权);
///   ③ 软删行:读不可见之外,UPDATE 亦不可达、物理行仍在(标记不物理删)。
/// 实体自带正控(本租户 UPDATE 照常生效),排除"过滤全关"的假阴性。
/// SQLite 文件库(%TEMP% 唯一名,finally 删除)。
/// </summary>
public class CTenantSoftDeleteGapTests
{
    /// <summary>租户+软删双标记实体(隔离于本文件)。</summary>
    public class CTenantRow : ISoftDelete, ITenantEntity
    {
        [Column(IsPrimary = true, IsIdentity = true)]
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public bool IsDeleted { get; set; }
        public string TenantId { get; set; } = "";
    }

    private static (ServiceProvider Provider, string DbPath) NewProvider()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"nexmesh-c-tenant-{Guid.CreateVersion7():N}.db");
        var services = new ServiceCollection();
        services.AddNexMesh();
        services.AddSingleton<IHostApplicationLifetime>(new FakeLifetime());
        services.AddNexMeshDb("main", o =>
        {
            o.DataType = global::FreeSql.DataType.Sqlite;
            o.ConnectionString = $"Data Source={dbPath}";
            o.AutoSyncStructure = true;
        });
        return (services.BuildServiceProvider(), dbPath);
    }

    private static async Task<long> SeedAsync(ServiceProvider provider, string tenant, string title)
    {
        var repo = provider.GetRequiredService<IRepository<CTenantRow>>();
        using (provider.GetRequiredService<INexMeshContextAccessor>()
                   .Enter(new NexMeshContext { UserId = $"u-{tenant}", TenantId = tenant }))
        using (TenantHelper.Enter(tenant))
        {
            var row = await repo.AddAsync(new CTenantRow { Title = title });
            return row.Id;
        }
    }

    private static void DeleteDbQuietly(string dbPath)
    {
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

    [Fact]
    public async Task 无租户上下文_读路径fail_closed_任何租户行不可见()
    {
        var (provider, dbPath) = NewProvider();
        try
        {
            var db = provider.GetRequiredService<NexMesh.Data.IDbFactory>().Get();
            var idA = await SeedAsync(provider, "hosp-A", "A 的单");
            await SeedAsync(provider, "hosp-B", "B 的单");

            // 前置:作用域已还原,当前无租户上下文
            Assert.Null(TenantHelper.Current);

            // fail-closed:null 匹配"未指定租户"而非"不过滤"——两租户的行都不可见
            Assert.Empty(await db.Select<CTenantRow>().ToListAsync());
            Assert.Equal(0, await db.Select<CTenantRow>().CountAsync());
            Assert.Null(await db.Select<CTenantRow>().Where(r => r.Id == idA).FirstAsync());
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
            DeleteDbQuietly(dbPath);
        }
    }

    [Fact]
    public async Task 无租户上下文_UPDATE写路径同样fail_closed_跨租户直改被拦截()
    {
        var (provider, dbPath) = NewProvider();
        try
        {
            var db = provider.GetRequiredService<NexMesh.Data.IDbFactory>().Get();
            var idA = await SeedAsync(provider, "hosp-A", "A 的单");

            // 正控:本租户上下文内 UPDATE 照常生效(排除过滤全关假阴性)
            using (provider.GetRequiredService<INexMeshContextAccessor>()
                       .Enter(new NexMeshContext { UserId = "uA", TenantId = "hosp-A" }))
            using (TenantHelper.Enter("hosp-A"))
            {
                var affected = await db.Update<CTenantRow>()
                    .Set(r => r.Title, "A 自己改")
                    .Where(r => r.Id == idA)
                    .ExecuteAffrowsAsync();
                Assert.Equal(1, affected);
            }

            // 攻击:无租户上下文直改 A 的行——全局过滤必须在 IUpdate 上同样生效
            Assert.Null(TenantHelper.Current);
            var hacked = await db.Update<CTenantRow>()
                .Set(r => r.Title, "越权篡改")
                .Where(r => r.Id == idA)
                .ExecuteAffrowsAsync();
            Assert.Equal(0, hacked); // 若为 1:IUpdate 未挂租户过滤,跨院区直改可行

            // 复核:库中值未被篡改
            using (provider.GetRequiredService<INexMeshContextAccessor>()
                       .Enter(new NexMeshContext { UserId = "uA", TenantId = "hosp-A" }))
            using (TenantHelper.Enter("hosp-A"))
            {
                var row = await db.Select<CTenantRow>().Where(r => r.Id == idA).FirstAsync();
                Assert.Equal("A 自己改", row!.Title);
            }
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
            DeleteDbQuietly(dbPath);
        }
    }

    [Fact]
    public async Task 软删行_读改双不可达_物理行仍在()
    {
        var (provider, dbPath) = NewProvider();
        try
        {
            var db = provider.GetRequiredService<NexMesh.Data.IDbFactory>().Get();
            var repo = provider.GetRequiredService<IRepository<CTenantRow>>();
            var accessor = provider.GetRequiredService<INexMeshContextAccessor>();
            var id = await SeedAsync(provider, "hosp-A", "待软删");

            // 无租户上下文软删:fail-closed 判负(行在过滤下不可见),行不得被删
            Assert.Null(TenantHelper.Current);
            Assert.False(await repo.SoftDeleteAsync(id));
            Assert.Equal(0, Convert.ToInt64(
                db.Ado.ExecuteScalar("SELECT COUNT(*) FROM CTenantRow WHERE IsDeleted=1")));

            // 本租户上下文软删:成功
            using (accessor.Enter(new NexMeshContext { UserId = "uA", TenantId = "hosp-A" }))
            using (TenantHelper.Enter("hosp-A"))
            {
                Assert.True(await repo.SoftDeleteAsync(id));
            }

            // 读:全局过滤隐藏
            Assert.Empty(await db.Select<CTenantRow>().ToListAsync());
            Assert.Null(await repo.FindAsync(id));

            // 写:软删行不可再被 UPDATE 改写(过滤在写路径同样生效)
            var affected = await db.Update<CTenantRow>()
                .Set(r => r.Title, "复活")
                .Where(r => r.Id == id)
                .ExecuteAffrowsAsync();
            Assert.Equal(0, affected);

            // 软删=标记:物理行仍在(可审计可恢复,不物理删)
            Assert.Equal(1, Convert.ToInt64(
                db.Ado.ExecuteScalar("SELECT COUNT(*) FROM CTenantRow WHERE IsDeleted=1")));
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
            DeleteDbQuietly(dbPath);
        }
    }
}
