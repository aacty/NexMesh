namespace NexMesh.Host;

using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        // 重复装配 fail-fast:约定服务/契约/模块会全量双份注册,模块 ConfigureServices 双执行
        if (builder.Services.Any(d => d.ServiceType == typeof(HostAssemblySet)))
        {
            throw new InvalidOperationException(
                "AddNexMeshHost 重复调用:模块宿主只允许装配一次(请检查是否在多个启动路径重复装配)。");
        }

        var mb = new NexMeshBuilder(builder.Services, builder.Configuration);
        builder.Configuration.GetSection("NexMesh").Bind(mb.Options);

        builder.Services.AddNexMesh(builder.Configuration);
        builder.Services.TryAddSingleton<Dashboard.DashboardStartupClock>(); // Dashboard 运行时长时钟
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

        // 零 Security 装配的模块宿主兜底:UseAuthorization 需要授权基础服务,
        // 模块/回调均未装 AddSecurity 时在此补齐(已注册则零操作),避免启动期晦涩报错
        if (!builder.Services.Any(d => d.ServiceType == typeof(IAuthorizationService)))
        {
            builder.Services.AddAuthorization();
        }

        // Kestrel 绑定:NexMesh:HttpPort 显式配置时生效(0.0.0.0 全接口监听);
        // GrpcPort>0 且装配了 gRPC 面时追加 Http2 监听
        if (mb.Options.HttpPort > 0 && mb.Options.GrpcPort == mb.Options.HttpPort)
        {
            throw new InvalidOperationException(
                $"NexMesh:HttpPort 与 NexMesh:GrpcPort 不可同值({mb.Options.HttpPort}):Kestrel 需要两个不同的监听端口,同值会在启动时地址冲突。");
        }

        if (mb.Options.HttpPort > 0 || mb.Options.GrpcPort > 0)
        {
            var grpcEnabled = builder.Services.Any(d => d.ServiceType == typeof(NexMeshGrpcExtensions.ContractGrpcRegistry));
            builder.WebHost.ConfigureKestrel(options =>
            {
                if (mb.Options.HttpPort > 0)
                {
                    options.ListenAnyIP(mb.Options.HttpPort);
                }
                if (mb.Options.GrpcPort > 0 && grpcEnabled)
                {
                    options.ListenAnyIP(mb.Options.GrpcPort, listen => listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
                }
            });
        }
        return builder;
    }

    // UseNexMeshHost 幂等防护(按 hostSet 实例记录,不改 HostAssemblySet 公共形态)
    private static readonly ConcurrentDictionary<HostAssemblySet, byte> AppliedHostSets = new();

    public static WebApplication UseNexMeshHost(this WebApplication app)
    {
        var hostSet = app.Services.GetRequiredService<HostAssemblySet>();

        // 二次执行会重复跑模块 ConfigureApplication(建表/种子/审计等业务初始化)
        if (!AppliedHostSets.TryAdd(hostSet, 0))
        {
            throw new InvalidOperationException("UseNexMeshHost 重复调用:模块 ConfigureApplication 只允许执行一次。");
        }

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
        if (app.Services.GetService<NexMeshGrpcExtensions.ContractGrpcRegistry>() is not null)
        {
            app.MapNexMeshGrpc(); // 契约 gRPC 面(AddNexMeshGrpc 注册过才挂)
        }
        app.MapNexMeshHealth();
        app.MapNexMeshDocs();
        return app;
    }
}
