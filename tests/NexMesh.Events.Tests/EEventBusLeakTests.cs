namespace NexMesh.Events.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// ═══════════════════════════════ E 专属订阅者(事件 ID "e:" 空间隔离)═════════════════════════════

public sealed class EFloodSubscriber : IEventSubscriber
{
    public static long FloodHandled;

    [EventSubscribe("e:flood")]
    public Task OnFlood(EventContext context)
    {
        Interlocked.Increment(ref FloodHandled);
        return Task.CompletedTask;
    }
}

/// <summary>异常风暴源:NumRetries=0,每次执行必抛——验证风暴不杀宿主且 dropped 计数守恒。</summary>
public sealed class EStormSubscriber : IEventSubscriber
{
    public static int Handled;

    [EventSubscribe("e:storm")]
    public Task OnStorm(EventContext context)
    {
        Interlocked.Increment(ref Handled);
        throw new InvalidOperationException($"风暴第 {context.Payload} 连击");
    }
}

/// <summary>业务自身抛 OperationCanceledException(非停机令牌):应走"非预期分发故障"隔离,不杀循环。</summary>
public sealed class EBusinessOceSubscriber : IEventSubscriber
{
    public static int Handled;

    [EventSubscribe("e:oce")]
    public Task OnOce(EventContext context)
    {
        Interlocked.Increment(ref Handled);
        throw new OperationCanceledException("业务自行抛出的取消信号(非停机令牌)");
    }
}

/// <summary>停机排干用:区分"支持取消的处理器"与"不响应取消的处理器"在排干阶段的真实命运。</summary>
public sealed class EDrainSubscriber : IEventSubscriber
{
    /// <summary>排干分发的处理器令牌在进入时是否已取消(实证"新鲜令牌被停机污染")。</summary>
    public static bool SlowTokenCanceledAtEntry;
    public static int AfterHandled;
    public static int BlockHandled;

    [EventSubscribe("e:drain:slow")]
    public async Task OnSlow(EventContext context)
    {
        SlowTokenCanceledAtEntry = context.CancellationToken.IsCancellationRequested;
        await Task.Delay(TimeSpan.FromSeconds(30), context.CancellationToken); // 支持取消
    }

    [EventSubscribe("e:drain:after")]
    public Task OnAfter(EventContext context)
    {
        Interlocked.Increment(ref AfterHandled);
        return Task.CompletedTask;
    }

    /// <summary>不响应取消的处理器(不把令牌传给 Delay):排干限时对它是否有效——实证点。</summary>
    [EventSubscribe("e:drain:block")]
    public async Task OnBlock(EventContext context)
    {
        Interlocked.Increment(ref BlockHandled);
        await Task.Delay(TimeSpan.FromSeconds(3)); // 无视取消
    }
}

/// <summary>E 专属指标探针:订阅 nexmesh.events 的 dropped/published 计数器,按 tag 分组累积(顺序执行测试内互不污染)。</summary>
internal sealed class EEventBusProbe : IDisposable
{
    public sealed record DropKey(string EventId, string Reason);

    private readonly MeterListener _listener = new();

    public ConcurrentDictionary<DropKey, long> Dropped { get; } = new();
    public ConcurrentDictionary<string, long> Published { get; } = new();

    public EEventBusProbe()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "nexmesh.events"
                && instrument.Name is "nexmesh.events.dropped" or "nexmesh.events.published")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var eventId = string.Empty;
            var reason = string.Empty;
            foreach (var tag in tags)
            {
                if (tag.Key == "event_id")
                {
                    eventId = tag.Value as string ?? string.Empty;
                }
                else if (tag.Key == "reason")
                {
                    reason = tag.Value as string ?? string.Empty;
                }
            }
            if (instrument.Name == "nexmesh.events.dropped")
            {
                Dropped.AddOrUpdate(new DropKey(eventId, reason), value, (_, c) => c + value);
            }
            else
            {
                Published.AddOrUpdate(eventId, value, (_, c) => c + value);
            }
        });
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();
}

// ═══════════════════════════════ 发布侧取消令牌语义 ══════════════════════════════

