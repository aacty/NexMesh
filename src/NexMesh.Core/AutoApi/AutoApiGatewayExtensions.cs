namespace NexMesh.Core.AutoApi;

using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.Core.Models;

/// <summary>
/// 虚拟分发网关(10M+ 逻辑路由模式):物理层面只注册一条通配路由(路由优先级天然低于一切字面量端点,
/// 与 MapAutoApis 可共存),全部逻辑路由托管于内存 LogicalRouteTable——千万级逻辑路由只是字典条目,
/// ASP.NET Core 路由树(DFA)大小与逻辑路由数完全解耦。执行路径与字面量模式共用 AutoApiDispatcher,行为一致。
/// </summary>
public static class AutoApiGatewayExtensions
{
    private static readonly string[] SupportedVerbs = ["GET", "POST", "PUT", "DELETE", "PATCH"];

    public static IEndpointRouteBuilder MapAutoApiGateway(this IEndpointRouteBuilder endpoints, params Assembly[] assemblies)
    {
        assemblies = assemblies.Length > 0 ? assemblies : [Assembly.GetEntryAssembly()!];

        // SSS+第二轮修复:网关模式与字面量模式同授权语义(默认拒绝)——读取同一配置
        var config = endpoints.ServiceProvider.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
        var allowByDefault = config?.GetValue<bool>("NexMesh:AutoApi:AllowAnonymousByDefault", false) ?? false;

        var table = new LogicalRouteTable();
        foreach (var endpoint in AutoApiExtensions.ScanEndpoints(assemblies))
        {
            table.Register(new LogicalRoute(endpoint.Verb, endpoint.Route, endpoint));
        }

        // 通配物理路由:仅承接未被任何字面量端点匹配的请求(路由优先级保证)
        endpoints.MapMethods("/{*path}", SupportedVerbs, async context =>
        {
            var path = context.Request.Path.Value ?? "/";
            var verb = context.Request.Method;

            if (table.TryMatch(path, verb, out var route, out var routeValues))
            {
                // 授权判定(与字面量模式 ApplyAuthorization 同规则):
                // [AllowAnonymous] 放行;否则默认拒绝或显式 [Authorize] 时要求已认证
                if (!route.Endpoint.AllowAnonymous
                    && (route.Endpoint.ExplicitAuthorize || !allowByDefault)
                    && context.User.Identity?.IsAuthenticated != true)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(
                        ApiResult<object>.Fail(40101, "该操作需要登录(网关默认拒绝)")));
                    return;
                }
                await AutoApiDispatcher.InvokeAsync(context, route.Endpoint, routeValues);
                return;
            }

            context.Response.ContentType = "application/json";
            if (table.ContainsPath(path, verb))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                await context.Response.WriteAsync(JsonSerializer.Serialize(
                    ApiResult<object>.Fail(40500, $"逻辑路由 '{path}' 不支持 {verb} 动词(虚拟网关)")));
                return;
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync(JsonSerializer.Serialize(
                ApiResult<object>.Fail(40400, $"逻辑路由 '{path}' 未注册(虚拟网关)")));
        });

        return endpoints;
    }
}
