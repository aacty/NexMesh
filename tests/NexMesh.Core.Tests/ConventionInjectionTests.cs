namespace NexMesh.Core.Tests;

using Microsoft.Extensions.DependencyInjection;
using NexMesh.Core.DependencyInjection;

public interface IOrderService { }

public sealed class OrderService : IOrderService, IScopedService
{
    public Guid Id { get; } = Guid.NewGuid();
}

public interface ICounter { }

public sealed class CounterSingleton : ICounter, ISingletonService
{
    public Guid Id { get; } = Guid.NewGuid();
}

public sealed class MailTransient : ITransientService
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// <summary>实现 IDisposable 的标记服务:验证 System 接口不会被注册为服务类型。</summary>
public sealed class DisposableService : IScopedService, IDisposable
{
    public void Dispose() { }
}

public class ConventionInjectionTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddNexMeshConventionServices(typeof(OrderService).Assembly);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Scoped_同作用域同实例_跨作用域新实例()
    {
        using var provider = BuildProvider();
        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();

        var a = scope1.ServiceProvider.GetRequiredService<OrderService>();
        var b = scope1.ServiceProvider.GetRequiredService<OrderService>();
        var c = scope2.ServiceProvider.GetRequiredService<OrderService>();

        Assert.Same(a, b);
        Assert.NotSame(a, c);
    }

    [Fact]
    public void 接口注册转发到具体类型_同作用域共享实例()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var byInterface = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var byConcrete = scope.ServiceProvider.GetRequiredService<OrderService>();

        Assert.Same(byConcrete, byInterface);
    }

    [Fact]
    public void Singleton_跨作用域同实例()
    {
        using var provider = BuildProvider();
        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();

        var a = scope1.ServiceProvider.GetRequiredService<ICounter>();
        var b = scope2.ServiceProvider.GetRequiredService<ICounter>();

        Assert.Same(a, b);
    }

    [Fact]
    public void Transient_每次解析新实例()
    {
        using var provider = BuildProvider();
        var a = provider.GetRequiredService<MailTransient>();
        var b = provider.GetRequiredService<MailTransient>();

        Assert.NotSame(a, b);
    }

    [Fact]
    public void System接口不参与注册()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetService<DisposableService>());
        Assert.Null(provider.GetService<IDisposable>());      // System 接口不注册
        Assert.Null(provider.GetService<IScopedService>());   // 标记接口本身不注册
    }
}
