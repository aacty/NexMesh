namespace NexMesh.Discovery.Tests;

using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>Redis 服务发现:注册/发现/惰性剔除/注销(真 Redis;无环境变量软跳过)。</summary>
public class DiscoveryTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("NEXMESH_TEST_REDIS");

    private static (RedisServiceRegistry Registry, string Prefix) NewRegistry()
    {
        var mux = StackExchange.Redis.ConnectionMultiplexer.Connect(ConnectionString!);
        return (new RedisServiceRegistry(mux, $"disc:{Guid.NewGuid():N}:"), "");
    }

    [Fact]
    public async Task 注册与发现_多实例并列()
    {
        if (ConnectionString is null) return;
        var (registry, _) = NewRegistry();
        await registry.RegisterAsync(new ServiceInstance { ServiceName = "svc-a", InstanceId = "i1", Address = "http://10.0.0.1:5000" });
        await registry.RegisterAsync(new ServiceInstance { ServiceName = "svc-a", InstanceId = "i2", Address = "http://10.0.0.2:5000" });
        await registry.RegisterAsync(new ServiceInstance { ServiceName = "svc-b", InstanceId = "i3", Address = "http://10.0.0.3:5000" });

        var found = await registry.DiscoverAsync("svc-a");
        Assert.Equal(2, found.Count);
        Assert.All(found, f => Assert.Equal("svc-a", f.ServiceName));
        Assert.Single(await registry.DiscoverAsync("svc-b"));
    }

    [Fact]
    public async Task 注销后从发现结果消失()
    {
        if (ConnectionString is null) return;
        var (registry, _) = NewRegistry();
        await registry.RegisterAsync(new ServiceInstance { ServiceName = "svc", InstanceId = "i1", Address = "http://x" });
        await registry.DeregisterAsync("svc", "i1");
        Assert.Empty(await registry.DiscoverAsync("svc"));
    }

    [Fact]
    public async Task TTL过期实例_惰性剔除_不返回()
    {
        if (ConnectionString is null) return;
        var mux = StackExchange.Redis.ConnectionMultiplexer.Connect(ConnectionString);
        var prefix = $"disc:{Guid.NewGuid():N}:";
        var shortTtl = new RedisServiceRegistry(mux, prefix, TimeSpan.FromMilliseconds(300));
        var normal = new RedisServiceRegistry(mux, prefix);

        await shortTtl.RegisterAsync(new ServiceInstance { ServiceName = "svc", InstanceId = "expired", Address = "http://x" });
        await Task.Delay(600); // 实例键过 TTL

        var found = await normal.DiscoverAsync("svc");
        Assert.Empty(found); // 惰性剔除:索引中的过期实例被移除且不返回
    }

    [Fact]
    public async Task 心跳服务_持续续约()
    {
        if (ConnectionString is null) return;
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddNexMeshServiceDiscovery(ConnectionString,
            self => { self.ServiceName = "hb-svc"; self.Address = "http://heartbeat:5000"; },
            ttlSeconds: 2);
        var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IServiceRegistry>();
        var hosted = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<HeartbeatService>().Single();
        await hosted.StartAsync(CancellationToken.None);

        await Task.Delay(200);
        Assert.Single(await registry.DiscoverAsync("hb-svc"));
        await Task.Delay(1100); // 跨过一个心跳周期(TTL 2s,间隔 1s)
        Assert.Single(await registry.DiscoverAsync("hb-svc")); // 心跳续约中

        await hosted.StopAsync(CancellationToken.None);
        Assert.Empty(await registry.DiscoverAsync("hb-svc")); // 优雅停机已注销
        await provider.DisposeAsync();
    }
}
