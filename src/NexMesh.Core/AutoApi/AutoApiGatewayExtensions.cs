namespace NexMesh.Core.AutoApi;

using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// 虚拟分发网关(10M+ 逻辑路由模式):物理层面只注册一条通配路由(路由优先级天然低于一切字面量端点,
/// 与 MapNexMeshContracts 可共存),全部逻辑路由托管于内存 LogicalRouteTable——千万级逻辑路由只是字典条目,
/// ASP.NET Core 路由树(DFA)大小与逻辑路由数完全解耦。执行路径与字面量模式共用 AutoApiDispatcher,行为完全一致。
/// </summary>
public static class AutoApiGatewayExtensions
{
    private static readonly string[] SupportedVerbs = ["GET", "POST", "PUT", "DELETE", "PATCH"];

    public static IEndpointRouteBuilder MapNexMeshContractGateway(this IEndpointRouteBuilder endpoints, params Assembly[] assemblies)
    {
        assemblies = assemblies.Length > 0 ? assemblies : [Assembly.GetEntryAssembly()!];

        // 网关模式与字面量模式同授权语义(默认拒绝)——读取同一配置
        var config = endpoints.ServiceProvider.GetService<IConfiguration>();
        var allowByDefault = config?.GetValue<bool>("NexMesh:Contracts:AllowAnonymousByDefault", false) ?? false;

        var table = new LogicalRouteTable();
        foreach (var endpoint in ContractApiExtensions.ScanContractEndpoints(assemblies))
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
                // 授权判定(与字面量模式同规则):
                // [AllowAnonymous] 放行;[Authorize(Roles)] 校验角色;否则默认拒绝或显式 [Authorize] 时要求已认证
                if (!route.Endpoint.AllowAnonymous
                    && (route.Endpoint.ExplicitAuthorize || route.Endpoint.Roles is not null || !allowByDefault)
                    && context.User.Identity?.IsAuthenticated != true)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    await WriteErrorAsync(context.Response, "该操作需要登录(网关默认拒绝)", 401);
                    return;
                }
                if (route.Endpoint.Roles is { Length: > 0 }
                    && context.User.Identity?.IsAuthenticated == true
                    && route.Endpoint.Roles.All(r => !context.User.IsInRole(r)))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json";
                    await WriteErrorAsync(context.Response, "权限不足(网关角色校验)", 403);
                    return;
                }
                await AutoApiDispatcher.InvokeAsync(context, route.Endpoint, routeValues);
                return;
            }

            context.Response.ContentType = "application/json";
            if (table.ContainsPath(path, verb))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                await WriteErrorAsync(context.Response, $"逻辑路由 '{path}' 不支持 {verb} 动词(虚拟网关)", 405);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await WriteErrorAsync(context.Response, $"逻辑路由 '{path}' 未注册(虚拟网关)", 404);
        });

        return endpoints;
    }

    /// <summary>错误体统一 Web 序列化选项(camelCase),与端点信封一致。</summary>
    private static Task WriteErrorAsync(HttpResponse response, string message, int code)
        => response.WriteAsync(JsonSerializer.Serialize(ApiResult.Fail(message, code), JsonSerializerOptions.Web));
}
