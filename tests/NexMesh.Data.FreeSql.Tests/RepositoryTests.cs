using FreeSql.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.Core;
using NexMesh.Data;

namespace NexMesh.Data.FreeSql.Tests;

public class RepositoryTests
{
    /// <summary>全约定实体:软删+审计+用户审计+乐观并发+雪花(非自增 long 主键)。</summary>
    public class Order : ISoftDelete, IAudited, IUserAudited, IHasConcurrencyStamp
    {
        [Column(IsPrimary = true)]
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public bool IsDeleted { get; set; }
        public DateTime CreateTime { get; set; }
        public DateTime? UpdateTime { get; set; }
        public string? CreateUserId { get; set; }
        public string? UpdateUserId { get; set; }
        public string ConcurrencyStamp { get; set; } = "";
    }

    private static IServiceProvider BuildProvider(string dbName)
    {
        var services = new ServiceCollection();
        services.AddNexMesh(); // ICurrentUser(此处无 HTTP/快照 → 匿名)
        services.AddNexMeshDb("main", o =>
        {
            o.DataType = global::FreeSql.DataType.Sqlite;
            o.ConnectionString = $"Data Source=file:{dbName}?mode=memory&cache=shared";
            o.AutoSyncStructure = true;
            o.WorkerId = 3;
        });
        return services.BuildServiceProvider();
    }

    private static string NewDb() => $"db-{Guid.NewGuid():N}";

    [Fact]
    public async Task 新增_雪花ID与审计字段自动填充()
    {
        var provider = BuildProvider(NewDb());
        var repo = provider.GetRequiredService<IRepository<Order>>();

        var order = await repo.AddAsync(new Order { Title = "O1" });
        var another = await repo.AddAsync(new Order { Title = "O2" });

        Assert.NotEqual(0, order.Id);                        // 雪花接管(非自增主键)
        Assert.NotEqual(order.Id, another.Id);               // 雪花唯一
        Assert.True(order.CreateTime > DateTime.MinValue);   // 时间审计
        Assert.NotEmpty(order.ConcurrencyStamp);             // 并发戳初始化
        Assert.False(order.IsDeleted);
    }

    [Fact]
    public async Task 软删除_全局过滤自动排除()
    {
        var dbName = NewDb();
        var provider = BuildProvider(dbName);
        var repo = provider.GetRequiredService<IRepository<Order>>();
        var fsql = provider.GetRequiredService<IFreeSql>();

        var a = await repo.AddAsync(new Order { Title = "keep" });
        var b = await repo.AddAsync(new Order { Title = "drop" });

        Assert.True(await repo.SoftDeleteAsync(b.Id));
        Assert.Equal(1, await repo.CountAsync());              // 全局过滤:计数自动只剩 1
        Assert.Null(await repo.FindAsync(b.Id));               // 点查同样被过滤
        Assert.NotNull(await repo.FindAsync(a.Id));
        // 物理数据仍在(可审计),DisableGlobalFilter 可穿透
        Assert.Equal(2, await fsql.Select<Order>()
            .DisableGlobalFilter("nexmesh-soft-delete").CountAsync());
    }

    [Fact]
    public async Task 乐观并发_戳不匹配抛409()
    {
        var provider = BuildProvider(NewDb());
        var repo = provider.GetRequiredService<IRepository<Order>>();

        var original = await repo.AddAsync(new Order { Title = "并发" });
        var stale = new Order { Id = original.Id, Title = "旧编辑", ConcurrencyStamp = original.ConcurrencyStamp };
        await repo.UpdateAsync(stale); // 第一次成功,库里戳已轮换

        var conflicted = new Order { Id = original.Id, Title = "用旧戳再改", ConcurrencyStamp = original.ConcurrencyStamp };
        var ex = await Assert.ThrowsAsync<MeshException>(() => repo.UpdateAsync(conflicted));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal(40902, ex.ErrorCode);
    }

