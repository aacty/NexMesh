using Microsoft.Extensions.DependencyInjection;
using NexMesh.Core;
using NexMesh.Core.Context;
using NexMesh.Data;

namespace NexMesh.Data.FreeSql.Tests;

/// <summary>SSS+第二轮:ITenantEntity 全局过滤 + FillCreate 自动填充。</summary>
public class TenantFilterTests
{
    /// <summary>租户实体:软删 + 租户。</summary>
    public class TenantOrder : ISoftDelete, ITenantEntity
    {
        [global::FreeSql.DataAnnotations.Column(IsPrimary = true, IsIdentity = true)]
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public bool IsDeleted { get; set; }
        public string TenantId { get; set; } = "";
    }

    private static IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddNexMesh();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(new FakeLifetime());
        services.AddNexMeshDb("main", o =>
        {
            o.DataType = global::FreeSql.DataType.Sqlite;
            o.ConnectionString = $"Data Source=file:db{Guid.NewGuid():N}?mode=memory&cache=shared";
            o.AutoSyncStructure = true;
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task FillCreate自动填充租户()
    {
        var provider = BuildProvider();
        var repo = provider.GetRequiredService<IRepository<TenantOrder>>();
        var accessor = provider.GetRequiredService<INexMeshContextAccessor>();

        TenantOrder order;
        using (accessor.Enter(new NexMeshContext { UserId = "u1", TenantId = "hosp-A" }))
        using (TenantHelper.Enter("hosp-A"))
        {
            order = await repo.AddAsync(new TenantOrder { Title = "A 的单" });
        }

        Assert.Equal("hosp-A", order.TenantId);
    }

    [Fact]
    public async Task 跨租户查询不可见()
    {
        var provider = BuildProvider();
        var repo = provider.GetRequiredService<IRepository<TenantOrder>>();
        var db = provider.GetRequiredService<NexMesh.Data.IDbFactory>().Get();
        var accessor = provider.GetRequiredService<INexMeshContextAccessor>();

        // 租户 A 写入
        using (accessor.Enter(new NexMeshContext { UserId = "uA", TenantId = "hosp-A" }))
        using (TenantHelper.Enter("hosp-A"))
        {
            await repo.AddAsync(new TenantOrder { Title = "A 的单" });
        }
        // 租户 B 写入
        using (accessor.Enter(new NexMeshContext { UserId = "uB", TenantId = "hosp-B" }))
        using (TenantHelper.Enter("hosp-B"))
        {
            await repo.AddAsync(new TenantOrder { Title = "B 的单" });
        }

        // 租户 A 视角:只看到 A 的单
        using (accessor.Enter(new NexMeshContext { UserId = "uA", TenantId = "hosp-A" }))
        using (TenantHelper.Enter("hosp-A"))
        {
            var list = await db.Select<TenantOrder>().ToListAsync();
            var single = Assert.Single(list);
            Assert.Equal("A 的单", single.Title);
        }
    }

    [Fact]
    public async Task RunAsync快照租户同步到全局过滤()
    {
        var provider = BuildProvider();
        var db = provider.GetRequiredService<NexMesh.Data.IDbFactory>().Get();
        var runner = provider.GetRequiredService<INexMeshBackgroundRunner>();
        var accessor = provider.GetRequiredService<INexMeshContextAccessor>();

        // 租户 A 数据
        using (accessor.Enter(new NexMeshContext { UserId = "uA", TenantId = "hosp-A" }))
        using (TenantHelper.Enter("hosp-A"))
        {
            await db.Insert(new TenantOrder { Title = "A 的单", TenantId = "hosp-A" }).ExecuteAffrowsAsync();
        }

        // 模拟:请求上下文是租户 B,但后台任务按快照租户 A 执行
        using (accessor.Enter(new NexMeshContext { UserId = "uB", TenantId = "hosp-B" }))
        using (TenantHelper.Enter("hosp-B"))
        {
            List<TenantOrder>? seen = null;
            await runner.RunAsync(
                new NexMeshContext { UserId = "uA", TenantId = "hosp-A" },
                async (sp, ct) =>
                {
                    seen = await db.Select<TenantOrder>().ToListAsync(ct);
                });

            // 后台执行期间读到的租户是快照 A(而非触发请求的 B)
            Assert.NotNull(seen);
            Assert.Single(seen!);
            Assert.Equal("A 的单", seen![0].Title);
        }
    }
}

public sealed class FakeLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() { }
}
