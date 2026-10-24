namespace NexMesh.Events.Tests;

using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

/// <summary>路由语义:精确/枚举/正则/广播(多订阅者)/无订阅/类型化载荷。</summary>
public class RoutingTests : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        TestCounters.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task 精确EventId路由命中()
    {
        await using var host = await EventHost.StartAsync();
        await host.Publisher.PublishAsync("route:exact");
        await EventHost.UntilAsync(() => TestCounters.Received.GetValueOrDefault("route:exact") == 1, "精确路由未命中");
    }

    [Fact]
    public async Task 枚举EventId按ToString约定路由()
    {
        await using var host = await EventHost.StartAsync();
        await host.Publisher.PublishAsync(TestEvents.OrderCreated);
        await EventHost.UntilAsync(() => TestCounters.Received.GetValueOrDefault("OrderCreated") == 1, "枚举路由未命中");
    }

    [Fact]
    public async Task 正则FuzzyMatch命中11位手机号模式()
    {
        await using var host = await EventHost.StartAsync();
        await host.Publisher.PublishAsync("route:phone:13812345678");
        await host.Publisher.PublishAsync("route:phone:abc");
        await EventHost.UntilAsync(() => TestCounters.Received.GetValueOrDefault("route:regex") == 1, "正则路由未命中");
        await Task.Delay(200); // 确认非匹配事件没有误命中
        Assert.Equal(1, TestCounters.Received.GetValueOrDefault("route:regex"));
    }

    [Fact]
    public async Task 同一事件广播到多个订阅者()
    {
        await using var host = await EventHost.StartAsync();
        await host.Publisher.PublishAsync("route:broadcast");
        await EventHost.UntilAsync(
            () => TestCounters.Received.GetValueOrDefault("route:broadcast:A") == 1
               && TestCounters.Received.GetValueOrDefault("route:broadcast:B") == 1,
            "广播未同时命中两个订阅者");
    }

    [Fact]
    public async Task 无订阅事件被静默忽略()
    {
        await using var host = await EventHost.StartAsync();
        await host.Publisher.PublishAsync("no:subscriber:event");
        await Task.Delay(200);
        Assert.Empty(TestCounters.Faults);
    }

    [Fact]
    public async Task 类型化载荷读取()
    {
        PayloadSubscriber.LastPayload = null;
        await using var host = await EventHost.StartAsync();
        await host.Publisher.PublishAsync("payload:typed", "载荷值");
        await EventHost.UntilAsync(() => PayloadSubscriber.LastPayload == "载荷值", "类型化载荷未读到");
    }
}

/// <summary>重试语义:失败后成功/耗尽进故障处理/异常类型白名单过滤。</summary>
public class RetryTests : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        TestCounters.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task 前两次失败第三次成功_总次数为3()
    {
        await using var host = await EventHost.StartAsync();
        await host.Publisher.PublishAsync("retry:flaky");
        await EventHost.UntilAsync(() => TestCounters.Received.GetValueOrDefault("retry:flaky") == 1, "重试后成功未送达");
        Assert.Equal(3, TestCounters.Attempts.GetValueOrDefault("retry:flaky"));
        Assert.Empty(TestCounters.Faults);
    }

    [Fact]
    public async Task 重试耗尽_进故障处理_次数为NumRetries加1()
    {
        await using var host = await EventHost.StartAsync(configureServices: s => s.AddSingleton<IEventFaultHandler, RecordingFaultHandler>());
        await host.Publisher.PublishAsync("retry:always-fails");
        await EventHost.UntilAsync(() => TestCounters.Faults.Count == 1, "重试耗尽未进故障处理");
        await EventHost.UntilAsync(() => TestCounters.Attempts.GetValueOrDefault("retry:always-fails") == 3, "尝试次数不为 3(NumRetries+1)");
        Assert.Equal("retry:always-fails", TestCounters.Faults.Single().EventId);
        Assert.IsType<InvalidOperationException>(TestCounters.Faults.Single().Error);
    }

    [Fact]
    public async Task 异常不在白名单_不重试直接故障()
    {
        await using var host = await EventHost.StartAsync(configureServices: s => s.AddSingleton<IEventFaultHandler, RecordingFaultHandler>());
        await host.Publisher.PublishAsync("retry:filtered");
        await EventHost.UntilAsync(() => TestCounters.Faults.Count == 1, "白名单外异常未进故障处理");
        Assert.Equal(1, TestCounters.Attempts.GetValueOrDefault("retry:filtered")); // 只尝试一次
    }
}