public class EBackpressureCancellationTests
{
    [Fact]
    public async Task E_已取消令牌发布_立即失败_不入队不计数()
    {
        await using var host = await EventHost.StartAsync();
        using var probe = new EEventBusProbe();
        EFloodSubscriber.FloodHandled = 0;

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.Publisher.PublishAsync("e:cancel", "载荷", cts.Token));

        Assert.Equal(0, probe.Published.GetValueOrDefault("e:cancel")); // 取消的发布未入队、未计发布数

        await host.Publisher.PublishAsync("e:flood"); // 对照:总线照常工作
        await EventHost.UntilAsync(() => EFloodSubscriber.FloodHandled == 1, "取消发布后总线应照常工作");
    }

    [Fact]
    public async Task E_回压等待中取消_令牌被尊重_未入队守恒()
    {
        GatedSubscriber.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TestCounters.Reset();
        EFloodSubscriber.FloodHandled = 0;
        await using var host = await EventHost.StartAsync(capacity: 1);
        using var probe = new EEventBusProbe();
        var channel = host.Services.GetRequiredService<Channel<EventWorkItem>>();

        await host.Publisher.PublishAsync("shutdown:gated"); // 第 1 条:占住唯一消费者
        await EventHost.UntilAsync(() => TestCounters.Received.GetValueOrDefault("shutdown:gated") == 1, "门闩未占用消费者");
        await host.Publisher.PublishAsync("e:flood");        // 第 2 条:占满容量 1 的队列
        Assert.Equal(1, channel.Reader.Count);

        using var cts = new CancellationTokenSource();
        var blocked = host.Publisher.PublishAsync("e:flood", null, cts.Token); // 第 3 条:回压挂起
        await Task.Delay(300);
        Assert.False(blocked.IsCompleted, "发布端未进入回压等待");

        cts.Cancel(); // 回压等待中取消:必须立即失败,不占队列
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        Assert.Equal(1, probe.Published.GetValueOrDefault("e:flood")); // 仅第 2 条计入发布数

        GatedSubscriber.Gate.SetResult(); // 放行
        await EventHost.UntilAsync(() => EFloodSubscriber.FloodHandled == 1, "已入队事件未送达");
        await EventHost.UntilAsync(() => channel.Reader.Count == 0, "队列未清空");

        // 守恒:取消的发布(0 入队)不产生任何 dropped;已入队的 1 条恰好送达 1 次
        Assert.Empty(probe.Dropped);
        await host.Publisher.PublishAsync("e:flood"); // 总线健康
        await EventHost.UntilAsync(() => EFloodSubscriber.FloodHandled == 2, "取消后总线应存活");
    }
}

// ═══════════════════════════════ 订阅者异常风暴 ══════════════════════════════

public class EStormTests
{
    [Fact]
    public async Task E_异常风暴百连击_总线存活_dropped计数守恒()
    {
        await using var host = await EventHost.StartAsync();
        using var probe = new EEventBusProbe();
        EStormSubscriber.Handled = 0;
        EFloodSubscriber.FloodHandled = 0;

        const int storms = 100;
        for (var i = 0; i < storms; i++)
        {
            await host.Publisher.PublishAsync("e:storm", i);
        }
        await EventHost.UntilAsync(() => EStormSubscriber.Handled == storms, "风暴连击未被全部执行");

        // 三本账对齐:发布 100 = 执行 100 = 弃置 100(reason=retries-exhausted)
        Assert.Equal(storms, probe.Published.GetValueOrDefault("e:storm"));
        Assert.Equal(storms, probe.Dropped.GetValueOrDefault(new EEventBusProbe.DropKey("e:storm", "retries-exhausted")));
        Assert.Equal(0, probe.Published.GetValueOrDefault("e:flood"));

        await host.Publisher.PublishAsync("e:flood"); // 风暴后总线必须活着
        await EventHost.UntilAsync(() => EFloodSubscriber.FloodHandled == 1, "异常风暴打死了总线");
    }

