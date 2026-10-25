namespace NexMesh;

using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh.Core.AutoApi;

/// <summary>
/// 接口契约端点引擎:扫描 IServiceApi 契约([ApiContract] 可选,用于自定义服务名),
/// 实现类自动注册 DI(Scoped),契约方法映射为 REST 端点——
/// 路由 api/{服务名}/{[Http*]模板};无模板用方法名去 Async 后缀;
/// 特性只读 .NET 原生 [HttpGet]/[HttpPost]/[Authorize]/[AllowAnonymous]/[FromBody]/[FromQuery];
/// 响应统一 ApiResult 信封(Success/Code=0/Message/Data/TraceId);默认要求认证(默认拒绝)。
/// </summary>
public static class ContractApiExtensions
{
    /// <summary>注册契约实现:扫描 IServiceApi 契约接口的公开实现类,Scoped 注册为契约接口。</summary>
    public static IServiceCollection AddNexMeshContracts(this IServiceCollection services, params Assembly[] assemblies)
    {
        foreach (var (iface, impl) in ScanContractImplementations(assemblies))
        {
            services.AddScoped(iface, impl);
        }
        return services;
    }

    /// <summary>
    /// 映射契约端点。授权语义(SSS+ 默认拒绝):显式 [AllowAnonymous] 放行;
    /// [Authorize]/[Authorize(Roles)] 要求认证/角色;未标注的端点一律要求认证。
    /// 配置 NexMesh:Contracts:AllowAnonymousByDefault=true 可回退默认放行。
    /// </summary>
    public static IEndpointRouteBuilder MapNexMeshContracts(this IEndpointRouteBuilder endpoints, params Assembly[] assemblies)
    {
        var config = endpoints.ServiceProvider.GetService<IConfiguration>();
        var allowByDefault = config?.GetValue<bool>("NexMesh:Contracts:AllowAnonymousByDefault", false) ?? false;

        var scanned = ScanContractEndpoints(assemblies);
        DetectRouteConflicts(scanned);

        foreach (var endpoint in scanned)
        {
            var builder = endpoints.MapMethods(endpoint.Route, [endpoint.Verb],
                context => AutoApiDispatcher.InvokeAsync(context, endpoint));

            if (endpoint.AllowAnonymous)
            {
                builder.AllowAnonymous();
            }
            else if (endpoint.Roles is { Length: > 0 })
            {
                // 角色以 IAuthorizeData 形式落元数据(策略由授权管线构建;OpenAPI/内省可读)
                builder.RequireAuthorization(new AuthorizeAttribute { Roles = string.Join(",", endpoint.Roles) });
            }
            else if (endpoint.ExplicitAuthorize || !allowByDefault)
            {
                builder.RequireAuthorization();
            }

            if (endpoint.RateLimitPolicy is not null)
            {
                builder.RequireRateLimiting(endpoint.RateLimitPolicy);
            }

            if (endpoint.Tags is not null)
            {
                builder.WithTags(endpoint.Tags);
            }
        }
        return endpoints;
    }

