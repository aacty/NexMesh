namespace NexMesh.Worker;

using Cronos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexMesh.Core;
using NexMesh.Core.Context;

/// <summary>
/// 定时作业基类(Cron 表达式,5 字段 分 时 日 月 周;含秒格式用重载指定)。
/// 纪律:单轮异常隔离(记录后继续下一轮,绝不停摆);退避等待(到点才醒,不忙轮询);
/// 优雅停机(stoppingToken);每轮经 BackgroundRunner 以系统身份快照执行
/// (ICurrentUser 可读 "system:{JobName}",遥测 Consumer Span 接续)。
/// </summary>
public abstract class NexMeshCronJob : BackgroundService
{
    private readonly CronExpression _cron;
    private readonly bool _runOnStart;
    private readonly IServiceProvider _provider;
    private readonly ILogger _logger;
    private readonly INexMeshBackgroundRunner _runner;

    protected NexMeshCronJob(string cronExpression, bool runOnStart = false, bool includeSeconds = false,
        IServiceProvider? provider = null, ILogger? logger = null)
    {
        _cron = CronExpression.Parse(cronExpression,
            includeSeconds ? CronFormat.IncludeSeconds : CronFormat.Standard);
        _runOnStart = runOnStart;
        _provider = provider ?? throw new ArgumentNullException(nameof(provider), "Cron 作业需要 IServiceProvider(经 AddNexMeshCronJob 注册自动注入)");
        _logger = logger ?? NullLogger.Instance;
        _runner = _provider.GetRequiredService<INexMeshBackgroundRunner>();
    }

    public string JobName => GetType().Name;

    /// <summary>作业身份快照:默认系统身份(定时任务无请求上下文);子类可覆写为具体租户身份。</summary>
    protected virtual NexMeshContext? ContextSnapshot => new()
    {
        UserId = $"system:{JobName}",
        UserName = JobName,
        Claims = new Dictionary<string, string> { ["job"] = JobName },
    };

    /// <summary>单轮业务逻辑:依赖一律从注入的 sp(全新作用域)解析,严禁闭包捕获 Scoped。</summary>
    protected abstract Task RunOnceAsync(IServiceProvider scoped, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_runOnStart)
        {
            await RunIsolatedAsync(stoppingToken);
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var next = _cron.GetNextOccurrence(now, TimeZoneInfo.Local)
                    ?? throw new InvalidOperationException($"Cron '{JobName}' 无下一次触发时间");
                var delay = next - now;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break; // 停机
            }

            await RunIsolatedAsync(stoppingToken);
        }
    }

    private async Task RunIsolatedAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _runner.RunAsync(ContextSnapshot, RunOnceAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 优雅停机中的在途一轮:正常退出
        }
        catch (Exception ex)
        {
            // 单轮异常隔离:记录后继续下一轮,绝不停摆整个调度器
            _logger.LogError(ex, "定时作业 {JobName} 单轮执行失败(调度继续)", JobName);
        }
    }
}

/// <summary>简单周期作业(TimeSpan):不需要 Cron 表达式的场景。接 IServiceProvider 时同样享受系统身份快照与日志隔离。</summary>
public abstract class NexMeshIntervalJob(TimeSpan interval, IServiceProvider? provider = null) : BackgroundService
{
    private readonly INexMeshBackgroundRunner? _runner = provider?.GetRequiredService<INexMeshBackgroundRunner>();

    public string JobName => GetType().Name;

    protected virtual NexMeshContext? ContextSnapshot => new()
    {
        UserId = $"system:{JobName}",
        Claims = new Dictionary<string, string> { ["job"] = JobName },
    };

    protected abstract Task RunOnceAsync(IServiceProvider scoped, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            try
            {
                if (_runner is not null)
                {
                    await _runner.RunAsync(ContextSnapshot, RunOnceAsync, stoppingToken);
                }
                else
                {
                    await RunOnceAsync(NullServiceProvider.Instance, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // 单轮异常隔离:吞掉继续;需要完整日志与身份快照时接 IServiceProvider
            }
        }
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public static readonly NullServiceProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
