namespace NexMesh.Events.Tests;

using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>测试基座:每个用例独立 Host(独立 Channel/注册表),互不串扰。</summary>
public sealed class EventHost : IAsyncDisposable
{
    private readonly IHost _host;

    private EventHost(IHost host) => _host = host;

    public IServiceProvider Services => _host.Services;

    public IEventPublisher Publisher => _host.Services.GetRequiredService<IEventPublisher>();

    /// <summary>显式停机(触发排干契约);DisposeAsync 里不再重复停。</summary>
    public Task StopInnerAsync() => _host.StopAsync(CancellationToken.None);

    public static async Task<EventHost> StartAsync(
        int capacity = 10_000, int concurrency = 1, int drainTimeoutSeconds = 5, bool unicast = false,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:Events:Capacity"] = capacity.ToString(),
            ["NexMesh:Events:Concurrency"] = concurrency.ToString(),
            ["NexMesh:Events:DrainTimeoutSeconds"] = drainTimeoutSeconds.ToString(),
            ["NexMesh:Events:Unicast"] = unicast ? "true" : "false",
        });
        configureServices?.Invoke(builder.Services);
        builder.Services.AddNexMeshEvents(builder.Configuration, subscriberAssemblies: typeof(EventHost).Assembly);
        var host = builder.Build();
        await host.StartAsync();
        return new EventHost(host);
    }

    /// <summary>轮询等待断言条件成立(10ms 间隔,默认 15 秒超时);超时抛出 fail 信息。</summary>
    public static async Task UntilAsync(Func<bool> condition, string failMessage, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException(failMessage);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}

/// <summary>测试订阅者全集:事件 ID 前缀隔离用例,静态计数器收集结果。</summary>
public static class TestCounters
{
    public static readonly ConcurrentDictionary<string, int> Received = new();
    public static readonly ConcurrentDictionary<string, int> Attempts = new();
    public static readonly ConcurrentQueue<(string EventId, string Handler, Exception Error)> Faults = new();
    public static readonly ConcurrentQueue<string> ExecutedOrder = new();

    public static void Reset()
    {
        Received.Clear();
        Attempts.Clear();
        Faults.Clear();
        ExecutedOrder.Clear();
    }
}

public enum TestEvents
{
    OrderCreated,
    OrderPaid,
}

// —— 路由用例 ——
public sealed class RoutingSubscriber : IEventSubscriber
{
    [EventSubscribe("route:exact")]
    public Task OnExact(EventContext context)
    {
        TestCounters.Received.AddOrUpdate("route:exact", 1, (_, c) => c + 1);
        TestCounters.ExecutedOrder.Enqueue("route:exact");
        return Task.CompletedTask;
    }

    [EventSubscribe(TestEvents.OrderCreated)]
    public Task OnEnum(EventContext context)
    {
        TestCounters.Received.AddOrUpdate("OrderCreated", 1, (_, c) => c + 1);
        return Task.CompletedTask;
    }

    [EventSubscribe(@"^route:phone:\d{11}$", FuzzyMatch = true)]
    public Task OnRegex(EventContext context)
    {
        TestCounters.Received.AddOrUpdate("route:regex", 1, (_, c) => c + 1);
        return Task.CompletedTask;
    }

    [EventSubscribe("route:broadcast")]
    public Task OnBroadcastA(EventContext context)
    {
        TestCounters.Received.AddOrUpdate("route:broadcast:A", 1, (_, c) => c + 1);
        return Task.CompletedTask;
    }
}

public sealed class RoutingSubscriber2 : IEventSubscriber
{
    [EventSubscribe("route:broadcast")]
    public Task OnBroadcastB(EventContext context)
    {
        TestCounters.Received.AddOrUpdate("route:broadcast:B", 1, (_, c) => c + 1);
        return Task.CompletedTask;
    }
}

// —— 重试用例 ——
public sealed class RetrySubscriber : IEventSubscriber
{
    [EventSubscribe("retry:flaky", NumRetries = 3)]
    public Task OnFlaky(EventContext context)
    {
        var attempts = TestCounters.Attempts.AddOrUpdate("retry:flaky", 1, (_, c) => c + 1);
        if (attempts <= 2)
        {
            throw new InvalidOperationException("前两次失败");
        }
        TestCounters.Received.AddOrUpdate("retry:flaky", 1, (_, c) => c + 1);
        return Task.CompletedTask;
    }

    [EventSubscribe("retry:always-fails", NumRetries = 2)]
    public Task OnAlwaysFails(EventContext context)
    {
        TestCounters.Attempts.AddOrUpdate("retry:always-fails", 1, (_, c) => c + 1);
        throw new InvalidOperationException("永远失败");
    }

    [EventSubscribe("retry:filtered", NumRetries = 3, ExceptionTypes = [typeof(ArgumentException)])]
    public Task OnFiltered(EventContext context)
    {
        TestCounters.Attempts.AddOrUpdate("retry:filtered", 1, (_, c) => c + 1);
        throw new InvalidOperationException("不在白名单,不应重试");
    }
}

