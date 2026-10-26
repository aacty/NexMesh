using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexMesh.Core;
using NexMesh.Core.Context;
using NexMesh.Worker;

namespace NexMesh.Worker.Tests;

public class CronJobTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    /// <summary>每 50ms 一轮的测试作业:计数、记录系统身份、可注入失败。</summary>
    private sealed class CountingJob(IServiceProvider sp) : NexMeshIntervalJob(TimeSpan.FromMilliseconds(50), sp)
    {
        public static int Runs;
        public static string? LastOperator;
        public static bool FailNext;

        protected override Task RunOnceAsync(IServiceProvider scoped, CancellationToken ct)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("planned failure");
            }
            Interlocked.Increment(ref Runs);
            LastOperator = scoped.GetRequiredService<ICurrentUser>().UserId;
            return Task.CompletedTask;
        }
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexMesh();
        services.AddSingleton<IHostApplicationLifetime>(new FakeLifetime());
        return services.BuildServiceProvider();
    }

    private static async Task<int> RunJobForAsync(TimeSpan duration)
    {
        var provider = BuildProvider();
        var job = new CountingJob(provider);
        using var cts = new CancellationTokenSource(duration);
        var task = job.StartAsync(cts.Token);
        try
        {
            await Task.Delay(duration, CancellationToken.None);
        }
        finally
        {
            await job.StopAsync(CancellationToken.None);
        }
        await task;
        return CountingJob.Runs;
    }

    [Fact]
    public async Task 周期作业_按周期触发_系统身份贯通()
    {
        CountingJob.Runs = 0;
        await RunJobForAsync(TimeSpan.FromMilliseconds(600));

        Assert.True(CountingJob.Runs >= 5, $"触发次数不足: {CountingJob.Runs}");
        Assert.Equal("system:CountingJob", CountingJob.LastOperator); // ICurrentUser 读到系统身份快照
    }

    [Fact]
    public async Task 单轮异常_调度不停摆()
    {
        CountingJob.Runs = 0;
        CountingJob.FailNext = true;

        var provider = BuildProvider();
        var job = new CountingJob(provider);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var task = job.StartAsync(cts.Token);
        await Task.Delay(400, CancellationToken.None);
        await job.StopAsync(CancellationToken.None);
        await task;

        Assert.True(CountingJob.Runs >= 3, $"异常后续轮未执行: {CountingJob.Runs}"); // 首轮抛异常,后续继续
    }

    [Fact]
    public async Task 停机_在途轮取消且循环退出()
    {
        CountingJob.Runs = 0;
        var provider = BuildProvider();
        var job = new CountingJob(provider);
        await job.StartAsync(CancellationToken.None);
        await Task.Delay(150, CancellationToken.None);
        var before = CountingJob.Runs;

        await job.StopAsync(CancellationToken.None);
        await Task.Delay(200, CancellationToken.None);

        Assert.Equal(before, CountingJob.Runs); // Stop 后不再触发
    }

    [Fact]
    public void Cron表达式_下一次触发对齐()
    {
        // 固定时区(CI 是 UTC,本地是 UTC+8):断言不依赖运行环境
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        var now = new DateTimeOffset(2026, 10, 23, 10, 7, 30, tz.GetUtcOffset(DateTime.Now));
        var cron = Cronos.CronExpression.Parse("*/5 * * * *", Cronos.CronFormat.Standard);
        var next = cron.GetNextOccurrence(now, tz)!.Value;

        Assert.Equal(0, next.Minute % 5);
        // 转回目标时区再断言(不依赖运行环境的本地时区)
        var localNext = TimeZoneInfo.ConvertTime(next, tz);
        Assert.Equal(10, localNext.Hour);
    }
}

/// <summary>手动触发(Dashboard 操作面):热执行/忙碌互斥/执行记录。</summary>
public class ManualTriggerTests
{
    private sealed class FakeLifetime2 : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static ServiceProvider ManualProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNexMesh();
        services.AddSingleton<IHostApplicationLifetime>(new FakeLifetime2());
        return services.BuildServiceProvider();
    }

    private sealed class SlowJob(IServiceProvider sp) : NexMeshIntervalJob(TimeSpan.FromHours(1), sp)
    {
        public static int Runs;
        private readonly IServiceProvider _sp = sp;
        protected override Task RunOnceAsync(IServiceProvider scoped, CancellationToken ct)
        {
            Interlocked.Increment(ref Runs);
            return Task.Delay(800, ct); // 模拟长作业
        }
    }

    [Fact]
    public async Task TriggerNow_立即执行一轮_并记录manual来源()
    {
        var services = new ServiceCollection();
        services.AddNexMesh();
        var sp = ManualProvider();
        var job = new SlowJob(sp);
        SlowJob.Runs = 0;

        var triggered = await job.TriggerNowAsync();
        Assert.True(triggered);
        Assert.Equal(1, SlowJob.Runs);
        Assert.Single(job.RecentRuns);
        Assert.Equal("manual", job.RecentRuns[0].Trigger);
        Assert.True(job.RecentRuns[0].Ok);
    }

    [Fact]
    public async Task 执行中再次触发_返回忙碌false_不重入()
    {
        var services = new ServiceCollection();
        services.AddNexMesh();
        var sp = ManualProvider();
        var job = new SlowJob(sp);
        SlowJob.Runs = 0;

        var first = job.TriggerNowAsync(); // 800ms 执行中
        await Task.Delay(100);
        var busy = await job.TriggerNowAsync(); // 闸被占用 → false
        Assert.False(busy);
        await first;
        Assert.Equal(1, SlowJob.Runs); // 无重入
    }

    [Fact]
    public async Task 失败执行_记录失败与来源()
    {
        var services = new ServiceCollection();
        services.AddNexMesh();
        var sp = ManualProvider();
        var failing = new FailingJob(sp);
        await Assert.ThrowsAnyAsync<Exception>(() => failing.TriggerNowAsync());
        Assert.Single(failing.RecentRuns);
        Assert.False(failing.RecentRuns[0].Ok);
        Assert.Equal("manual", failing.RecentRuns[0].Trigger);
        Assert.NotNull(failing.RecentRuns[0].Error);
    }

    private sealed class FailingJob(IServiceProvider sp) : NexMeshIntervalJob(TimeSpan.FromHours(1), sp)
    {
        protected override Task RunOnceAsync(IServiceProvider scoped, CancellationToken ct)
            => throw new InvalidOperationException("演练失败");
    }
}
