namespace NexMesh.Events;

using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>NexMesh.Events 注册入口。</summary>
public static class NexMeshEventsServiceCollectionExtensions
{
    /// <summary>
    /// 注册进程内事件总线:有界 Channel(容量/并行度/停机排干超时见 NexMesh:Events 配置节)+
    /// 发布器 + 后台消费 + 订阅者自动发现(扫描程序集中 IEventSubscriber 实现,Scoped 注册)+
    /// 默认监视器(日志)与故障处理器(日志,可被业务注册覆盖)。
    /// 处理器签名在启动期校验并编译(签名非法 fail-fast)。
    /// </summary>
    public static IServiceCollection AddNexMeshEvents(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<NexMeshEventsOptions>? configure = null,
        params Assembly[] subscriberAssemblies)
    {
        // 幂等保护:重复调用不重复注册消费宿主(否则双倍消费)
        if (services.Any(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                && d.ImplementationType == typeof(NexMeshEventBusHostedService)))
        {
            return services;
        }

        var options = new NexMeshEventsOptions();
        configuration.GetSection(NexMeshEventsOptions.SectionName).Bind(options);
        configure?.Invoke(options);
        services.AddOptions<NexMeshEventsOptions>().Configure(o =>
        {
            o.Capacity = options.Capacity;
            o.Concurrency = options.Concurrency;
            o.Unicast = options.Unicast;
            o.DrainTimeoutSeconds = options.DrainTimeoutSeconds;
        });

        // 上下文/五步纪律执行器:自给自足(TryAdd 不与 AddNexMesh 宿主重复注册)
        services.TryAddSingleton<NexMesh.Core.Context.INexMeshContextAccessor, NexMesh.Core.Context.AsyncLocalNexMeshContextAccessor>();
        services.TryAddSingleton<NexMesh.Core.Context.INexMeshBackgroundRunner, NexMesh.Core.Context.NexMeshBackgroundRunner>();

        // 订阅者自动发现:IEventSubscriber 实现类(未显式注册时按 Scoped 注册)
        var assemblies = subscriberAssemblies.Length > 0 ? subscriberAssemblies : [Assembly.GetEntryAssembly() ?? typeof(NexMeshEventsServiceCollectionExtensions).Assembly];
        var subscriberTypes = new List<Type>();
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type is { IsClass: true, IsAbstract: false }
                    && typeof(IEventSubscriber).IsAssignableFrom(type))
                {
                    subscriberTypes.Add(type);
                    if (!services.Any(d => d.ServiceType == type))
                    {
                        services.AddScoped(type);
                    }
                }
            }
        }
        // 逐条以 typeof(Type) 为服务类型注册订阅者类型实例,使 IEnumerable<Type> 可解析
        // (重载陷阱:AddSingleton(typeof(Type), T) 会命中 (serviceType, implementationType) 双 Type 重载,必须显式转 object)
        foreach (var subscriberType in subscriberTypes)
        {
            services.AddSingleton(typeof(Type), (object)subscriberType);
        }

        // 有界通道:注册期创建,发布器与消费服务共享;FullMode.Wait = 满则回压等待
        services.AddSingleton(_ => Channel.CreateBounded<EventWorkItem>(
            new BoundedChannelOptions(Math.Max(options.Capacity, 1))
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = options.Concurrency <= 1,
                SingleWriter = false,
            }));
        services.AddSingleton(sp =>
        {
            var channel = sp.GetRequiredService<Channel<EventWorkItem>>();
            // 队列深度可观察指标(gauge 读通道计数)
            EventBusMetrics.Initialize().CreateObservableGauge<int>(
                "nexmesh.events.queue_depth", () => channel.Reader.Count, unit: "{events}");
            return new NexMeshEventPublisher(
                channel.Writer,
                sp.GetRequiredService<NexMesh.Core.Context.INexMeshContextAccessor>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System);
        });
        services.TryAddSingleton<IEventPublisher>(sp => sp.GetRequiredService<NexMeshEventPublisher>());
        services.TryAddSingleton<IEventMonitor, EventLogMonitor>();
        services.TryAddSingleton<IEventFaultHandler, EventLogFaultHandler>();
        services.AddHostedService<NexMeshEventBusHostedService>();
        return services;
    }
}

/// <summary>默认监视器:处理器执行/完成/失败日志(调试级)。</summary>
public sealed class EventLogMonitor(ILogger<EventLogMonitor> logger) : IEventMonitor
{
    public Task OnExecutingAsync(EventContext context, string handlerName, CancellationToken cancellationToken)
    {
        logger.LogDebug("[NexMesh.Events] {Handler} 开始处理 {EventId}", handlerName, context.EventId);
        return Task.CompletedTask;
    }

    public Task OnExecutedAsync(EventContext context, string handlerName, Exception? exception, CancellationToken cancellationToken)
    {
        if (exception is null)
        {
            logger.LogDebug("[NexMesh.Events] {Handler} 完成 {EventId}", handlerName, context.EventId);
        }
        else
        {
            logger.LogError(exception, "[NexMesh.Events] {Handler} 处理 {EventId} 失败(重试已耗尽)", handlerName, context.EventId);
        }
        return Task.CompletedTask;
    }
}

/// <summary>默认故障处理器:记录 Error 日志(业务可注册自定义实现做死信持久化等)。</summary>
public sealed class EventLogFaultHandler(ILogger<EventLogFaultHandler> logger) : IEventFaultHandler
{
    public Task HandleAsync(EventContext context, string handlerName, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "[NexMesh.Events] 事件 {EventId} 在 {Handler} 重试耗尽后进入故障处理(默认:仅日志)",
            context.EventId, handlerName);
        return Task.CompletedTask;
    }
}
