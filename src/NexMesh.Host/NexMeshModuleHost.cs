namespace NexMesh.Host;

using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.Auth;
using NexMesh.Core;
using NexMesh.Core.Context;
using NexMesh.Core.DependencyInjection;
using NexMesh.Data;

/// <summary>
/// 模块式宿主入口(AddNexMeshHost/UseNexMeshHost):一行装配——
/// 配置节点绑定(NexMesh:IP/HttpPort)、Core 总装、约定注入、契约实现注册、
/// 模块扫描(业务 AppModule 自动发现),并按阶段执行 ConfigureServices/ConfigureApplication。
/// 认证经 AddSecurity(SecurityBuilder) 装配;Kestrel 按 NexMesh:HttpPort 绑定。
/// </summary>
public static class NexMeshModuleHostExtensions
{
    public static WebApplicationBuilder AddNexMeshHost(this WebApplicationBuilder builder, Action<NexMeshBuilder>? configure = null)
        => AddNexMeshHost(builder, null, configure);

    /// <summary>测试/多程序集宿主:显式传入扫描根程序集(缺省为入口程序集)。</summary>
    public static WebApplicationBuilder AddNexMeshHost(
        this WebApplicationBuilder builder, Assembly[]? assemblies, Action<NexMeshBuilder>? configure = null)
    {
        var mb = new NexMeshBuilder(builder.Services, builder.Configuration);
        builder.Configuration.GetSection("NexMesh").Bind(mb.Options);

        builder.Services.AddNexMesh(builder.Configuration);
        var scanned = ModuleHostAssemblyScanner.Closure(assemblies ?? [Assembly.GetEntryAssembly()!]);
        var hostSet = new HostAssemblySet(scanned);
        builder.Services.AddSingleton(hostSet);
        builder.Services.AddNexMeshConventionServices(scanned);
        builder.Services.AddNexMeshContracts(scanned);
        builder.Services.AddNexMeshRepositories(scanned); // IRepository 标记仓储自动 Scoped 注册

        if (mb.Options.AutoScanModules)
        {
            mb.AutoScanModules(scanned);
        }

        configure?.Invoke(mb);

        foreach (var module in mb.Modules
                     .OrderBy(m => m.Phase)
                     .ThenBy(m => m.Priority)
                     .ThenBy(m => m.Name, StringComparer.Ordinal))
        {
            module.ConfigureServices(mb);
        }

        // 模块清单留给 UseNexMeshHost 执行 ConfigureApplication
        hostSet.Modules.AddRange(mb.Modules);

        // Kestrel 绑定:NexMesh:HttpPort 显式配置时生效(0.0.0.0 全接口监听)
        if (mb.Options.HttpPort > 0)
        {
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(mb.Options.HttpPort);
            });
        }
        return builder;
    }

    public static WebApplication UseNexMeshHost(this WebApplication app)
    {
        var hostSet = app.Services.GetRequiredService<HostAssemblySet>();

        // 模块 ConfigureApplication(按阶段)先行——业务初始化(建表/种子/审计 AOP)就位于此
        foreach (var module in hostSet.Modules
                     .OrderBy(m => m.Phase)
                     .ThenBy(m => m.Priority)
                     .ThenBy(m => m.Name, StringComparer.Ordinal))
        {
            module.ConfigureApplication(app);
        }

        app.UseNexMeshContext();
        app.UseNexMeshExceptionHandling();
        app.UseNexMeshAuthentication();
        app.UseAuthorization();
        NexMeshHostExtensions.UseNexMeshRateLimiting(app); // 注册过 AddNexMeshRateLimiter 才挂(未注册零开销)
        app.MapNexMeshContracts(hostSet.Assemblies);
        app.MapNexMeshHealth();
        app.MapNexMeshDocs();
        return app;
    }
}
