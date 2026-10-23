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
/// 不传参 → 扫描入口程序集。System 命名空间下的接口(IDisposable 等)与其他标记接口不参与注册。
/// </summary>
public static class ConventionServiceCollectionExtensions
{
    public static IServiceCollection AddNexMeshConventionServices(this IServiceCollection services, params Assembly[] assemblies)
    {
        assemblies = assemblies.Length > 0 ? assemblies : [Assembly.GetEntryAssembly()!];

        foreach (var asm in assemblies)
        {
            var types = asm.GetTypes().Where(t => !t.IsAbstract && !t.IsInterface);
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
                foreach (var intf in BusinessInterfaces(type))
                    services.Add(ServiceDescriptor.Describe(intf, sp => sp.GetRequiredService(type), lifetime));
            }
        }
        return services;
    }

    private static IEnumerable<Type> BusinessInterfaces(Type type) =>
        type.GetInterfaces().Where(i =>
            i != typeof(IScopedService) && i != typeof(ISingletonService) && i != typeof(ITransientService)
            && i.Namespace is not null && !i.Namespace.StartsWith("System", StringComparison.Ordinal));
}