    [Fact]
    public async Task E_业务自行抛OCE_被计为非预期分发故障_总线存活()
    {
        await using var host = await EventHost.StartAsync();
        using var probe = new EEventBusProbe();
        EBusinessOceSubscriber.Handled = 0;
        EFloodSubscriber.FloodHandled = 0;

        await host.Publisher.PublishAsync("e:oce");
        await EventHost.UntilAsync(() => EBusinessOceSubscriber.Handled == 1, "业务 OCE 事件未被消费");
        await EventHost.UntilAsync(
            () => probe.Dropped.GetValueOrDefault(new EEventBusProbe.DropKey("e:oce", "retries-exhausted")) == 1,
            "业务 OCE 应被隔离并计入丢弃");

        // [实证] 业务自抛 OCE(非停机令牌)被 EventRetryLoop 视为控制流取消:
        // 终态返回、绝不重试(Handled==1),计入 retries-exhausted 而非 unexpected-dispatch-failure
        Assert.Equal(1, EBusinessOceSubscriber.Handled);

        await host.Publisher.PublishAsync("e:flood");
        await EventHost.UntilAsync(() => EFloodSubscriber.FloodHandled == 1, "业务 OCE 打死了总线");
    }
}

// ═══════════════════════════════ 停机排干限时 ══════════════════════════════

public class EDrainLimitTests : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        TestCounters.Reset();
        GatedSubscriber.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EDrainSubscriber.SlowTokenCanceledAtEntry = false;
        EDrainSubscriber.AfterHandled = 0;
        EDrainSubscriber.BlockHandled = 0;
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// [P2 发现·实证] 排干"新鲜令牌"被宿主停机污染:
    /// Host.StopAsync 先同步触发 ApplicationStopping,再调用各 IHostedService.StopAsync——
    /// 因此排干阶段经 INexMeshBackgroundRunner 五步纪律创建的联合令牌
    /// (drainCts.Token + lifetime.ApplicationStopping)在创建瞬间就已取消。
    /// 支持取消的处理器在排干阶段必然立即 OCE,且因 drainCts 尚未超时被计为
    /// "非预期分发故障"而非优雅跳过。StopAsync 注释宣称的"排干用新鲜令牌限时处理"
    /// 实际只对忽略令牌的处理器成立。
    /// </summary>
    [Fact]
    public async Task E_停机排干_支持取消的处理器拿到的新鲜令牌已被停机污染()
    {
        var host = await EventHost.StartAsync(drainTimeoutSeconds: 5, // 长限时:排除 drainCts 超时干扰,纯测污染
            configureServices: s => s.AddSingleton<IEventFaultHandler, RecordingFaultHandler>());
        using var probe = new EEventBusProbe();
        GatedSubscriber.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EDrainSubscriber.SlowTokenCanceledAtEntry = false;
        EDrainSubscriber.AfterHandled = 0;

        await host.Publisher.PublishAsync("shutdown:gated"); // 占住消费者,让后续事件滞留队列
        await EventHost.UntilAsync(() => TestCounters.Received.GetValueOrDefault("shutdown:gated") == 1, "门闩事件未进入处理");
        await host.Publisher.PublishAsync("e:drain:slow");
        await host.Publisher.PublishAsync("e:drain:after");

        var stopTask = host.StopInnerAsync();
        GatedSubscriber.Gate.SetResult(); // 放行 → 消费循环退出 → 进入排干
        await stopTask.WaitAsync(TimeSpan.FromSeconds(15));

        // 【修复反转】RunDrainAsync 不联动宿主停机令牌:支持取消的处理器入口拿到新鲜令牌(核心断言),
        // 30 秒任务由 5 秒排干限时在到点后取消(限时语义从"不可达"变为可达)→ OCE 终态化;
        // 排干顺序中止,e:drain:after 未分发(随 shutdown 丢弃)
        Assert.False(EDrainSubscriber.SlowTokenCanceledAtEntry, "排干阶段处理器入口令牌不应被宿主停机污染(修复点)");
        Assert.Equal(1, probe.Dropped.GetValueOrDefault(new EEventBusProbe.DropKey("e:drain:slow", "retries-exhausted")));
        Assert.Equal(1, EDrainSubscriber.AfterHandled); // 重试环把超时 OCE 作控制流消化,排干继续完成 after
        await host.DisposeAsync();
    }

    /// <summary>
    /// [P3 发现·实证] 停机排干限时(DrainTimeoutSeconds)只对响应取消的处理器有效:
    /// 排干循环以 await DispatchSafeAsync 顺序执行,限时令牌不中断一次正在执行的 dispatch——
    /// 不响应取消的卡死处理器让"限时"形同虚设(本例 1 秒限时实际耗 3 秒+);
    /// 且由于上一测试实证的令牌污染,响应取消的处理器又必然立即失败,
    /// 于是 "drain-timeout" 弃置路径在真实停机序列中几乎不可达。
    /// </summary>
    [Fact]
    public async Task E_停机排干限时_对不响应取消的处理器无效()
    {
        var host = await EventHost.StartAsync(drainTimeoutSeconds: 1);
        using var probe = new EEventBusProbe();
        GatedSubscriber.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EDrainSubscriber.BlockHandled = 0;
        EDrainSubscriber.AfterHandled = 0;

        await host.Publisher.PublishAsync("shutdown:gated");
        await EventHost.UntilAsync(() => TestCounters.Received.GetValueOrDefault("shutdown:gated") == 1, "门闩事件未进入处理");
        await host.Publisher.PublishAsync("e:drain:block"); // 3 秒,不响应取消
        await host.Publisher.PublishAsync("e:drain:after");

        var sw = Stopwatch.StartNew();
        var stopTask = host.StopInnerAsync();
        GatedSubscriber.Gate.SetResult();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(20));
        sw.Stop();

        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(2.5),
            $"1 秒排干限时实际用时 {sw.Elapsed.TotalMilliseconds:F0}ms:限时被无视(对不响应取消的处理器无效)");
        Assert.Equal(1, EDrainSubscriber.BlockHandled); // 卡死的处理器还是被完整执行
        Assert.Equal(1, EDrainSubscriber.AfterHandled); // 后续事件也全部排干(未按限时弃置)
        await host.DisposeAsync();
    }
}

