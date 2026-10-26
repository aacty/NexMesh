using Microsoft.Extensions.Configuration;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;

namespace NexMesh.Benchmarks;

/// <summary>
/// NexMesh 框架热点基准(结果文件入库,提交前后对比回归):
/// 1. 契约分发:编译委托 vs 反射 Invoke(A1 深修的量化收益)
/// 2. 登录限流 TryPass(每请求热路径)
/// 3. 事件总线发布(有界 Channel 写入)
/// 4. 上下文快照存取(AsyncLocal 槽位)
/// 运行:dotnet run -c Release -p:NexMesh.Benchmarks -- --filter * [--join]
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        var config = ManualConfig.Create(DefaultConfig.Instance)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator) // FreeSql 官方包无 Release 标记(第三方包常态,框架代码自身全部 Release)
            .AddJob(Job.ShortRun.WithIterationCount(20).WithWarmupCount(5))
            .AddColumn(StatisticColumn.Mean)
            .AddColumn(StatisticColumn.P95)
            .AddColumn(StatisticColumn.Median)
            .WithSummaryStyle(SummaryStyle.Default.WithMaxParameterColumnWidth(48));
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
    }
}

// ═══════════════ 基准 1:契约分发(编译委托 vs 反射) ═══════════════

public sealed class SampleContract
{
    public Task<int> GetAnswerAsync(int a, int b) => Task.FromResult(a + b);

    public Task NoArgAsync() => Task.CompletedTask;
}

/// <summary>同一方法两种调用方式的成本对比——量化 AutoApiDispatcher 委托化的收益。</summary>
[MemoryDiagnoser]
public class DispatchBenchmarks
{
    private readonly SampleContract _service = new();
    private readonly object?[] _args = [1, 2];
    private readonly System.Reflection.MethodInfo _method = typeof(SampleContract).GetMethod(nameof(SampleContract.GetAnswerAsync))!;
    private readonly Func<object, object?[], object?> _compiled;

    public DispatchBenchmarks()
    {
        // 与 ContractInvokerCache 相同的表达式编译
        var serviceParam = System.Linq.Expressions.Expression.Parameter(typeof(object), "service");
        var argsParam = System.Linq.Expressions.Expression.Parameter(typeof(object?[]), "args");
        var parameters = _method.GetParameters();
        var callArgs = new System.Linq.Expressions.Expression[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            callArgs[i] = System.Linq.Expressions.Expression.Convert(
                System.Linq.Expressions.Expression.ArrayIndex(argsParam, System.Linq.Expressions.Expression.Constant(i)),
                parameters[i].ParameterType);
        }
        var call = System.Linq.Expressions.Expression.Call(
            System.Linq.Expressions.Expression.Convert(serviceParam, _method.DeclaringType!), _method, callArgs);
        _compiled = System.Linq.Expressions.Expression
            .Lambda<Func<object, object?[], object?>>(System.Linq.Expressions.Expression.Convert(call, typeof(object)), serviceParam, argsParam)
            .Compile();
    }

    [Benchmark(Baseline = true)]
    public async Task 反射Invoke() => await (Task<int>)_method.Invoke(_service, _args)!;

    [Benchmark]
    public async Task 编译委托() => await (Task<int>)_compiled(_service, _args)!;
}

// ═══════════════ 基准 2:登录限流 TryPass ═══════════════

/// <summary>登录限流器每请求判定成本(固定窗口计数 + 字典原子更新)。</summary>
[MemoryDiagnoser]
public class LoginRateLimiterBenchmarks
{
    private readonly NexMesh.Auth.LoginRateLimiter _limiter;
    private readonly Microsoft.AspNetCore.Http.DefaultHttpContext _context = new();

    public LoginRateLimiterBenchmarks()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NexMesh:Auth:LoginRateLimit:PermitLimit"] = "1000000", // 基准不打 429
                ["NexMesh:Auth:LoginRateLimit:WindowSeconds"] = "60",
            })
            .Build();
        _limiter = new NexMesh.Auth.LoginRateLimiter(config);
        _context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
    }

    [Benchmark]
    public async Task<bool> TryPass() => await _limiter.TryPassAsync(_context);
}

