namespace NexMesh.Core.AutoApi;

using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;

/// <summary>标记接口:实现类(经约定注入或手工注册后)由 MapAutoApis 自动映射为 REST 端点。</summary>
public interface IAutoApi { }

/// <summary>声明 AutoApi 基础路由(如 "api/employee");缺省为 api/{类名小写}。</summary>
[AttributeUsage(AttributeTargets.Class)]
public class ServiceRouteAttribute(string route) : Attribute
{
    public string Route { get; } = route;
}

/// <summary>表单声明:复杂参数从 multipart/urlencoded 表单字段绑定(简单属性同名映射,IFormFile 属性按名匹配文件)。</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromFormAttribute : Attribute
{
}

/// <summary>
/// AutoApi(字面量端点模式,≤万级端点首选):扫描 IAutoApi 实现类,方法映射为 REST 端点。
/// 动词与路由:方法标注 [HttpGet("{id}")] 等标准特性 → 按特性;无特性 → 默认 POST /{方法名小写}。
/// 参数绑定(有界):简单类型从路由值或 Query 绑定;单个复杂类型从 JSON Body 绑定;
/// CancellationToken 注入 RequestAborted;多复杂参数映射期 fail-fast。
/// </summary>
public static class AutoApiExtensions
{
    public static IEndpointRouteBuilder MapAutoApis(this IEndpointRouteBuilder endpoints, params Assembly[] assemblies)
    {
        // SSS+安全:默认拒绝——未标注 [Authorize] 或 [AllowAnonymous] 的端点一律要求认证;
        // 显式 [AllowAnonymous] 才匿名(docs/standards/08"默认拒绝"条款;配置 NexMesh:AutoApi:AllowAnonymousByDefault=true 可回退旧行为)
        var config = endpoints.ServiceProvider.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
        var allowByDefault = config?.GetValue<bool>("NexMesh:AutoApi:AllowAnonymousByDefault", false) ?? false;
        return MapAutoApisInternal(endpoints, allowByDefault, assemblies);
    }

    private static IEndpointRouteBuilder MapAutoApisInternal(
        this IEndpointRouteBuilder endpoints, bool defaultAllowAnonymous, params Assembly[] assemblies)
    {
        assemblies = assemblies.Length > 0 ? assemblies : [Assembly.GetEntryAssembly()!];

        foreach (var endpoint in ScanEndpoints(assemblies))
        {
            var builder = endpoints.MapMethods(endpoint.Route, [endpoint.Verb], context =>
                AutoApiDispatcher.InvokeAsync(context, endpoint));
            ApplyAuthorization(builder, endpoint, defaultAllowAnonymous);
        }
        return endpoints;
    }

    /// <summary>授权标注转译:显式 [AllowAnonymous] 优先放行;[Authorize] 要求认证;默认拒绝(SSS+安全修复)。</summary>
    private static void ApplyAuthorization(
        Microsoft.AspNetCore.Builder.IEndpointConventionBuilder builder,
        AutoApiEndpoint endpoint, bool defaultAllowAnonymous)
    {
        var allowAnonymous = HasAttribute<AllowAnonymousAttribute>(endpoint.ServiceType, endpoint.Method);
        if (allowAnonymous)
        {
            builder.AllowAnonymous();
            return;
        }
        if (HasAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>(endpoint.ServiceType, endpoint.Method)
            || !defaultAllowAnonymous)
        {
            builder.RequireAuthorization();
        }
    }

