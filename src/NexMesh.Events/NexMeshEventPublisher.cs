namespace NexMesh.Events;

using System.Threading.Channels;
using NexMesh.Core.Context;

/// <summary>事件工作项(队列元素):事件 ID + 载荷 + 发布时刻的上下文快照。</summary>
public sealed record EventWorkItem(string EventId, object? Payload, NexMesh.Core.Context.NexMeshContext? Context, DateTimeOffset CreatedAt);

/// <summary>
/// 事件发布器:写入有界 Channel 即返回;队列满时 WriteAsync 回压等待(不丢不静默)。
/// 捕获发布时刻的框架上下文快照随事件入队——处理时经 INexMeshBackgroundRunner 五步纪律再水化,
/// 处理器内身份/租户/链路与发布端一致。
/// </summary>
public sealed class NexMeshEventPublisher(
    ChannelWriter<EventWorkItem> writer,
    INexMeshContextAccessor contextAccessor,
    TimeProvider timeProvider) : IEventPublisher
{
    public Task PublishAsync(string eventId, object? payload = null, CancellationToken cancellationToken = default)
        => WriteAsync(eventId, payload, cancellationToken);

    public Task PublishAsync(Enum eventId, object? payload = null, CancellationToken cancellationToken = default)
        => WriteAsync(eventId.ToString()!, payload, cancellationToken);

    private async Task WriteAsync(string eventId, object? payload, CancellationToken cancellationToken)
    {
        var item = new EventWorkItem(eventId, payload, contextAccessor.Current, timeProvider.GetUtcNow());
        await writer.WriteAsync(item, cancellationToken);
        EventBusMetrics.Published(eventId);
    }
}

/// <summary>事件总线 OTel 指标(Meter nexmesh.events):发布/重试/丢弃计数。零第三方依赖(System.Diagnostics.Metrics 运行时内置)。</summary>
internal static class EventBusMetrics
{
    public const string MeterName = "nexmesh.events";

    private static readonly System.Diagnostics.Metrics.Meter Meter = new(MeterName);
    private static readonly System.Diagnostics.Metrics.Counter<long> PublishedCounter = Meter.CreateCounter<long>("nexmesh.events.published");
    private static readonly System.Diagnostics.Metrics.Counter<long> RetriedCounter = Meter.CreateCounter<long>("nexmesh.events.retried");
    private static readonly System.Diagnostics.Metrics.Counter<long> DroppedCounter = Meter.CreateCounter<long>("nexmesh.events.dropped");

    public static void Published(string eventId) => PublishedCounter.Add(1, new KeyValuePair<string, object?>("event_id", eventId));

    public static void Retried(string eventId, string handlerName) =>
        RetriedCounter.Add(1, new KeyValuePair<string, object?>("event_id", eventId), new KeyValuePair<string, object?>("handler", handlerName));

    public static void Dropped(string eventId, string handlerName, string reason) =>
        DroppedCounter.Add(1,
            new KeyValuePair<string, object?>("event_id", eventId),
            new KeyValuePair<string, object?>("handler", handlerName),
            new KeyValuePair<string, object?>("reason", reason));

    public static System.Diagnostics.Metrics.Meter Initialize() => Meter;
}
