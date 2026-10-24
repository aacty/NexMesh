using NexMesh.Events;

namespace NexMesh.AiDemo.Events;

/// <summary>事件总线演示订阅者:发布→五步纪律执行→日志可见。</summary>
public sealed class DemoEventSubscriber(ILogger<DemoEventSubscriber> logger) : IEventSubscriber
{
    [EventSubscribe("demo:employee:created")]
    public Task OnEmployeeCreated(EventContext context)
    {
        logger.LogInformation("[演示] 收到员工创建事件 {EventId},租户={Tenant},载荷={Payload}",
            context.EventId, context.Context?.TenantId ?? "(无)", context.Payload);
        return Task.CompletedTask;
    }

    [EventSubscribe("demo:order:flaky", NumRetries = 2, RetryTimeoutMs = 100)]
    public Task OnFlaky(EventContext context)
    {
        logger.LogInformation("[演示] 疑似不稳定处理器被调用(演示重试策略)");
        throw new InvalidOperationException("演示:首次必然失败,观察重试与故障上报");
    }
}
