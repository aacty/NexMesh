namespace NexMesh.Core.DependencyInjection;

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

/// <summary>标记接口:实现类自动注册为 Scoped 生命周期(按其全部业务接口 + 自身具体类型)。</summary>
public interface IScopedService { }

/// <summary>标记接口:实现类自动注册为 Singleton 生命周期。</summary>
public interface ISingletonService { }

/// <summary>标记接口:实现类自动注册为 Transient 生命周期。</summary>
public interface ITransientService { }

/// <summary>
/// 约定注入:扫描程序集中实现标记接口的类,按标记映射生命周期注册(接口注册转发到具体类型,同作用域共享实例)。
/// 不传参 → 扫描入口程序集。System/Microsoft 命名空间下的接口(IDisposable/IHostedService 等)
/// 与其他标记接口不参与注册;开放泛型实现类按"开放接口→开放实现"类型转发注册。
/// </summary>
public static class ConventionServiceCollectionExtensions
{
    public static IServiceCollection AddNexMeshConventionServices(this IServiceCollection services, params Assembly[] assemblies)
    {
        assemblies = assemblies.Length > 0 ? assemblies : [Assembly.GetEntryAssembly()!];

        foreach (var asm in assemblies)
        {
            var types = SafeGetTypes(asm).Where(t => !t.IsAbstract && !t.IsInterface);
            foreach (var type in types)
            {
                ServiceLifetime lifetime;
                if (typeof(IScopedService).IsAssignableFrom(type))
                    lifetime = ServiceLifetime.Scoped;
                else if (typeof(ISingletonService).IsAssignableFrom(type))
                    lifetime = ServiceLifetime.Singleton;
                else if (typeof(ITransientService).IsAssignableFrom(type))
                    lifetime = ServiceLifetime.Transient;
                else
                    continue;

                services.Add(ServiceDescriptor.Describe(type, type, lifetime));

                if (type.IsGenericTypeDefinition)
                {
                    // 开放泛型实现类(如 Repo<T> : IRepo<T>):factory 转发内部解析开放自类型必然失败
                    // (运行期炸弹),改用"开放接口定义→开放实现"类型转发,容器支持闭合解析(IRepo<int> → Repo<int>)
                    foreach (var intf in BusinessInterfaces(type))
                        services.Add(ServiceDescriptor.Describe(intf.IsGenericType ? intf.GetGenericTypeDefinition() : intf, type, lifetime));
                    continue;
                }

                foreach (var intf in BusinessInterfaces(type))
                    services.Add(ServiceDescriptor.Describe(intf, sp => sp.GetRequiredService(type), lifetime));
            }
        }
        return services;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly asm)
    {
        try
        {
            return asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // 与 ModuleHost.SafeGetTypes 同语义:闭包扫描容忍加载失败的类型
            return ex.Types.Where(t => t is not null)!;
        }
    }

    private static IEnumerable<Type> BusinessInterfaces(Type type) =>
        type.GetInterfaces().Where(i =>
            i != typeof(IScopedService) && i != typeof(ISingletonService) && i != typeof(ITransientService)
            && IsBusinessNamespace(i));

    private static bool IsBusinessNamespace(Type intf)
    {
        // 开放泛型实现类的 GetInterfaces() 返回"开放构造"接口(如 IRepo<T>),其 Namespace 为 null:
        // 统一取泛型定义(对泛型定义自身则原样)后再判命名空间
        var probe = intf.IsGenericType ? intf.GetGenericTypeDefinition() : intf;
        return probe.Namespace is not null
            && !probe.Namespace.StartsWith("System", StringComparison.Ordinal)
            && !probe.Namespace.StartsWith("Microsoft", StringComparison.Ordinal);
    }
}