    /// <summary>同一 (Verb, Route) 只允许一个端点:重复即启动期 fail-fast,避免路由歧义静默覆盖。</summary>
    private static void DetectRouteConflicts(List<AutoApiEndpoint> scanned)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in scanned)
        {
            if (!seen.Add($"{endpoint.Verb}|{endpoint.Route}"))
            {
                throw new NotSupportedException(
                    $"[NexMesh Contracts] 路由冲突:{endpoint.Verb} {endpoint.Route} 被多个契约方法声明" +
                    $"({endpoint.ServiceType.Name}.{endpoint.Method.Name});请检查实现类或用 [ApiContract(name)] 区分服务名。");
            }
        }
    }

    /// <summary>契约实现扫描:IServiceApi 契约接口 × 公开实现类(去重:一接口多实现时每个实现都注册,端点由路由冲突检测兜底)。</summary>
    internal static IEnumerable<(Type Iface, Type Impl)> ScanContractImplementations(Assembly[] assemblies)
    {
        assemblies = assemblies.Length > 0 ? assemblies : [Assembly.GetEntryAssembly()!];

        var contracts = new List<Type>();
        var impls = new List<List<Type>>();
        foreach (var asm in assemblies)
        {
            foreach (var type in asm.GetTypes())
            {
                if (type is { IsInterface: true, IsGenericType: false }
                    && type != typeof(IServiceApi)
                    && typeof(IServiceApi).IsAssignableFrom(type))
                {
                    contracts.Add(type);
                }
            }
        }

        foreach (var contract in contracts)
        {
            var found = new List<Type>();
            foreach (var asm in assemblies)
            {
                foreach (var type in asm.GetTypes())
                {
                    if (type is { IsClass: true, IsAbstract: false } && contract.IsAssignableFrom(type))
                    {
                        found.Add(type);
                    }
                }
            }
            if (found.Count > 0)
            {
                impls.Add(found);
            }
        }

        for (var i = 0; i < contracts.Count; i++)
        {
            foreach (var impl in impls[i])
            {
                yield return (contracts[i], impl);
            }
        }
    }

    /// <summary>契约端点扫描:方法级 [Http*] 模板 → api/{服务名}/{模板};无模板/裸特性 → api/{服务名}/{方法名去Async}。</summary>
    internal static List<AutoApiEndpoint> ScanContractEndpoints(Assembly[] assemblies)
    {
        var result = new List<AutoApiEndpoint>();
        foreach (var (iface, impl) in ScanContractImplementations(assemblies))
        {
            var contractName = iface.GetCustomAttribute<ApiContractAttribute>()?.Name;
            var serviceName = contractName ?? DeriveServiceName(iface.Name);
            var prefix = $"api/{serviceName}";

            foreach (var method in iface.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName)
                {
                    continue;
                }
                result.Add(BuildContractEndpoint(iface, method, prefix));
            }
        }
        return result;
    }

    internal static AutoApiEndpoint BuildContractEndpoint(Type iface, MethodInfo method, string prefix)
    {
        var (verb, template) = ResolveHttpMethod(method);
        var actionName = method.Name.EndsWith("Async", StringComparison.Ordinal)
            ? method.Name[..^5]
            : method.Name;
        var route = string.IsNullOrEmpty(template)
            ? $"{prefix}/{actionName}"
            : $"{prefix}/{template.TrimStart('/')}";

        var bindable = method.GetParameters()
            .Where(p => p.ParameterType != typeof(CancellationToken))
            .ToArray();
        var bodyCandidates = bindable.Where(p => !AutoApiDispatcher.IsSimple(p.ParameterType)
            && p.ParameterType != typeof(Microsoft.AspNetCore.Http.IFormFile)
            && p.ParameterType != typeof(Microsoft.AspNetCore.Http.IFormFileCollection)
            && p.GetCustomAttribute<FromFormAttribute>() is null).ToArray();
        if (bodyCandidates.Length > 1)
        {
            throw new NotSupportedException(
                $"[NexMesh Contracts] 契约方法 {iface.Name}.{method.Name} 含多个 JSON Body 复杂类型参数," +
                $"至多一个 Body;其余复杂参数请用 [FromForm] 或 IFormFile。");
        }
        var bodyParam = bodyCandidates.FirstOrDefault();

        // 按声明返回类型解析结果:Task<T> 取 Result;非泛型 Task → 空载荷信封。
        // 不能按运行时类型反射(.NET 10 的 Task.CompletedTask 实际是 Task<VoidTaskResult>)。
        var declaredResult = method.ReturnType.IsGenericType
            && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? method.ReturnType.GetProperty("Result")
            : null;

        var allowAnonymous = method.GetCustomAttribute<AllowAnonymousAttribute>() is not null
            || iface.GetCustomAttribute<AllowAnonymousAttribute>() is not null;
        var authorize = method.GetCustomAttribute<AuthorizeAttribute>()
            ?? iface.GetCustomAttribute<AuthorizeAttribute>();
        // 官方 [EnableRateLimiting] 仅允许标注于方法(.NET 10 AttributeUsage 不含接口),故仅方法级识别
        var rateLimit = method.GetCustomAttribute<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>();

        // ServiceType = 契约接口(DI 按接口解析实现;接口 MethodInfo 可直接在实现实例上 Invoke)
        return new AutoApiEndpoint(iface, method, verb, route, bodyParam, declaredResult)
        {
            AllowAnonymous = allowAnonymous,
            ExplicitAuthorize = authorize is not null,
            Roles = authorize?.Roles?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            RateLimitPolicy = rateLimit?.PolicyName,
            Tags = iface.Name,
        };
    }

    /// <summary>服务名推导:ISysUserApi → SysUser;IAuthApi → Auth;非 I 前缀接口名原样去尾部 Api。</summary>
    internal static string DeriveServiceName(string interfaceName)
    {
        var name = interfaceName.Length > 1 && interfaceName[0] == 'I' && char.IsUpper(interfaceName, 1)
            ? interfaceName[1..]
            : interfaceName;
        if (name.EndsWith("Api", StringComparison.Ordinal))
        {
            name = name[..^3];
        }
        return name;
    }

    private static (string Verb, string? Template) ResolveHttpMethod(MethodInfo method)
    {
        var attr = method.GetCustomAttribute<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>();
        if (attr is HttpGetAttribute get) return ("GET", get.Template);
        if (attr is HttpPostAttribute post) return ("POST", post.Template);
        if (attr is HttpPutAttribute put) return ("PUT", put.Template);
        if (attr is HttpDeleteAttribute del) return ("DELETE", del.Template);
        if (attr is HttpPatchAttribute patch) return ("PATCH", patch.Template);
        return ("POST", null);
    }
}
