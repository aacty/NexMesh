namespace NexMesh.Data.Migrations;

using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>NexMesh.Data.Migrations 注册入口。</summary>
public static class NexMeshMigrationsServiceCollectionExtensions
{
    /// <summary>
    /// 注册迁移链(单实例执行语义:多实例同时启动时仅一个实例能成功应用迁移,详见 NexMeshMigrationRunner 注释):扫描程序集中 NexMeshMigration 子类(未注册则按 NexMeshMigration 服务类型注册,支持 IEnumerable 解析);
    /// 配置节 NexMesh:Migrations { Enabled }:true 时挂 HostedService 启动自动执行(fail-fast),
    /// false(默认)时业务自行选择时机显式调用 app.MigrateNexMeshAsync()(适合与模块种子精确编排顺序)。
    /// </summary>
    public static IServiceCollection AddNexMeshMigrations(
        this IServiceCollection services,
        IConfiguration configuration,
        params Assembly[] migrationAssemblies)
    {
        var options = new NexMeshMigrationsOptions();
        configuration.GetSection(NexMeshMigrationsOptions.SectionName).Bind(options);
        services.AddOptions<NexMeshMigrationsOptions>().Configure(o => o.Enabled = options.Enabled);

        // 扫描注册:迁移类注册为 NexMeshMigration 服务类型(IEnumerable<NexMeshMigration> 可解析,幂等)
        var assemblies = migrationAssemblies.Length > 0
            ? migrationAssemblies
            : [Assembly.GetEntryAssembly() ?? typeof(NexMeshMigrationsServiceCollectionExtensions).Assembly];
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type is { IsClass: true, IsAbstract: false }
                    && typeof(NexMeshMigration).IsAssignableFrom(type)
                    && !services.Any(d => d.ServiceType == typeof(NexMeshMigration) && d.ImplementationType == type))
                {
                    services.AddTransient(typeof(NexMeshMigration), type);
                }
            }
        }

        services.AddSingleton<IMigrationRunner, NexMeshMigrationRunner>();
        if (options.Enabled)
        {
            services.AddHostedService<MigrationHostedService>();
        }
        return services;
    }

    /// <summary>显式注册单个迁移(不扫描程序集时使用;测试与精细控制场景)。</summary>
    public static IServiceCollection AddNexMeshMigration<TMigration>(this IServiceCollection services)
        where TMigration : NexMeshMigration
    {
        if (!services.Any(d => d.ServiceType == typeof(NexMeshMigration) && d.ImplementationType == typeof(TMigration)))
        {
            services.AddTransient(typeof(NexMeshMigration), typeof(TMigration));
        }
        return services;
    }
}

/// <summary>启动期自动执行迁移;任何失败抛出(默认策略 StopHost,启动失败即部署失败)。</summary>
internal sealed class MigrationHostedService(
    IMigrationRunner runner,
    ILogger<MigrationHostedService>? logger = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger?.LogInformation("[NexMesh.Migrations] Enabled=true,启动期执行待应用迁移");
        await runner.RunPendingAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>WebApplication 显式迁移扩展:在 Build() 与 UseNexMeshHost() 之间调用,先于模块种子执行。</summary>
public static class NexMeshMigrationAppExtensions
{
    /// <summary>显式应用全部待迁移(需已 AddNexMeshMigrations);失败抛出阻止后续启动。</summary>
    public static async Task MigrateNexMeshAsync(this WebApplication app, CancellationToken cancellationToken = default)
        => await app.Services.GetRequiredService<IMigrationRunner>().RunPendingAsync(cancellationToken);
}
