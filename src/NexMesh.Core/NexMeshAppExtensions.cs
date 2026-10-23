namespace NexMesh.Core;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.Core.Context;
using NexMesh.Core.Middlewares;
using Scalar.AspNetCore;

/// <summary>NexMesh.Core 总装入口。</summary>
public static class NexMeshAppExtensions
{
    /// <summary>
    /// 总装注册:IHttpContextAccessor / ICurrentUser / 健康检查 / CORS 配置化 / 请求体限制配置化。
    /// SSS+安全:CORS 未显式配置 Origins 时默认拒绝跨域(不产生 Access-Control-Allow-Origin),
    /// 彻底移除 fail-open 的 AllowAnyOrigin 默认;表单上限可配置(默认 100MB,非无界)。
    /// </summary>
    public static IServiceCollection AddNexMesh(this IServiceCollection services, IConfiguration? configuration = null)
    {
        services.AddLogging(); // 宿主自带;独立容器(测试/工具)获得 NullLogger 兜底
        services.AddMvcCore(); // IActionResult 执行器(FileStreamResult 等),仅注册基础设施不启用 MVC 管道
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddSingleton<INexMeshContextAccessor, AsyncLocalNexMeshContextAccessor>();
        services.AddSingleton<INexMeshBackgroundRunner, NexMeshBackgroundRunner>();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped(typeof(ICurrentUser<>), typeof(CurrentUser<>)); // 强类型业务用户模型(开放泛型)
        services.AddHealthChecks();

        // B4:multipart 上限可配置(NexMesh:Upload:MaxSizeBytes),默认 100MB——不再放开到无界
        var maxUpload = configuration?.GetValue<long?>("NexMesh:Upload:MaxSizeBytes") ?? 100L * 1024 * 1024;
        services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxUpload);

        // B9:CORS 默认拒绝——仅在显式配置 Origins 时放行对应来源
        var origins = configuration?.GetSection("NexMesh:Cors:Origins").Get<string[]>();
        services.AddCors(o => o.AddPolicy("nexmesh", p =>
        {
            if (origins is { Length: > 0 })
            {
                p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
            }
            // 未配置 → 无 Allow-* 规则 → 浏览器跨域请求被拒(fail-closed)
        }));
        return services;
    }

    /// <summary>全局异常中间件(X-Trace-Id + MeshException 业务码透传)。</summary>
    public static IApplicationBuilder UseNexMeshExceptionHandling(this IApplicationBuilder app)
        => app.UseMiddleware<MeshExceptionMiddleware>();

    /// <summary>健康检查端点(/health)。</summary>
    public static IEndpointRouteBuilder MapNexMeshHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health");
        return endpoints;
    }

    /// <summary>
    /// OpenAPI 端点 + Scalar 调试 UI(/scalar/v1)。
    /// SSS+安全(B6):默认仅开发环境暴露;生产需显式 MapNexMeshDocs(force: true)。
    /// </summary>
    public static IEndpointRouteBuilder MapNexMeshDocs(this IEndpointRouteBuilder endpoints, bool force = false)
    {
        var env = endpoints.ServiceProvider.GetService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        var isDev = string.Equals(env?.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);
        if (!force && !isDev)
        {
            return endpoints; // 生产默认不暴露文档
        }
        endpoints.MapOpenApi();
        endpoints.MapScalarApiReference();
        return endpoints;
    }
}