    /// <summary>类级/方法级特性查找(隐式实现不继承接口特性,沿 InterfaceMap 上溯)。</summary>
    private static bool HasAttribute<TAttr>(Type type, MethodInfo? method) where TAttr : Attribute
    {
        if (method is not null && method.GetCustomAttribute<TAttr>() is not null)
        {
            return true;
        }
        if (type.GetCustomAttribute<TAttr>() is not null)
        {
            return true;
        }
        foreach (var iface in type.GetInterfaces())
        {
            if (iface == typeof(IAutoApi) || iface.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
            {
                continue;
            }
            if (iface.GetCustomAttribute<TAttr>() is not null)
            {
                return true;
            }
            var map = type.GetInterfaceMap(iface);
            for (var i = 0; i < map.TargetMethods.Length; i++)
            {
                if (method is not null && map.TargetMethods[i] == method
                    && map.InterfaceMethods[i].GetCustomAttribute<TAttr>() is not null)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>扫描全部 IAutoApi 方法并构建执行元数据(映射期校验:多复杂参数 fail-fast)。</summary>
    internal static List<AutoApiEndpoint> ScanEndpoints(Assembly[] assemblies)
    {
        var result = new List<AutoApiEndpoint>();
        foreach (var asm in assemblies)
        {
            var apiTypes = asm.GetTypes()
                .Where(t => typeof(IAutoApi).IsAssignableFrom(t) && !t.IsAbstract && t.IsClass);
            foreach (var type in apiTypes)
            {
                var routeAttr = type.GetCustomAttribute<ServiceRouteAttribute>();
                string prefix = routeAttr?.Route ?? $"api/{type.Name.ToLowerInvariant()}";

                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => !m.IsSpecialName);
                foreach (var method in methods)
                {
                    result.Add(BuildEndpoint(type, method, prefix));
                }
            }
        }
        return result;
    }

    internal static AutoApiEndpoint BuildEndpoint(Type type, MethodInfo method, string prefix)
    {
        var (verb, template, hasAttribute) = ResolveHttpMethod(type, method);
        var route = !hasAttribute
            ? $"{prefix}/{method.Name.ToLowerInvariant()}"   // 无特性:默认 POST + 方法名子路由
            : string.IsNullOrEmpty(template)
                ? prefix                                     // 裸特性(如 [HttpPost]):动词打到基础路由
                : $"{prefix}/{template.TrimStart('/')}";

        var bindable = method.GetParameters()
            .Where(p => p.ParameterType != typeof(CancellationToken))
            .ToArray();

        // 启动期校验(fail-fast):非表单/文件的复杂参数(Body 候选)至多一个;
        // IFormFile/IFormFileCollection/[FromForm] 复杂参数不受限,可与 Body 混搭
        var bodyCandidates = bindable.Where(p => !AutoApiDispatcher.IsSimple(p.ParameterType)
            && p.ParameterType != typeof(Microsoft.AspNetCore.Http.IFormFile)
            && p.ParameterType != typeof(Microsoft.AspNetCore.Http.IFormFileCollection)
            && p.GetCustomAttribute<FromFormAttribute>() is null).ToArray();
        if (bodyCandidates.Length > 1)
        {
            throw new NotSupportedException(
                $"[NexMesh AutoApi] 方法 {type.Name}.{method.Name} 含多个 JSON Body 复杂类型参数," +
                $"至多一个 Body;其余复杂参数请用 [FromForm] 或 IFormFile。");
        }
        var bodyParam = bodyCandidates.FirstOrDefault();

        // 按声明返回类型解析结果:Task<T> 取 Result;非泛型 Task → 204。
        // 不能按运行时类型反射(.NET 10 的 Task.CompletedTask 实际是 Task<VoidTaskResult>,
        // 会把内部占位对象当业务 DTO 序列化)。
        var declaredResult = method.ReturnType.IsGenericType
            && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? method.ReturnType.GetProperty("Result")
            : null;

        return new AutoApiEndpoint(type, method, verb, route, bodyParam, declaredResult)
        {
            AllowAnonymous = HasAttribute<AllowAnonymousAttribute>(type, method),
            ExplicitAuthorize = HasAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>(type, method),
        };
    }

    private static (string Verb, string? Template, bool HasAttribute) ResolveHttpMethod(Type type, MethodInfo method)
    {
        var attr = FindHttpAttribute(type, method);
        if (attr is HttpGetAttribute get) return ("GET", get.Template, true);
        if (attr is HttpPostAttribute post) return ("POST", post.Template, true);
        if (attr is HttpPutAttribute put) return ("PUT", put.Template, true);
        if (attr is HttpDeleteAttribute del) return ("DELETE", del.Template, true);
        if (attr is HttpPatchAttribute patch) return ("PATCH", patch.Template, true);
        return ("POST", null, false);
    }

    /// <summary>
    /// 特性查找:方法自身 → 接口声明(隐式实现不会继承接口方法上的特性,
    /// 业务契约通常把 [Http*] 标注在接口上,必须沿 InterfaceMap 上溯)。
    /// </summary>
    private static Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute? FindHttpAttribute(Type type, MethodInfo method)
    {
        if (method.GetCustomAttribute<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>() is { } direct)
        {
            return direct;
        }

        foreach (var iface in type.GetInterfaces())
        {
            if (iface == typeof(IAutoApi) || iface.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
            {
                continue;
            }
            var map = type.GetInterfaceMap(iface);
            for (var i = 0; i < map.TargetMethods.Length; i++)
            {
                if (map.TargetMethods[i] == method
                    && map.InterfaceMethods[i].GetCustomAttribute<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>() is { } inherited)
                {
                    return inherited;
                }
            }
        }
        return null;
    }
}