// ═══════════════ 基准 3:事件总线发布 ═══════════════

/// <summary>有界 Channel 发布成本(无订阅场景下的入队下限)。</summary>
[MemoryDiagnoser]
public class EventPublishBenchmarks
{
    private readonly System.Threading.Channels.Channel<NexMesh.Events.EventWorkItem> _channel =
        System.Threading.Channels.Channel.CreateBounded<NexMesh.Events.EventWorkItem>(
            new System.Threading.Channels.BoundedChannelOptions(10_000) { FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait });

    [Benchmark]
    public async Task 发布并入队()
    {
        await _channel.Writer.WriteAsync(new NexMesh.Events.EventWorkItem("bench:event", null, null, DateTimeOffset.UtcNow));
        _channel.Reader.TryRead(out _);
    }
}

// ═══════════════ 基准 4:上下文快照存取 ═══════════════

/// <summary>AsyncLocal 上下文槽位读写成本(每请求进出一次)。</summary>
[MemoryDiagnoser]
public class ContextSlotBenchmarks
{
    private readonly NexMesh.Core.Context.AsyncLocalNexMeshContextAccessor _accessor = new();

    [Benchmark]
    public void 上下文读写还原()
    {
        var context = new NexMesh.Core.Context.NexMeshContext { UserId = "bench", TenantId = "t1" };
        using (_accessor.Enter(context))
        {
            _ = _accessor.Current;
        }
    }
}

// ═══════════════ 基准 5:数据层 CRUD(FreeSql SQLite 内存)═══════════════

/// <summary>仓储 CRUD 热路径:插入(雪花)/查询/软删过滤计数。</summary>
[MemoryDiagnoser]
public class DataCrudBenchmarks
{
    public sealed class BenchRow : NexMesh.Data.ISoftDelete
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public int Value { get; set; }
        public bool IsDeleted { get; set; }
    }

    private readonly IFreeSql _fsql;

    public DataCrudBenchmarks()
    {
        _fsql = new FreeSql.FreeSqlBuilder()
            .UseConnectionString(FreeSql.DataType.Sqlite, "Data Source=bench-crud.db;Cache=Shared")
            .UseAutoSyncStructure(true)
            .Build();
        _fsql.GlobalFilter.Apply<NexMesh.Data.ISoftDelete>("sd", e => !e.IsDeleted);
        _fsql.Insert(Enumerable.Range(0, 100).Select(i => new BenchRow { Id = NexMesh.Data.NexMeshSnowflake.NextId(), Name = $"row{i}", Value = i }).ToList()).ExecuteAffrows();
    }

    [Benchmark]
    public long 软删过滤计数()
        => _fsql.Select<BenchRow>().Count();

    [Benchmark]
    public BenchRow 单行查询()
        => _fsql.Select<BenchRow>().Where(r => r.Value == 50).First();

    [Benchmark]
    public long 插入单行()
    {
        var id = _fsql.Insert(new BenchRow { Id = NexMesh.Data.NexMeshSnowflake.NextId(), Name = "bench", Value = 999 }).ExecuteIdentity();
        return id;
    }
}

// ═══════════════ 基准 6:缓存命中(L1/L2)═══════════════

/// <summary>双层缓存命中路径:未命中写→L1 命中→防击穿读。</summary>
[MemoryDiagnoser]
public class CacheBenchmarks
{
    private readonly NexMesh.Cache.NexMeshCacheSource _source = new(new NexMesh.Cache.CacheSourceOptions { KeyPrefix = "bench:" }, null);

    public CacheBenchmarks()
    {
        _source.SetAsync("warm", "v").GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task<string?> L1命中()
        => await _source.GetAsync<string>("warm");

    [Benchmark]
    public async Task 未命中写入()
        => await _source.SetAsync("miss" + Random.Shared.Next(1000), "v");

    [Benchmark]
    public async Task<string?> 防击穿读()
        => await _source.GetOrCreateAsync("goc", TimeSpan.FromSeconds(30), _ => Task.FromResult<string?>("built"));
}