/// <summary>回压语义:容量 1 的有界队列,消费者阻塞时发布端回压等待,不丢事件。</summary>
public class BackpressureTests : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        TestCounters.Reset();
        GatedSubscriber.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task 队列满时发布端回压等待_不丢弃()
    {
        await using var host = await EventHost.StartAsync(capacity: 1);
        // 第 1 条被消费(处理器阻塞在 Gate),第 2 条占满队列,第 3 条必须等待
        await host.Publisher.PublishAsync("shutdown:gated");
        await EventHost.UntilAsync(() => TestCounters.Received.GetValueOrDefault("shutdown:gated") == 1, "首条未开始处理");
        await host.Publisher.PublishAsync("stress:load", "占位1"); // 队列占满

        var blocked = host.Publisher.PublishAsync("stress:load", "占位2");
        await Task.Delay(300);
        Assert.False(blocked.IsCompleted); // 回压:发布端被挂起

        GatedSubscriber.Gate.SetResult(); // 放行,队列流动,第 3 条得以入队
        await blocked.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(blocked.IsCompletedSuccessfully);
    }
}

/// <summary>停机契约:排干期内消费剩余事件;处理器放行后全部送达。</summary>
public class ShutdownTests : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        TestCounters.Reset();
        GatedSubscriber.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task 停机排干_等待中的事件全部送达()
    {
        var host = await EventHost.StartAsync(drainTimeoutSeconds: 5);
        await host.Publisher.PublishAsync("shutdown:gated");
        await host.Publisher.PublishAsync("stress:load", "排干1");
        await host.Publisher.PublishAsync("stress:load", "排干2");
        await EventHost.UntilAsync(() => TestCounters.Received.GetValueOrDefault("shutdown:gated") == 1, "首条未进入处理");

        var stopTask = host.StopInnerAsync();          // 停机开始:消费循环退出,进入排干
        GatedSubscriber.Gate.SetResult();              // 放行门,排干继续
        await stopTask.WaitAsync(TimeSpan.FromSeconds(10));
        await EventHost.UntilAsync(
            () => TestCounters.Received.GetValueOrDefault("排干1") == 1 && TestCounters.Received.GetValueOrDefault("排干2") == 1,
            "排干期内未消费完剩余事件");
        await host.DisposeAsync();
    }
}

/// <summary>并发压测:1 万事件 × 8 并发发布,断言不丢不重(恰好送达一次)。</summary>
public class StressTests : IAsyncLifetime
{
    private const int Total = 10_000;

    public Task InitializeAsync()
    {
        TestCounters.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task 一万事件并发发布_恰好送达一次()
    {
        await using var host = await EventHost.StartAsync(capacity: 2_048, concurrency: 4);
        var publishTasks = Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            for (var i = worker; i < Total; i += 8)
            {
                await host.Publisher.PublishAsync("stress:load", $"evt-{i}");
            }
        }));
        await Task.WhenAll(publishTasks);
        await EventHost.UntilAsync(
            () => TestCounters.Received.Values.Sum() == Total,
            "15 秒内未消费完全部事件",
            timeout: TimeSpan.FromSeconds(60));

        // 恰好一次:每个事件 ID 的计数必须为 1
        Assert.Equal(Total, TestCounters.Received.Count);
        Assert.All(TestCounters.Received.Values, count => Assert.Equal(1, count));
    }
}