// —— 压测/回压用例 ——
public sealed class StressSubscriber : IEventSubscriber
{
    [EventSubscribe("stress:load")]
    public Task OnLoad(EventContext context)
    {
        TestCounters.Received.AddOrUpdate((string)context.Payload!, 1, (_, c) => c + 1);
        return Task.CompletedTask;
    }
}

public sealed class PayloadSubscriber : IEventSubscriber
{
    public static string? LastPayload;

    [EventSubscribe("payload:typed")]
    public Task OnTyped(EventContext context)
    {
        LastPayload = context.GetPayload<string>();
        TestCounters.Received.AddOrUpdate("payload:typed", 1, (_, c) => c + 1);
        return Task.CompletedTask;
    }
}

// —— 故障处理器(可观察重试耗尽) ——
public sealed class RecordingFaultHandler : IEventFaultHandler
{
    public Task HandleAsync(EventContext context, string handlerName, Exception exception, CancellationToken cancellationToken)
    {
        TestCounters.Faults.Enqueue((context.EventId, handlerName, exception));
        return Task.CompletedTask;
    }
}

// —— 慢处理器(停机排干用例:阻塞直到放行) ——
public sealed class GatedSubscriber : IEventSubscriber
{
    public static TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [EventSubscribe("shutdown:gated")]
    public async Task OnGated(EventContext context)
    {
        TestCounters.Received.AddOrUpdate("shutdown:gated", 1, (_, c) => c + 1);
        await Gate.Task; // 阻塞直至测试放行
    }
}

// —— SSS+ 加固用例:毒事件隔离 / 确定性顺序 / 未启动停机 ——
public sealed class PoisonSubscriber : IEventSubscriber
{
    public static bool Activated;

    [EventSubscribe("poison:bad")]
    public Task OnPoison(EventContext context)
    {
        Activated = true;
        throw new OutOfMemoryException("模拟毒事件:极不寻常的运行时异常");
    }
}

public sealed class AfterPoisonSubscriber : IEventSubscriber
{
    [EventSubscribe("poison:after")]
    public Task OnAfter(EventContext context)
    {
        TestCounters.Received.AddOrUpdate("poison:after", 1, (_, c) => c + 1);
        return Task.CompletedTask;
    }
}

/// <summary>排序约定用订阅者:类名 Alphabet 前置,验证广播顺序与单播选择按 SortKey 确定。</summary>
public sealed class AaOrderSubscriber : IEventSubscriber
{
    [EventSubscribe("order:stable")]
    public Task Handle(EventContext context)
    {
        TestCounters.ExecutedOrder.Enqueue("AaOrderSubscriber.Handle");
        TestCounters.Received.AddOrUpdate("order:stable:Aa", 1, (_, c) => c + 1);
        return Task.CompletedTask;
    }
}

public sealed class ZzOrderSubscriber : IEventSubscriber
{
    [EventSubscribe("order:stable")]
    public Task Handle(EventContext context)
    {
        TestCounters.ExecutedOrder.Enqueue("ZzOrderSubscriber.Handle");
        TestCounters.Received.AddOrUpdate("order:stable:Zz", 1, (_, c) => c + 1);
        return Task.CompletedTask;
    }
}

/// <summary>会抛异常的监视器(旁路失败必须静默)。</summary>
public sealed class ThrowingMonitor : IEventMonitor
{
    public Task OnExecutingAsync(EventContext context, string handlerName, CancellationToken cancellationToken)
        => throw new InvalidOperationException("监视器自身故障");

    public Task OnExecutedAsync(EventContext context, string handlerName, Exception? exception, CancellationToken cancellationToken)
        => throw new InvalidOperationException("监视器自身故障");
}

// —— 二轮审核用例:正则回溯炸弹 / 停机封口 ——
public sealed class RegexBombSubscriber : IEventSubscriber
{
    public static int Handled;

    // 灾难性回溯模式:对长串非匹配输入,无超时的 IsMatch 会指数级挂死
    [EventSubscribe(@"^(a+)+$", FuzzyMatch = true)]
    public Task OnBomb(EventContext context)
    {
        Interlocked.Increment(ref Handled);
        return Task.CompletedTask;
    }
}

// —— 混沌实测用例:真实并发 + 确定性故障注入 ——
public sealed class ChaosSubscriber : IEventSubscriber
{
    /// <summary>每事件尝试次数(实证重试行为)。</summary>
    public static readonly ConcurrentDictionary<string, int> Attempts = new();

    /// <summary>成功送达的事件 ID。</summary>
    public static readonly ConcurrentDictionary<string, byte> Delivered = new();

    /// <summary>编号编码规则:F 前缀=首次失败后重试成功;X 前缀=永远失败(重试耗尽)。</summary>
    [EventSubscribe("chaos:work", NumRetries = 2)]
    public Task OnWork(EventContext context)
    {
        var id = (string)context.Payload!;
        var attempt = Attempts.AddOrUpdate(id, 1, (_, c) => c + 1);
        if (id.StartsWith("X"))
        {
            throw new InvalidOperationException($"#{attempt} 永远失败(演示重试耗尽)");
        }
        if (id.StartsWith("F") && attempt == 1)
        {
            throw new InvalidOperationException("首次失败(演示重试成功)");
        }
        Delivered[id] = 1;
        return Task.CompletedTask;
    }
}