    [Fact]
    public async Task 更新_时间审计自动刷新()
    {
        var provider = BuildProvider(NewDb());
        var repo = provider.GetRequiredService<IRepository<Order>>();

        var order = await repo.AddAsync(new Order { Title = "t" });
        order.Title = "t2";
        await repo.UpdateAsync(order);

        var reloaded = await repo.FindAsync(order.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("t2", reloaded!.Title);
        Assert.NotNull(reloaded.UpdateTime); // 更新时间自动填充
    }

    [Fact]
    public async Task 多库_命名互不串扰()
    {
        var services = new ServiceCollection();
        services.AddNexMesh();
        services.AddNexMeshDb("main", o =>
        {
            o.DataType = global::FreeSql.DataType.Sqlite;
            o.ConnectionString = $"Data Source=file:{NewDb()}?mode=memory&cache=shared";
            o.AutoSyncStructure = true;
        });
        services.AddNexMeshDb("his", o =>
        {
            o.DataType = global::FreeSql.DataType.Sqlite;
            o.ConnectionString = $"Data Source=file:{NewDb()}?mode=memory&cache=shared";
            o.AutoSyncStructure = true;
        });
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbFactory>();

        var mainDb = factory.Get("main");
        var hisDb = factory.Get("his");

        await mainDb.Insert(new Order { Id = 101, Title = "主库", CreateTime = DateTime.Now }).ExecuteAffrowsAsync();
        await hisDb.Insert(new Order { Id = 202, Title = "HIS库", CreateTime = DateTime.Now }).ExecuteAffrowsAsync();

        Assert.NotSame(mainDb, hisDb);
        Assert.NotNull(await mainDb.Select<Order>().Where(o => o.Id == 101).FirstAsync());
        Assert.NotNull(await hisDb.Select<Order>().Where(o => o.Id == 202).FirstAsync());
        Assert.Null(await mainDb.Select<Order>().Where(o => o.Id == 202).FirstAsync()); // 不串库
    }

    /// <summary>自增主键实体:Id 由数据库分配并回填,雪花严禁预填。</summary>
    public class AutoIdRow : IAudited
    {
        [Column(IsPrimary = true, IsIdentity = true)]
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public DateTime CreateTime { get; set; }
        public DateTime? UpdateTime { get; set; }
    }

    [Fact]
    public async Task 自增主键_数据库分配并回填_雪花不预填()
    {
        var provider = BuildProvider(NewDb());
        var repo = provider.GetRequiredService<IRepository<AutoIdRow>>();

        var row = await repo.AddAsync(new AutoIdRow { Name = "auto" });

        Assert.Equal(1, row.Id); // 首条自增 Id=1 且已回填(而非 0 或雪花值)
    }

    [Fact]
    public async Task 分页_总数与页数据正确()
    {
        var provider = BuildProvider(NewDb());
        var repo = provider.GetRequiredService<IRepository<Order>>();

        for (var i = 1; i <= 25; i++)
        {
            await repo.AddAsync(new Order { Title = $"O{i:D2}" });
        }

        var page2 = await repo.PagedAsync(pageIndex: 2, pageSize: 10);

        Assert.Equal(25, page2.TotalCount);
        Assert.Equal(10, page2.Items.Count);
    }
}

/// <summary>类型化数据源与 ID 生成器(对齐约定表面)。</summary>
public class DbSourceContractTests
{
    [Fact]
    public void AddDBSource_类型化注册_注入标记实例()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var mb = new NexMesh.Core.NexMeshBuilder(services, config);
        mb.AddDBSource<MarkerDbA>(new NexMesh.Data.FreeSqlDBOptions
        {
            DataType = global::FreeSql.DataType.Sqlite,
            ConnectionString = $"Data Source=dsrc-{Guid.NewGuid():N}.db;",
            AutoSyncStructure = true,
        });

        var provider = services.BuildServiceProvider();
        var source = provider.GetRequiredService<MarkerDbA>();

        Assert.NotNull(source);
        Assert.NotNull(source.SqlClient); // IFreeSql<MarkerDbA>(FreeSql 原生多库标记)
        Assert.True(provider.GetRequiredService<global::IFreeSql<MarkerDbA>>() is not null);
    }

    [Fact]
    public void IIdGenerator_雪花与字符串形态()
    {
        var generator = new NexMesh.Data.NexMeshIdGenerator();
        var id1 = generator.NextSnowflakeId();
        var id2 = generator.NextId();
        var str = generator.NextSnowflakeString();

        Assert.True(id1 > 0);
        Assert.True(id2 > 0);
        Assert.NotEqual(id1, id2);
        var next = generator.NextString();
        Assert.False(string.IsNullOrWhiteSpace(next));
        Assert.True(long.TryParse(next, out _));
        Assert.False(string.IsNullOrWhiteSpace(str));
    }

    public class MarkerDbA : NexMesh.Data.FreeSqlDBSourceBase<MarkerDbA>
    {
        public MarkerDbA(global::IFreeSql<MarkerDbA> fsql) : base(fsql)
        {
        }
    }
}
