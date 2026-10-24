namespace NexMesh.Events;

using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexMesh.Core.Context;

/// <summary>
/// 事件总线后台消费服务:
/// ① 启动期构建订阅注册表(编译委托,签名非法 fail-fast,确定性排序);
/// ② 按配置并行度消费有界 Channel,单事件内订阅者按排序顺序执行;Unicast 模式只执行首个匹配订阅;
/// ③ 处理经 INexMeshBackgroundRunner 五步纪律(链路接续/环境槽/独立作用域/双令牌/全还原);
/// ④ 异常隔离:分发路径任何非取消异常(含正则匹配超时、DI 解析失败)都被捕获,
///    消费循环与宿主不因业务异常终止;
/// ⑤ 停机契约:取消即停止取新事件(在途队列留给排干),排干用新鲜令牌限时处理;
///    注册表未构建时安全丢弃计数;排干结束完成写端——停机后发布方收 ChannelClosedException 而非永久悬挂。
/// </summary>
public sealed class NexMeshEventBusHostedService(
    Channel<EventWorkItem> channel,
    IOptions<NexMeshEventsOptions> options,
    IEnumerable<Type> subscriberTypes,
    INexMeshBackgroundRunner runner,
    TimeProvider? timeProvider = null,
    ILogger<NexMeshEventBusHostedService>? logger = null) : BackgroundService
{
    private readonly ChannelReader<EventWorkItem> _reader = channel.Reader;
    private readonly ChannelWriter<EventWorkItem> _writer = channel.Writer;
    private readonly NexMeshEventsOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger? _logger = logger;
    private EventSubscriptionRegistry? _registry;

    /// <summary>启动期构建注册表:扫描订阅类型的全部 [EventSubscribe] 方法并编译,签名非法立即失败。</summary>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriptions = new List<EventSubscription>();
        foreach (var type in subscriberTypes)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var attribute in method.GetCustomAttributes<EventSubscribeAttribute>())
                {
                    subscriptions.Add(new EventSubscription(type, method, attribute));
                }
            }
        }
        _registry = new EventSubscriptionRegistry(subscriptions);
        _logger?.LogInformation("[NexMesh.Events] 注册表构建完成:订阅者 {Subscribers} 个,订阅 {Subscriptions} 条(含正则 {Fuzzy} 条;单播={Unicast})",
            subscriberTypes.Count(), subscriptions.Count, subscriptions.Count(s => s.Attribute.FuzzyMatch), _options.Unicast);

        var consumers = new Task[Math.Max(_options.Concurrency, 1)];
        for (var i = 0; i < consumers.Length; i++)
        {
            consumers[i] = ConsumeAsync(stoppingToken);
        }
        return Task.WhenAll(consumers);
    }

    /// <summary>停机排干契约:先等消费循环退出(base 停机),再用新鲜令牌限时排空可读事件;
    /// 剩余未处理事件计数上报;最后完成写端——停机后发布方立即收到 ChannelClosedException 而非永久悬挂。</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        // 注册表未构建(宿主从未启动):显式安全丢弃路径,不进入分发
        if (_registry is null)
        {
            ReportRemaining("(never-started)");
            _writer.TryComplete();
            return;
        }

        var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drainCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(_options.DrainTimeoutSeconds, 0)));
        try
        {
            while (_reader.TryRead(out var item))
            {
                await DispatchSafeAsync(item, drainCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // 排干超时:停止消费,统计剩余并上报
        }
        ReportRemaining("(shutdown)");
        _writer.TryComplete(); // 停机封口:悬挂中的发布方立刻失败而非无限等待
    }

    /// <summary>统计并上报停机时仍未处理的事件数(丢弃计数 + 警告日志)。</summary>
    private void ReportRemaining(string reason)
    {
        var remaining = _reader.Count;
        if (remaining <= 0)
        {
            return;
        }
        _logger?.LogWarning("[NexMesh.Events] 停机排干未处理事件 {Count} 条已丢弃({Reason})", remaining, reason);
        for (var i = 0; i < remaining && _reader.TryRead(out var dropped); i++)
        {
            EventBusMetrics.Dropped(dropped.EventId, reason, "drain-timeout");
        }
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!await _reader.WaitToReadAsync(stoppingToken))
                {
                    return; // 写端关闭
                }
                // 取消即停:已入队事件留给 StopAsync 排干阶段用新鲜令牌处理,
                // 避免在停机时用已取消令牌分发导致支持取消的处理器被无谓中断
                while (!stoppingToken.IsCancellationRequested && _reader.TryRead(out var item))
                {
                    await DispatchSafeAsync(item, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停机:StopAsync 负责排干
        }
    }

    /// <summary>分发隔离壳:任何非取消异常(含 DI 解析失败、基础设施异常)都被捕获记日志与
    /// dropped 指标,消费循环不因此终止;取消异常上抛(停机语义)。</summary>
    private async Task DispatchSafeAsync(EventWorkItem item, CancellationToken cancellationToken)
    {
        try
        {
            await DispatchAsync(item, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 停机取消向上传导(消费循环据此退出)
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[NexMesh.Events] 事件 {EventId} 分发时发生非预期异常(毒事件已隔离,总线继续运行)", item.EventId);
            EventBusMetrics.Dropped(item.EventId, "(dispatch)", "unexpected-dispatch-failure");
        }
    }

    /// <summary>单事件分发:全部匹配订阅按确定性顺序执行(五步纪律 + 特性级重试 + 监视器 + 故障上报);
    /// 任何异常都不逃逸——消费循环永不因业务异常终止。</summary>
    private async Task DispatchAsync(EventWorkItem item, CancellationToken cancellationToken)
    {
        var subscriptions = _registry!.Match(item.EventId);
        if (subscriptions.Count == 0)
        {
            _logger?.LogDebug("[NexMesh.Events] 事件 {EventId} 无订阅者,忽略", item.EventId);
            return;
        }
        if (_options.Unicast && subscriptions.Count > 1)
        {
            subscriptions = [subscriptions[0]]; // 单播:只执行排序最前的订阅(确定性)
        }

        foreach (var subscription in subscriptions)
        {
            await runner.RunAsync(item.Context, async (sp, ct) =>
            {
                var context = new EventContext(item.EventId, item.Payload, item.Context, item.CreatedAt, ct);
                var monitor = sp.GetService<IEventMonitor>();
                var fault = sp.GetService<IEventFaultHandler>();
                await SafeMonitorAsync(monitor, m => m.OnExecutingAsync(context, subscription.HandlerName, context.CancellationToken));

                var lastError = await EventRetryLoop.ExecuteAsync(
                    subscription.Attribute.NumRetries,
                    subscription.Attribute.RetryTimeoutMs,
                    subscription.Attribute.ExceptionTypes,
                    _timeProvider,
                    async () =>
                    {
                        var instance = sp.GetRequiredService(subscription.SubscriberType);
                        await subscription.Invoke(instance, context);
                    },
                    onRetry: () => EventBusMetrics.Retried(item.EventId, subscription.HandlerName),
                    cancellationToken: ct);

                if (lastError is not null)
                {
                    EventBusMetrics.Dropped(item.EventId, subscription.HandlerName, "retries-exhausted");
                    await SafeMonitorAsync(monitor, m => m.OnExecutedAsync(context, subscription.HandlerName, lastError, context.CancellationToken));
                    if (fault is not null)
                    {
                        try
                        {
                            await fault.HandleAsync(context, subscription.HandlerName, lastError, ct);
                        }
                        catch (Exception faultEx)
                        {
                            _logger?.LogError(faultEx, "[NexMesh.Events] 故障处理器 {Handler} 自身异常(事件 {EventId})",
                                subscription.HandlerName, item.EventId);
                        }
                    }
                    return;
                }
                await SafeMonitorAsync(monitor, m => m.OnExecutedAsync(context, subscription.HandlerName, null, context.CancellationToken));
            }, cancellationToken);
        }
    }

    /// <summary>监视器异常不影响事件处理(监视器是旁路)。</summary>
    private static async Task SafeMonitorAsync(IEventMonitor? monitor, Func<IEventMonitor, Task> invoke)
    {
        if (monitor is null)
        {
            return;
        }
        try
        {
            await invoke(monitor);
        }
        catch
        {
            // 旁路失败静默
        }
    }
}
