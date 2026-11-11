namespace NexMesh.Worker.Tests;

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexMesh.Core;
using NexMesh.Core.Context;
using NexMesh.Worker;

/// <summary>
/// 攻击面 E(框架架构师 E,资源与泄漏,第二轮交叉测试):Worker 调度韧性。
/// 覆盖既有测试的缺口:Cron 作业(NexMeshCronJob)此前零异常隔离/停机收场测试
/// (CronJobTests 只覆盖 Interval 类与表达式解析);Interval 作业无执行超时重叠防护测试;
/// 停机时在途作业的取消语义无测试。
/// </summary>
public class EJobResilienceTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexMesh();
        services.AddSingleton<IHostApplicationLifetime>(new FakeLifetime());
        return services.BuildServiceProvider();
    }

    // ═══════════════════════════ Cron 作业:异常风暴不打死调度轮 ═══════════════════════════

    /// <summary>每秒一轮(秒级 Cron),每轮必炸:验证调度轮在连续异常下继续滚动。</summary>
    private sealed class CrashingCronJob(IServiceProvider sp)
        : NexMeshCronJob("*/1 * * * * *", runOnStart: true, includeSeconds: true, sp)
    {
        public static int Attempts;

        protected override Task RunOnceAsync(IServiceProvider scoped, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref Attempts);
            throw new InvalidOperationException($"每轮都炸 #{n}");
        }
    }

    [Fact]
    public async Task E_Cron作业_每轮异常风暴_调度轮不停摆()
    {
        CrashingCronJob.Attempts = 0;
        var provider = BuildProvider();
        try
        {
            var job = new CrashingCronJob(provider);
            await job.StartAsync(CancellationToken.None);
            await Task.Delay(3000, CancellationToken.None); // runOnStart 1 轮 + 秒级调度 2~3 轮
            var attemptsAfter3s = CrashingCronJob.Attempts;

            Assert.True(attemptsAfter3s >= 3, $"3 秒内仅执行 {attemptsAfter3s} 轮:异常打死了 Cron 调度轮");
            Assert.All(job.RecentRuns, r => Assert.False(r.Ok)); // 每轮都记录为失败(面板速览)
            Assert.All(job.RecentRuns, r => Assert.Equal("scheduled", r.Trigger));

            await job.StopAsync(CancellationToken.None);
            await Task.Delay(500, CancellationToken.None);
            Assert.Equal(attemptsAfter3s, CrashingCronJob.Attempts); // 停机后不再触发
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    // ═══════════════════════════ Cron 作业:停机时在途轮收场 ═══════════════════════════

    private sealed class HangingCronJob(IServiceProvider sp)
        : NexMeshCronJob("* * * * * *", runOnStart: true, includeSeconds: true, sp)
    {
        public static TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static bool CancelObserved;

        protected override async Task RunOnceAsync(IServiceProvider scoped, CancellationToken ct)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct); // 模拟 30 秒长作业,响应取消
            }
            catch (OperationCanceledException)
            {
                CancelObserved = true;
            }
        }
    }

    [Fact]
    public async Task E_Cron作业_停机收场_在途轮收到取消_StopAsync限时返回()
    {
        HangingCronJob.Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HangingCronJob.CancelObserved = false;
        var provider = BuildProvider();
        try
        {
            var job = new HangingCronJob(provider);
            await job.StartAsync(CancellationToken.None);
            await HangingCronJob.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); // 首轮在途

            var sw = Stopwatch.StartNew();
            await job.StopAsync(CancellationToken.None);
            sw.Stop();

            Assert.True(HangingCronJob.CancelObserved, "停机时在途轮未收到取消令牌");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
                $"StopAsync 耗时 {sw.Elapsed.TotalMilliseconds:F0}ms:停机未收场在途作业");
            Assert.DoesNotContain(job.RecentRuns, r => !r.Ok); // 优雅停机的在途轮不计失败
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    // ═══════════════════════════ Cron 作业:停机窗口的调度竞态 ═══════════════════════════

    /// <summary>
    /// [审读疑点 → 实证为安全] 静态审读疑似缺口:RunIsolatedAsync 的 _runGate.WaitAsync(0, stoppingToken)
    /// 在 try 之外(ExecuteAsync 对 RunIsolatedAsync 的调用亦无 try-catch),且 Interval 版同类场景
    /// 整段被 try-catch 包住——疑为停机窗口 OCE 逸出点。实弹推翻:①调度轮的 Task.Delay(delay, stoppingToken)
    /// 先行抛 OCE 并 break,几乎不可能在取消后仍进入 RunIsolatedAsync;②即便进入,闸立即可得时
    /// SemaphoreSlim.WaitAsync(0, token) 走快速路径不检查取消令牌。真实竞态窗口(gate 被手动触发占住
    /// 且取消恰好落在 Delay 完成之后)不可稳定构造,判定不可达;本测试实证启动即停机场景优雅退出。
    /// </summary>
    [Fact]
    public async Task E_Cron作业_停机令牌已取消时启动_调度轮优雅退出_无OCE逸出()
    {
        var provider = BuildProvider();
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel(); // 停机窗口:ExecuteAsync 启动瞬间令牌已取消

            var job = new CrashOnStartCronJob(provider);
            var ex = await Record.ExceptionAsync(() => job.StartAsync(cts.Token));
            Assert.Null(ex); // [实证] 无 OCE 逸出:executeTask 优雅完成,宿主不会记录 BackgroundService 失败
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    private sealed class CrashOnStartCronJob(IServiceProvider sp)
        : NexMeshCronJob("* * * * * *", runOnStart: true, includeSeconds: true, sp)
    {
        protected override Task RunOnceAsync(IServiceProvider scoped, CancellationToken ct) => Task.CompletedTask;
    }

    // ═══════════════════════════ Interval 作业:执行超时与下轮重叠防护 ═══════════════════════════

    private sealed class OverlapProbeJob(IServiceProvider sp) : NexMeshIntervalJob(TimeSpan.FromMilliseconds(100), sp)
    {
        private static readonly object PeakLock = new();
        public static int Concurrent;
        public static int PeakConcurrent;
        public static long CompletedRuns;

        protected override async Task RunOnceAsync(IServiceProvider scoped, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref Concurrent);
            lock (PeakLock)
            {
                if (now > PeakConcurrent)
                {
                    PeakConcurrent = now;
                }
            }
            try
            {
                await Task.Delay(250, ct); // 执行时长(250ms)> 周期(100ms):必然跨越多个调度点
            }
            finally
            {
                Interlocked.Decrement(ref Concurrent);
                Interlocked.Increment(ref CompletedRuns);
            }
        }
    }

    [Fact]
    public async Task E_Interval作业_执行时长超过周期_下轮不重叠_并发峰值恒为1()
    {
        OverlapProbeJob.Concurrent = 0;
        OverlapProbeJob.PeakConcurrent = 0;
        OverlapProbeJob.CompletedRuns = 0;
        var provider = BuildProvider();
        try
        {
            var job = new OverlapProbeJob(provider);
            await job.StartAsync(CancellationToken.None);
            await Task.Delay(1500, CancellationToken.None);
            await job.StopAsync(CancellationToken.None);

            Assert.True(OverlapProbeJob.CompletedRuns >= 3,
                $"1.5 秒内仅完成 {OverlapProbeJob.CompletedRuns} 轮(周期 100ms/执行 250ms):调度未滚动");
            Assert.Equal(1, OverlapProbeJob.PeakConcurrent); // 执行闸防护:同一作业绝不并发执行
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    // ═══════════════════════════ Interval 作业:停机收场 ═══════════════════════════

    private sealed class HangingIntervalJob(IServiceProvider sp) : NexMeshIntervalJob(TimeSpan.FromMilliseconds(50), sp)
    {
        public static TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static bool CancelObserved;
        public static long Runs;

        protected override async Task RunOnceAsync(IServiceProvider scoped, CancellationToken ct)
        {
            Interlocked.Increment(ref Runs);
            Started.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException)
            {
                CancelObserved = true;
            }
        }
    }

    [Fact]
    public async Task E_Interval作业_停机收场_在途轮取消且停机后不再触发()
    {
        HangingIntervalJob.Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HangingIntervalJob.CancelObserved = false;
        HangingIntervalJob.Runs = 0;
        var provider = BuildProvider();
        try
        {
            var job = new HangingIntervalJob(provider);
            await job.StartAsync(CancellationToken.None);
            await HangingIntervalJob.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var runsAtStop = Interlocked.Read(ref HangingIntervalJob.Runs);

            var sw = Stopwatch.StartNew();
            await job.StopAsync(CancellationToken.None);
            sw.Stop();

            Assert.True(HangingIntervalJob.CancelObserved, "停机时在途轮未收到取消令牌");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
                $"StopAsync 耗时 {sw.Elapsed.TotalMilliseconds:F0}ms:停机未收场在途作业");

            await Task.Delay(300, CancellationToken.None); // 停机后周期本应再触发,必须不触发
            Assert.Equal(runsAtStop, Interlocked.Read(ref HangingIntervalJob.Runs));
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }
}
