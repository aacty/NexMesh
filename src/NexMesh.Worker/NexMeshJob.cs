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

    /// <summary>执行闸:同一作业的调度轮与手动触发互斥(忙碌时手动触发立即返回 false,不排队)。</summary>
    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>最近执行记录(环形 10 条,内存态;运维速览定位,审计走日志)。</summary>
    private readonly Queue<JobRunRecord> _recentRuns = new();
    private readonly object _runsLock = new();

    /// <summary>最近执行记录(新→旧,最多 10 条;Dashboard 展示)。</summary>
    public IReadOnlyList<JobRunRecord> RecentRuns
    {
        get { lock (_runsLock) { return _recentRuns.ToList(); } }
    }

    private void RecordRun(JobRunRecord record)
    {
        lock (_runsLock)
        {
            _recentRuns.Enqueue(record);
            while (_recentRuns.Count > 10)
            {
                _recentRuns.Dequeue();
            }
        }
    }

    /// <summary>立即手动执行一轮(Dashboard/运维):与调度轮互斥;正在执行返回 false;走同一后台执行纪律(系统身份快照+独立作用域)。
    /// triggeredBy 供审计记录(操作人;HTTP 访问日志为权威,此为速览)。</summary>
    public async Task<bool> TriggerNowAsync(string? triggeredBy = null, CancellationToken ct = default)
    {
        if (!await _runGate.WaitAsync(0, ct))
        {
            return false; // 调度轮或上一次手动在执行:忙碌
        }
        var started = DateTimeOffset.UtcNow;
        try
        {
            await _runner.RunAsync(ContextSnapshot, RunOnceAsync, ct);
            RecordRun(new JobRunRecord(started, DateTimeOffset.UtcNow, true, null, "manual", triggeredBy));
            return true;
        }
        catch (Exception ex)
        {
            RecordRun(new JobRunRecord(started, DateTimeOffset.UtcNow, false, ex.Message, "manual", triggeredBy));
            throw;
        }
        finally
        {
            _runGate.Release();
        }
    }

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
        if (!await _runGate.WaitAsync(0, stoppingToken))
        {
            return;
        }
        var started = DateTimeOffset.UtcNow;
        try
        {
            await _runner.RunAsync(ContextSnapshot, RunOnceAsync, stoppingToken);
            RecordRun(new JobRunRecord(started, DateTimeOffset.UtcNow, true, null, "scheduled", null));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 优雅停机中的在途一轮:正常退出(不记失败)
        }
        catch (Exception ex)
        {
            // 单轮异常隔离:记录后继续下一轮,绝不停摆整个调度器
            RecordRun(new JobRunRecord(started, DateTimeOffset.UtcNow, false, ex.Message, "scheduled", null));
            _logger.LogError(ex, "定时作业 {JobName} 单轮执行失败(调度继续)", JobName);
        }
        finally
        {
            _runGate.Release();
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

    /// <summary>执行闸:调度轮与手动触发互斥。</summary>
    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>最近执行记录(环形 10 条)。</summary>
    private readonly Queue<JobRunRecord> _recentRuns = new();
    private readonly object _runsLock = new();

    public IReadOnlyList<JobRunRecord> RecentRuns
    {
        get { lock (_runsLock) { return _recentRuns.ToList(); } }
    }

    private void RecordRun(JobRunRecord record)
    {
        lock (_runsLock)
        {
            _recentRuns.Enqueue(record);
            while (_recentRuns.Count > 10)
            {
                _recentRuns.Dequeue();
            }
        }
    }

    /// <summary>立即手动执行一轮(Dashboard/运维):与调度轮互斥;正在执行返回 false;triggeredBy 供审计。</summary>
    public async Task<bool> TriggerNowAsync(string? triggeredBy = null, CancellationToken ct = default)
    {
        if (!await _runGate.WaitAsync(0, ct))
        {
            return false;
        }
        var started = DateTimeOffset.UtcNow;
        try
        {
            if (_runner is not null)
            {
                await _runner.RunAsync(ContextSnapshot, RunOnceAsync, ct);
            }
            else
            {
                await RunOnceAsync(NullServiceProvider.Instance, ct);
            }
            RecordRun(new JobRunRecord(started, DateTimeOffset.UtcNow, true, null, "manual", triggeredBy));
            return true;
        }
        catch (Exception ex)
        {
            RecordRun(new JobRunRecord(started, DateTimeOffset.UtcNow, false, ex.Message, "manual", triggeredBy));
            throw;
        }
        finally
        {
            _runGate.Release();
        }
    }

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
                if (!await _runGate.WaitAsync(0, stoppingToken))
                {
                    continue;
                }
                var started = DateTimeOffset.UtcNow;
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
                    RecordRun(new JobRunRecord(started, DateTimeOffset.UtcNow, true, null, "scheduled", null));
                }
                finally
                {
                    _runGate.Release();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 单轮异常隔离:吞掉继续(与 Cron 一致,记录失败便于面板速览)
                RecordRun(new JobRunRecord(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false, ex.Message, "scheduled", null));
            }
        }
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public static readonly NullServiceProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}

/// <summary>作业单次执行记录(Dashboard 展示;内存态,重启清零,审计走日志)。</summary>
public sealed record JobRunRecord(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool Ok,
    string? Error,
    string Trigger,
    string? TriggeredBy)
{
    /// <summary>耗时(毫秒)。</summary>
    public long DurationMs => (long)(FinishedAt - StartedAt).TotalMilliseconds;
}
