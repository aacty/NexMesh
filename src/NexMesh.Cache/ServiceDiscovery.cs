namespace NexMesh.Discovery;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

/// <summary>服务实例注册信息。</summary>
public sealed record ServiceInstance
{
    /// <summary>服务名(逻辑名,如 "socialwork-api")。</summary>
    public string ServiceName { get; init; } = "";

    /// <summary>实例唯一标识(缺省主机名#PID)。</summary>
    public string InstanceId { get; init; } = "";

    /// <summary>调用地址(如 http://192.168.1.8:50010)。</summary>
    public string Address { get; init; } = "";

    /// <summary>可选 gRPC 地址。</summary>
    public string? GrpcAddress { get; init; }

    /// <summary>元数据(版本/租户/区域)。</summary>
    public Dictionary<string, string> Metadata { get; init; } = [];

    /// <summary>最后心跳时间。</summary>
    public DateTimeOffset LastHeartbeat { get; init; }
}

/// <summary>服务发现客户端:注册自身 + 按名发现他人。</summary>
public interface IServiceRegistry
{
    /// <summary>注册自身(心跳由后台服务自动续约;TTL 过期即被剔除)。</summary>
    Task RegisterAsync(ServiceInstance instance, CancellationToken ct = default);

    /// <summary>注销自身(优雅停机)。</summary>
    Task DeregisterAsync(string serviceName, string instanceId, CancellationToken ct = default);

    /// <summary>按服务名发现活跃实例(TTL 内有心跳的)。</summary>
    Task<List<ServiceInstance>> DiscoverAsync(string serviceName, CancellationToken ct = default);
}

/// <summary>
/// Redis 注册表服务发现:实例键 = {prefix}svc:{service}:{instance}(JSON+TTL,心跳续约);
/// 服务索引 = {prefix}svcidx:{service}(SET 记录实例 ID,惰性剔除——发现时剔除已过期键)。
/// 心跳间隔= TTL/2;无中心节点,Redis 即真相源。
/// </summary>
public sealed class RedisServiceRegistry(
    IConnectionMultiplexer redis,
    string prefix = "nexmesh:disc:",
    TimeSpan? ttl = null) : IServiceRegistry
{
    private readonly TimeSpan _ttl = ttl ?? TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RegisterAsync(ServiceInstance instance, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var value = JsonSerializer.Serialize(instance with { LastHeartbeat = DateTimeOffset.UtcNow }, Json);
        await db.StringSetAsync(InstanceKey(instance.ServiceName, instance.InstanceId), value, _ttl);
        await db.SetAddAsync(IndexKey(instance.ServiceName), instance.InstanceId);
    }

    public async Task DeregisterAsync(string serviceName, string instanceId, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        await db.KeyDeleteAsync(InstanceKey(serviceName, instanceId));
        await db.SetRemoveAsync(IndexKey(serviceName), instanceId);
    }

    public async Task<List<ServiceInstance>> DiscoverAsync(string serviceName, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var indexKey = IndexKey(serviceName);
        var instanceIds = await db.SetMembersAsync(indexKey);
        var result = new List<ServiceInstance>();
        foreach (var id in instanceIds)
        {
            var raw = await db.StringGetAsync(InstanceKey(serviceName, id.ToString()));
            if (raw.IsNullOrEmpty)
            {
                await db.SetRemoveAsync(indexKey, id); // 惰性剔除:实例键已过 TTL
                continue;
            }
            try
            {
                var parsed = JsonSerializer.Deserialize<ServiceInstance>(raw.ToString(), Json);
                if (parsed is not null)
                {
                    result.Add(parsed);
                }
            }
            catch (JsonException)
            {
                await db.SetRemoveAsync(indexKey, id); // 污染数据剔除
            }
        }
        return result;
    }

    private string InstanceKey(string service, string instance) => $"{prefix}svc:{service}:{instance}";
    private string IndexKey(string service) => $"{prefix}svcidx:{service}";
}

/// <summary>心跳后台服务:每 TTL/2 对已注册实例续约(进程死→TTL 过期→自动出发现结果)。</summary>
public sealed class HeartbeatService(
    IServiceRegistry registry,
    ServiceInstance self,
    TimeSpan ttl,
    ILogger<HeartbeatService>? logger = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromTicks(Math.Max(ttl.Ticks / 2, TimeSpan.FromSeconds(1).Ticks));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await registry.RegisterAsync(self, stoppingToken);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[NexMesh.Discovery] 心跳失败(下轮重试)");
            }
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        try
        {
            await registry.DeregisterAsync(self.ServiceName, self.InstanceId); // 优雅停机注销
        }
        catch
        {
            // 停机注销失败由 TTL 兜底
        }
    }
}

public static class NexMeshDiscoveryExtensions
{
    /// <summary>
    /// 注册服务发现:configureSelf 描述自身(服务名/地址/元数据);后台心跳每 TTL/2 续约;
    /// 实例 ID 缺省"主机名#PID"。消费方注入 IServiceRegistry.DiscoverAsync("服务名")。
    /// </summary>
    public static IServiceCollection AddNexMeshServiceDiscovery(
        this IServiceCollection services,
        string redisConnectionString,
        Action<ServiceInstanceBuilder> configureSelf,
        string keyPrefix = "nexmesh:disc:",
        int ttlSeconds = 30)
    {
        var multiplexer = ConnectionMultiplexer.Connect(redisConnectionString);
        var ttl = TimeSpan.FromSeconds(ttlSeconds);
        services.AddSingleton(multiplexer);
        services.AddSingleton<IServiceRegistry>(new RedisServiceRegistry(multiplexer, keyPrefix, ttl));

        var builder = new ServiceInstanceBuilder
        {
            InstanceId = $"{Environment.MachineName}#{Environment.ProcessId}",
        };
        configureSelf(builder);
        var self = builder.Build();
        services.AddSingleton(self);
        services.AddSingleton(new HostedParams(ttl));
        services.AddHostedService(sp => new HeartbeatService(
            sp.GetRequiredService<IServiceRegistry>(),
            sp.GetRequiredService<ServiceInstance>(),
            sp.GetRequiredService<HostedParams>().Ttl,
            sp.GetService<ILogger<HeartbeatService>>()));
        return services;
    }

    internal sealed record HostedParams(TimeSpan Ttl);
}


/// <summary>服务实例构建器(configureSelf 可变赋值;Build 产出不可变实例)。</summary>
public sealed class ServiceInstanceBuilder
{
    public string ServiceName { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public string Address { get; set; } = "";
    public string? GrpcAddress { get; set; }
    public Dictionary<string, string> Metadata { get; } = [];

    public ServiceInstance Build() => new()
    {
        ServiceName = ServiceName,
        InstanceId = InstanceId,
        Address = Address,
        GrpcAddress = GrpcAddress,
        Metadata = Metadata,
    };
}
