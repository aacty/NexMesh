namespace NexMesh.Data;

using FreeSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>命名库工厂:默认 "main";SPD 的 BaseDB/HisBaseDB 多库形态。</summary>
public interface IDbFactory
{
    IFreeSql Get(string name = "main");
}

/// <summary>
/// 命名库注册:AddNexMeshDb("main", o => {...}) 可多次调用注册多库;
/// 软删实体自动挂全局过滤;WorkerId 进雪花配置;IFreeSql 以 Keyed 方式注册可注入指定库。
/// </summary>
public static class NexMeshDbServiceCollectionExtensions
{
    public static IServiceCollection AddNexMeshDb(
        this IServiceCollection services,
        string name = "main",
        Action<NexMeshDbOptions>? configure = null)
    {
        var options = new NexMeshDbOptions();
        configure?.Invoke(options);
        NexMeshSnowflake.Configure(options.WorkerId);

        var builder = new FreeSqlBuilder().UseConnectionString(options.DataType, options.ConnectionString);
        var autoSync = options.AutoSyncStructure;
        if (autoSync == true)
        {
            builder = builder.UseAutoSyncStructure(true);
        }
        var fsql = builder.Build();

        if (options.EnableSoftDeleteFilter)
        {
            // 全局软删过滤:所有 Select/Update 自动排除已删记录——忘记加条件的越权读取从此不可能
            fsql.GlobalFilter.Apply<ISoftDelete>("nexmesh-soft-delete", e => !e.IsDeleted);
        }
        if (options.EnableTenantFilter)
        {
            // SSS+安全:全局租户过滤——ITenantEntity 实体的查询/更新自动限定 ICurrentUser.TenantId;
            // 写入路径由仓储 FillCreate 自动填充 TenantId。一处遗漏即跨院区泄露的风险由此封死。
            fsql.GlobalFilter.Apply<ITenantEntity>("nexmesh-tenant", e => e.TenantId == TenantAccessor.Current);
        }

        services.TryAddSingleton<IDbFactory, DbFactory>();
        services.Add(new ServiceDescriptor(typeof(IFreeSql),
            sp => fsql, ServiceLifetime.Singleton)); // 默认(未命名)注入 = 最后注册的库
        services.AddKeyedSingleton(typeof(IFreeSql), name, (sp, key) => fsql);
        services.TryAddScoped(typeof(IRepository<>), typeof(Repository<>));
        return services;
    }

    internal sealed class DbFactory(IServiceProvider sp) : IDbFactory
    {
        public IFreeSql Get(string name = "main") =>
            sp.GetRequiredKeyedService<IFreeSql>(name);
    }
}