/// <summary>属性测试:重试不变量——对任意失败次数 k 与重试配置 r,直接打真实重试环:
/// 当且仅当 r≥k 时最终成功;尝试次数恒等于 min(k, r+1);白名单外异常永不重试。</summary>
public class RetryPropertyTests : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    [Property]
    public async Task 重试不变量(byte failTimesRaw, byte retriesRaw)
    {
        var failTimes = failTimesRaw % 6; // 0..5
        var retries = retriesRaw % 6;     // 0..5
        var attempts = 0;

        var error = await EventRetryLoop.ExecuteAsync(
            numRetries: retries,
            retryTimeoutMs: 0,
            exceptionTypes: null,
            TimeProvider.System,
            () =>
            {
                attempts++;
                if (attempts <= failTimes)
                {
                    throw new InvalidOperationException($"第 {attempts} 次失败");
                }
                return Task.CompletedTask;
            });

        var delivered = error is null;
        var expectedDelivered = retries >= failTimes;
        // 尝试次数:失败 k≤r 时 = k+1(k 次失败后第 k+1 次成功);k>r 时 = r+1(额度用尽)——统一为 min(k,r)+1
        var expectedAttempts = Math.Min(failTimes, retries) + 1;

        Assert.True(delivered == expectedDelivered, $"送达({delivered})应等于 r≥k({expectedDelivered}) [k={failTimes}, r={retries}]");
        Assert.Equal(expectedAttempts, attempts);
    }

    [Property]
    public void 白名单外异常立即失败不重试(byte retriesRaw)
    {
        var retries = retriesRaw % 6;
        var attempts = 0;
        var error = EventRetryLoop.ExecuteAsync(
            numRetries: retries,
            retryTimeoutMs: 0,
            exceptionTypes: [typeof(ArgumentException)],
            TimeProvider.System,
            () =>
            {
                attempts++;
                throw new InvalidOperationException("白名单外");
            }).GetAwaiter().GetResult();

        Assert.NotNull(error);
        Assert.Equal(1, attempts);
    }
}

