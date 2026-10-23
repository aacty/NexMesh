namespace NexMesh.Host;

using System.Reflection;
using FreeSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.AI;
using NexMesh.Core;
using NexMesh.Core.AutoApi;
using NexMesh.Core.Configuration;
using NexMesh.Core.Context;
using NexMesh.Core.DependencyInjection;
using NexMesh.Cache;
using NexMesh.Data;

/// <summary>
/// 服务宿主总装:面向"业务开发者只写业务逻辑与 AI 逻辑"的铁律——
/// DI 注册、生命周期、配置装载、数据库接线、AI 管道、中间件顺序全部在此收口。
/// </summary>
public static class NexMeshHostExtensions
{
    /// <summary>
    /// 一行装配全部服务:Configs/ 约定配置 + Core(异常/健康/CORS/文档/ICurrentUser) +
    /// 约定注入(标记接口自动注册) + FreeSql(配置驱动,缺省 SQLite 零基础设施) +
    /// AI 底座(Provider 管道 + 工具安全门启动校验)。
    /// 缺省扫描入口程序集;测试宿主可显式传入业务程序集。
    /// </summary>
    public static WebApplicationBuilder AddNexMeshService(this WebApplicationBuilder builder, params Assembly[] assemblies)
    {
        assemblies = assemblies.Length > 0 ? assemblies : [Assembly.GetEntryAssembly()!];

        builder.Configuration.AddNexMeshConfig("Configs");
        var configuration = builder.Configuration;

        // Core 总装 + 约定注入
        builder.Services.AddNexMesh(configuration);
        builder.Services.AddNexMeshConventionServices(assemblies);

        // 数据层:NexMesh:Db 配置驱动(Type/ConnectionString/AutoSyncStructure/WorkerId);
        // 整节缺失 → SQLite 本地文件(不装任何数据库也能跑,开发零基础设施)。
        // 经 Data 包注册:软删全局过滤/仓储约定(审计/雪花/乐观并发)随之生效
        var dbType = Enum.TryParse<DataType>(configuration["NexMesh:Db:Type"], ignoreCase: true, out var parsedType)
            ? parsedType
            : DataType.Sqlite;
        var connectionString = configuration["NexMesh:Db:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = "Data Source=app.db";
        }
        var autoSync = bool.TryParse(configuration["NexMesh:Db:AutoSyncStructure"], out var parsedSync)
            ? parsedSync
            : string.Equals(builder.Environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);
        var workerId = int.TryParse(configuration["NexMesh:Db:WorkerId"], out var parsedWorker) ? parsedWorker : 1;

        builder.Services.AddNexMeshDb("main", o =>
        {
            o.DataType = dbType;
            o.ConnectionString = connectionString;
            o.AutoSyncStructure = autoSync;
            o.WorkerId = workerId;
        });

        // AI 底座(未配置 Provider 时静默可用,不产生任何依赖)
        builder.Services.AddNexMeshAi(configuration, assemblies);

        // 缓存:L1(Memory)+L2(Redis) 双层;未配连接串全内存兜底(零基础设施)
        builder.Services.AddNexMeshCache(configuration);

        return builder;
    }

    /// <summary>一行装配管道与端点:上下文快照 → 异常/TraceId → CORS → AutoApi → 健康检查 → OpenAPI/Scalar。</summary>
    public static WebApplication UseNexMeshService(this WebApplication app, params Assembly[] assemblies)
    {
        app.UseNexMeshContext();
        app.UseNexMeshExceptionHandling();
        app.UseCors("nexmesh");
        app.MapAutoApis(assemblies.Length > 0 ? assemblies : [Assembly.GetEntryAssembly()!]);
        app.MapNexMeshHealth();
        app.MapNexMeshDocs();
        return app;
    }
}
