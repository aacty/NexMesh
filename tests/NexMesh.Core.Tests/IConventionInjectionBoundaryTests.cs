namespace NexMesh.Core.Tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexMesh.Core.DependencyInjection;

public interface IGenericRepo<T>
{
    T? Get(int id);
}

public sealed class GenericRepo<T> : IGenericRepo<T>, IScopedService
{
    public T? Get(int id) => default;
}

/// <summary>同时实现 ISingletonService 与 IHostedService:Microsoft 命名空间接口不应被约定注册。</summary>
public sealed class BoundaryHosted : ISingletonService, IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>双标记接口:IScopedService + ISingletonService,生命周期必须确定。</summary>
public sealed class DualMarkedService : IScopedService, ISingletonService
{
}

public abstract class MarkedBase : IScopedService
{
}

public sealed class MarkedDerived : MarkedBase
{
}

public interface IDupBiz
{
    string Who();
}

public sealed class DupBizFirst : IDupBiz, IScopedService
{
    public string Who() => "first";
}

public sealed class DupBizSecond : IDupBiz, IScopedService
{
    public string Who() => "second";
}

/// <summary>约定注入边界攻击面(交叉测试 I):泛型标记、继承链、双标记、宿主接口误注册。</summary>
public class IConventionInjectionBoundaryTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddNexMeshConventionServices(typeof(IConventionInjectionBoundaryTests).Assembly);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void 开放泛型实现类_业务接口按闭合类型可解析()
    {
        using var provider = BuildProvider();

        // GenericRepo<T> : IGenericRepo<T> + IScopedService
        // 期望:IRepo<int> → GenericRepo<int>;缺陷时:开放泛型 factory 转发解析炸
        var repo = provider.GetRequiredService<IGenericRepo<int>>();
        Assert.NotNull(repo);
    }

    [Fact]
    public void 开放泛型实现类_自身按闭合类型可解析()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService<GenericRepo<int>>());
    }

    [Fact]
    public void Microsoft命名空间接口_不参与约定注册()
    {
        var services = new ServiceCollection();
        services.AddNexMeshConventionServices(typeof(IConventionInjectionBoundaryTests).Assembly);

        // IHostedService 被宿主以 GetServices 消费:约定误注册会把它拽进业务生命周期
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
        // 自身具体类型仍正常注册
        Assert.Contains(services, d => d.ServiceType == typeof(BoundaryHosted));
    }

    [Fact]
    public void 双标记接口_生命周期确定取首个匹配_Scoped()
    {
        var services = new ServiceCollection();
        services.AddNexMeshConventionServices(typeof(IConventionInjectionBoundaryTests).Assembly);

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(DualMarkedService));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void 继承链标记接口_子类自动注册()
    {
        using var provider = BuildProvider();

        // MarkedDerived 继承 MarkedBase 的 IScopedService 标记
        Assert.NotNull(provider.GetRequiredService<MarkedDerived>());
        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();
        Assert.NotSame(
            scope1.ServiceProvider.GetRequiredService<MarkedDerived>(),
            scope2.ServiceProvider.GetRequiredService<MarkedDerived>());
    }

    [Fact]
    public void 多实现同一业务接口_单解析取最后注册_可枚举取全部()
    {
        using var provider = BuildProvider();

        Assert.Equal("second", provider.GetRequiredService<IDupBiz>().Who());
        var all = provider.GetServices<IDupBiz>().ToList();
        Assert.Equal(2, all.Count);
    }
}