/// <summary>SSS+ 审核加固测试:毒事件隔离、取消语义、单播、确定性顺序、停机防护、监视器旁路。</summary>
public class SssPlusHardeningTests : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        TestCounters.Reset();
        PoisonSubscriber.Activated = false;
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task 毒事件不杀总线_后续事件继续处理()
    {
        await using var host = await EventHost.StartAsync();
        await host.Publisher.PublishAsync("poison:bad");   // OutOfMemoryException 级别的毒事件
        await EventHost.UntilAsync(() => PoisonSubscriber.Activated, "毒事件未被消费");
        await host.Publisher.PublishAsync("poison:after"); // 总线必须还活着
        await EventHost.UntilAsync(() => TestCounters.Received.GetValueOrDefault("poison:after") == 1, "毒事件后总线已死");
    }

    [Fact]
    public async Task 取消异常永不重试_重试延迟尊重取消()
    {
        var attempts = 0;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var error = await EventRetryLoop.ExecuteAsync(
            numRetries: 5,
            retryTimeoutMs: 10_000, // 若不尊重取消,此延迟会挂 10 秒
            exceptionTypes: null,
            TimeProvider.System,
            () =>
            {
                attempts++;
                throw new OperationCanceledException(cts.Token);
            },
            cancellationToken: cts.Token);

        Assert.IsType<OperationCanceledException>(error);
        Assert.Equal(1, attempts); // 取消即终态:一次都不重试
    }

    [Fact]
    public async Task 单播模式_仅排序最前的订阅执行()
    {
        await using var host = await EventHost.StartAsync(unicast: true);
        await host.Publisher.PublishAsync("order:stable");
        await EventHost.UntilAsync(
            () => TestCounters.Received.GetValueOrDefault("order:stable:Aa") == 1,
            "单播应执行排序最前的 AaOrderSubscriber");
        await Task.Delay(200);
        Assert.Equal(0, TestCounters.Received.GetValueOrDefault("order:stable:Zz")); // Zz 不执行
    }

    [Fact]
    public async Task 广播模式_订阅执行顺序确定()
    {
        await using var host = await EventHost.StartAsync(unicast: false);
        for (var i = 0; i < 20; i++)
        {
            await host.Publisher.PublishAsync("order:stable");
        }
        await EventHost.UntilAsync(
            () => TestCounters.Received.GetValueOrDefault("order:stable:Aa") == 20
               && TestCounters.Received.GetValueOrDefault("order:stable:Zz") == 20,
            "广播应送达两个订阅者各 20 次");
        var order = TestCounters.ExecutedOrder.Where(o => o.EndsWith("OrderSubscriber.Handle")).ToList();
        Assert.Equal(40, order.Count);
        // 每次 Aa 在 Zz 前(SortKey 确定性:并发=1 时同一事件的订阅按序执行)
        for (var i = 0; i < 40; i += 2)
        {
            Assert.StartsWith("Aa", order[i], StringComparison.Ordinal);
            Assert.StartsWith("Zz", order[i + 1], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task 宿主未启动即停机_不抛异常_剩余计数丢弃()
    {
        // 构建但不 StartAsync:注册表从未构建,StopAsync 走安全丢弃路径
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddNexMeshEvents(builder.Configuration, subscriberAssemblies: typeof(EventHost).Assembly);
        using var host = builder.Build();
        var publisher = host.Services.GetRequiredService<IEventPublisher>();
        await publisher.PublishAsync("never:started", "payload"); // Channel 在 DI 层,发布成功

        await host.StopAsync(); // 不得抛 NullReferenceException
    }

    [Fact]
    public async Task 监视器自身异常旁路静默_事件仍送达()
    {
        await using var host = await EventHost.StartAsync(
            configureServices: s => s.AddSingleton<IEventMonitor, ThrowingMonitor>());
        await host.Publisher.PublishAsync("route:exact");
        await EventHost.UntilAsync(() => TestCounters.Received.GetValueOrDefault("route:exact") == 1, "监视器故障不应影响事件处理");
    }
}

/// <summary>二轮 SSS+ 审核加固测试:正则回溯炸弹隔离、停机封口、排干新鲜令牌、注册幂等。</summary>
public class SecondPassHardeningTests : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        TestCounters.Reset();
        RegexBombSubscriber.Handled = 0;
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task 正则回溯炸弹_匹配超时不挂死_总线继续()
    {
        await using var host = await EventHost.StartAsync();
        // 40 个 a + 结尾 b:对 ^(a+)+$ 是指数级回溯输入;无超时会挂死消费者(停机都关不掉)
        await host.Publisher.PublishAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" + "b");
        await host.Publisher.PublishAsync("poison:after"); // 总线必须还活着
        await EventHost.UntilAsync(
            () => TestCounters.Received.GetValueOrDefault("poison:after") == 1,
            "正则回溯炸弹挂死了总线(或匹配未超时)");
        Assert.Equal(0, RegexBombSubscriber.Handled); // 炸弹输入被隔离丢弃,未误命中
    }

    [Fact]
    public async Task 停机封口_停机后发布方收到明确异常而非永久悬挂()
    {
        var host = await EventHost.StartAsync();
        await host.Publisher.PublishAsync("route:exact");
        await host.StopInnerAsync(); // 停机:排干 + writer.TryComplete

        var publish = host.Publisher.PublishAsync("after:shutdown");
        var finished = await Task.WhenAny(publish, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(publish, finished); // 3 秒内必须结束(而非无限悬挂)
        await Assert.ThrowsAnyAsync<System.Threading.Channels.ChannelClosedException>(
            () => host.Publisher.PublishAsync("after:shutdown2"));
        await host.DisposeAsync();
    }

    [Fact]
    public async Task 停机排干_队列内事件用新鲜令牌成功处理()
    {
        var host = await EventHost.StartAsync(drainTimeoutSeconds: 5);
        // 用门闩占住消费者,让后续事件滞留队列;停机后放行,排干阶段用新鲜令牌处理队列事件
        GatedSubscriber.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await host.Publisher.PublishAsync("shutdown:gated");
        await EventHost.UntilAsync(() => TestCounters.Received.GetValueOrDefault("shutdown:gated") == 1, "门闩事件未进入处理");
        await host.Publisher.PublishAsync("stress:load", "排干-新鲜令牌1");
        await host.Publisher.PublishAsync("stress:load", "排干-新鲜令牌2");

        var stopTask = host.StopInnerAsync();
        GatedSubscriber.Gate.SetResult(); // 放行门闩,让消费循环退出进入排干
        await stopTask.WaitAsync(TimeSpan.FromSeconds(10));
        await EventHost.UntilAsync(
            () => TestCounters.Received.GetValueOrDefault("排干-新鲜令牌1") == 1
               && TestCounters.Received.GetValueOrDefault("排干-新鲜令牌2") == 1,
            "排干阶段未用新鲜令牌完成队列事件");
        // 排干的两条不允许进故障通道(若用了取消令牌,支持取消的路径会报 OCE 故障)
        Assert.DoesNotContain(TestCounters.Faults, f => f.EventId == "stress:load");
        await host.DisposeAsync();
    }

    [Fact]
    public void 重复注册幂等_消费宿主唯一()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        services.AddNexMeshEvents(configuration, subscriberAssemblies: typeof(EventHost).Assembly);
        services.AddNexMeshEvents(configuration, subscriberAssemblies: typeof(EventHost).Assembly);
        services.AddNexMeshEvents(configuration, subscriberAssemblies: typeof(EventHost).Assembly);

        var hosted = services.Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)).ToList();
        Assert.Single(hosted);
    }
}

