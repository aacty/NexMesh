namespace NexMesh.Data;

using FreeSql;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.Core;

/// <summary>数据源模式。</summary>
public enum FreeSqlDBMode
{
    Single,
    MasterSlave,
}

/// <summary>数据源配置(FreeSql 连接参数;与 FreeSqlBuilder 一一对应)。</summary>
public sealed class FreeSqlDBOptions
{
    public DataType DataType { get; set; } = DataType.MySql;

    public string ConnectionString { get; set; } = "";

    public FreeSqlDBMode Mode { get; set; } = FreeSqlDBMode.Single;

    public string[] SlaveConnectionString { get; set; } = [];

    public bool AutoSyncStructure { get; set; }

    public bool LazyLoading { get; set; }

    public bool EnableHealthCheck { get; set; } = true;

    public bool EnableSqlTracing { get; set; }

    /// <summary>启动期配置校验:连接串缺失立即 fail-fast(避免运行期空连接)。</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException("[NexMesh Data] FreeSqlDBOptions.ConnectionString 未配置");
        }
    }
}

/// <summary>类型化数据源契约:以标记类型隔离多库,同一实体可挂不同库。</summary>
public interface IFreeSqlDBSource<TFreeSqlDBSource> where TFreeSqlDBSource : class
{
    IFreeSql<TFreeSqlDBSource> SqlClient { get; }
}

/// <summary>类型化数据源基类:构造注入 IFreeSql&lt;T&gt;(FreeSql 原生多库标记)。</summary>
public abstract class FreeSqlDBSourceBase<TFreeSqlDBSource>(IFreeSql<TFreeSqlDBSource> sqlClient)
    : IFreeSqlDBSource<TFreeSqlDBSource> where TFreeSqlDBSource : class
{
    public IFreeSql<TFreeSqlDBSource> SqlClient { get; } = sqlClient;
}

/// <summary>仓储标记接口:实现类经 AddNexMeshRepositories 自动 Scoped 注册。</summary>
public interface IRepository
{
}

/// <summary>
/// 类型化数据源注册:FreeSqlBuilder 构建(可定制)→ Build&lt;T&gt; 生成 IFreeSql&lt;T&gt; 标记实例 →
/// 注册 IFreeSql&lt;T&gt;/IFreeSql/TFreeSqlDBSource 单例。多库时多次调用,标记类型互不干扰。
/// </summary>
public static class FreeSqlDBSourceExtensions
{
    public static NexMeshBuilder AddDBSource<TFreeSqlDBSource>(
        this NexMeshBuilder builder,
        FreeSqlDBOptions options,
        Action<FreeSqlBuilder>? freeSqlBuilderAction = null,
        Action<IFreeSql<TFreeSqlDBSource>>? freeSqlAction = null,
        string? healthCheckName = null) where TFreeSqlDBSource : class
    {
        options.Validate();
        var fsb = new FreeSqlBuilder().UseConnectionString(options.DataType, options.ConnectionString);
        if (options.AutoSyncStructure)
        {
            fsb = fsb.UseAutoSyncStructure(true);
        }
        if (options.LazyLoading)
        {
            fsb = fsb.UseLazyLoading(true);
        }
        if (options.Mode == FreeSqlDBMode.MasterSlave && options.SlaveConnectionString.Length > 0)
        {
            fsb = fsb.UseSlave(options.SlaveConnectionString);
        }
        if (options.EnableSqlTracing)
        {
            fsb = fsb.UseMonitorCommand(
                cmd => Console.WriteLine($"[Sql] {cmd.CommandText}"),
                (cmd, log) => Console.WriteLine($"[SqlTrace] {log}"));
        }
        freeSqlBuilderAction?.Invoke(fsb);

        var fsql = fsb.Build<TFreeSqlDBSource>();
        freeSqlAction?.Invoke(fsql);

        builder.Services.AddSingleton(typeof(IFreeSql<TFreeSqlDBSource>), fsql);
        // 非泛型 IFreeSql 兜底注入(多源时以最后注册者为准,与 AddNexMeshDb 语义一致)
        builder.Services.AddSingleton(typeof(IFreeSql), fsql);
        builder.Services.AddSingleton<TFreeSqlDBSource>();
        if (!string.IsNullOrWhiteSpace(healthCheckName))
        {
            builder.Services.AddHealthChecks().AddCheck(healthCheckName, new FreeSqlHealthCheck(fsql));
        }
        return builder;
    }

    private sealed class FreeSqlHealthCheck(IFreeSql fsql) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
    {
        public Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
            Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return Task.FromResult(fsql.Ado.ExecuteConnectTest(5)
                    ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy()
                    : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("连接测试失败"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy(ex.Message));
            }
        }
    }
}

/// <summary>仓储自动注册:扫描实现 IRepository 的公开类,Scoped 注册为自身类型。</summary>
public static class NexMeshRepositoryExtensions
{
    public static IServiceCollection AddNexMeshRepositories(this IServiceCollection services, params System.Reflection.Assembly[] assemblies)
    {
        foreach (var asm in assemblies)
        {
            foreach (var type in asm.GetTypes())
            {
                if (type is { IsClass: true, IsAbstract: false }
                    && typeof(IRepository).IsAssignableFrom(type))
                {
                    services.AddScoped(type);
                }
            }
        }
        return services;
    }
}

/// <summary>ID 生成器(雪花长整型/字符串;其他形态经由 NextString 扩展)。</summary>
public interface IIdGenerator
{
    long NextId();

    long NextLong();

    string NextString();

    long NextSnowflakeId();

    string NextSnowflakeString();
}

/// <summary>默认实现:全部形态委托 NexMeshSnowflake(41+10+12 位,WorkerId 经 AddNexMeshDb 配置)。</summary>
public sealed class NexMeshIdGenerator : IIdGenerator
{
    public long NextId() => NexMeshSnowflake.NextId();

    public long NextLong() => NexMeshSnowflake.NextId();

    public string NextString() => NextSnowflakeString();

    public long NextSnowflakeId() => NexMeshSnowflake.NextId();

    public string NextSnowflakeString() => NexMeshSnowflake.NextId().ToString();
}