// ═══════════════════════════════ 长跑:10 万次发布 × 满载回压 ══════════════════════════════

public class EFloodTests
{
    [Fact]
    public async Task E_十万次发布_满载回压_水位有界_零丢弃_守恒()
    {
        const int total = 100_000;
        const int capacity = 8_192;
        TestCounters.Reset();
        EFloodSubscriber.FloodHandled = 0;

        var host = await EventHost.StartAsync(capacity: capacity, concurrency: 1);
        using var probe = new EEventBusProbe();
        var channel = host.Services.GetRequiredService<Channel<EventWorkItem>>();

        // 8 并发发布 × 单消费者:发布速率远高于消费速率 → 队列顶满 → 发布端回压等待(不丢不静默)
        var peakDepth = 0;
        var workers = Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            for (var i = w; i < total; i += 8)
            {
                await host.Publisher.PublishAsync("e:flood");
                if (i % 512 == 0)
                {
                    var depth = channel.Reader.Count;
                    var current = Volatile.Read(ref peakDepth);
                    if (depth > current)
                    {
                        Volatile.Write(ref peakDepth, depth);
                    }
                }
            }
        })).ToArray();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(120)); // 8×12500 全部入队(回压等待下完成)

        // 水位纪律:8 并发发布下峰值 ≤ 容量(Channel 有界 ⇒ 内存有界),且真实到达高水位(回压确实发生)
        var peak = Volatile.Read(ref peakDepth);
        Assert.InRange(peak, capacity / 4, capacity);

        await EventHost.UntilAsync(() => EFloodSubscriber.FloodHandled == total, "十万事件未在限期内消化完",
            TimeSpan.FromSeconds(180));

        // 守恒:发布 100000 = 送达 100000 + 丢弃 0;水位回落 0
        Assert.Equal(0, channel.Reader.Count);
        Assert.Equal(total, probe.Published.GetValueOrDefault("e:flood"));
        Assert.Empty(probe.Dropped); // 全程零丢弃(满载走回压等待,不静默丢)
        Assert.Equal(total, EFloodSubscriber.FloodHandled);

        await host.DisposeAsync();
    }
}