/// <summary>实证混沌测试:2000 事件 × 8 并发发布 + 真实故障注入 + 负载中停机——守恒断言:没有事件静默消失。</summary>
public class ChaosTests : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        TestCounters.Reset();
        ChaosSubscriber.Attempts.Clear();
        ChaosSubscriber.Delivered.Clear();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task 负载中停机_守恒_无事件静默消失()
    {
        const int workers = 8, perWorker = 250; // 2000 个事件
        var rejected = 0L; // 停机封口后收 ChannelClosedException 的发布(从未入队,不算)
        var accepted = 0L;
        var host = await EventHost.StartAsync(capacity: 512, concurrency: 4, drainTimeoutSeconds: 10,
            configureServices: s => s.AddSingleton<IEventFaultHandler, RecordingFaultHandler>());

        var stopping = Task.Delay(300); // 300ms 后负载中途停机(此时发布仍在进行)
        var publishAll = Task.WhenAll(Enumerable.Range(0, workers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < perWorker; i++)
            {
                // 每 3 个 1 个"首次失败",每 5 个 1 个"永远失败"
                var id = (i % 5 == 0 ? "X" : i % 3 == 0 ? "F" : "-") + $"-{w}-{i}";
                try
                {
                    await host.Publisher.PublishAsync("chaos:work", id);
                    Interlocked.Increment(ref accepted);
                }
                catch (System.Threading.Channels.ChannelClosedException)
                {
                    Interlocked.Increment(ref rejected); // 停机封口:明确拒绝,非静默丢失
                }
                if (i % 50 == 0) await Task.Delay(5); // 拉长发布窗口,让停机落在负载中间
            }
        })));

        await stopping;
        var stopTask = host.StopInnerAsync(); // 负载中真实停机:取消消费→新鲜令牌排干→封口
        await publishAll;
        await stopTask.WaitAsync(TimeSpan.FromSeconds(30));
        await host.DisposeAsync();

        // ═══ 守恒律:入队成功的每一个事件,要么送达,要么走完重试进故障通道——不允许第三种下落 ═══
        var chaosFaults = TestCounters.Faults.Count(f => f.EventId == "chaos:work");
        var total = 2000L;
        Assert.Equal(total, accepted + rejected); // 发布侧账目:要么入队,要么被明确拒绝
        Assert.Equal(accepted, (long)ChaosSubscriber.Delivered.Count + chaosFaults); // 守恒:无静默消失

        // ═══ 重试行为实证:各类型尝试次数精确 ═══
        var fIds = ChaosSubscriber.Attempts.Where(kv => kv.Key.StartsWith("F")).ToList();
        var xIds = ChaosSubscriber.Attempts.Where(kv => kv.Key.StartsWith("X")).ToList();
        var plainIds = ChaosSubscriber.Attempts.Where(kv => kv.Key.StartsWith("-")).ToList();
        Assert.All(fIds, kv => Assert.True(kv.Value == 2, $"F 类应恰好 2 次(失败+重试成功),{kv.Key}={kv.Value}"));
        Assert.All(xIds, kv => Assert.Equal(3, kv.Value));  // X 类 = NumRetries+1 = 3 次耗尽
        Assert.All(plainIds, kv => Assert.Equal(1, kv.Value)); // 普通类 1 次成功
        Assert.Equal(fIds.Count, fIds.Count(kv => ChaosSubscriber.Delivered.ContainsKey(kv.Key))); // F 类全部最终送达
        Assert.DoesNotContain(ChaosSubscriber.Delivered, kv => kv.Key.StartsWith("X")); // X 类零送达
    }
}

/// <summary>实证可观测性:真实 MeterListener 收到发布计数(不是"理论上会发出")。</summary>
public class MetricsEmissionTests : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task 发布计数真实到达指标监听器()
    {
        var totals = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "nexmesh.events")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            totals.AddOrUpdate(instrument.Name, value, (_, current) => current + value));
        listener.Start();

        await using var host = await EventHost.StartAsync();
        for (var i = 0; i < 10; i++)
        {
            await host.Publisher.PublishAsync("metrics:probe");
        }
        await EventHost.UntilAsync(
            () => totals.GetValueOrDefault("nexmesh.events.published") >= 10,
            "15 秒内未观察到 ≥10 次真实发布计数");

        await host.Publisher.PublishAsync("chaos:retry-probe"); // 无订阅事件也计数
        await EventHost.UntilAsync(
            () => totals.GetValueOrDefault("nexmesh.events.published") >= 11,
            "无订阅事件也应计入发布计数");
    }
}
