using System.Diagnostics;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;
using GrpcChannelOptions = Grpc.Net.Client.GrpcChannelOptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NexMesh;
using NexMesh.Core;
using NexMesh.Host;
using NexMesh.Core.DependencyInjection;

// ═══ gRPC 契约网关实测(进程内 InMemory channel,测网关自身) ═══
// 两种模式:args 无参 = 1000 万请求吞吐军演(默认);args[0]="latency" = 单通道顺序延迟分布(测热路径改造效果)
var mode = args.Length > 0 ? args[0] : "load";
if (mode == "latency")
{
    await LatencyBenchmark.RunAsync();
    return;
}

const long TOTAL = 10_000_000;
long issued_ = 0;
int DEGREE = Environment.ProcessorCount * 2;

var builder = WebApplication.CreateBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning); // 10M 请求:Info 级请求日志会拖死 IO
builder.Configuration["NexMesh:Contracts:AllowAnonymousByDefault"] = "true";
builder.WebHost.UseTestServer();
builder.Services.AddNexMesh();
builder.Services.AddNexMeshContracts(typeof(LoadService).Assembly);
builder.Services.AddNexMeshGrpc(typeof(LoadService).Assembly);
var app = builder.Build();
app.UseNexMeshExceptionHandling();
app.MapNexMeshContracts(typeof(LoadService).Assembly);
app.MapNexMeshGrpc();
await app.StartAsync();

var server = (TestServer)app.Services.GetRequiredService(typeof(Microsoft.AspNetCore.Hosting.Server.IServer));
Console.WriteLine($"机器: {Environment.ProcessorCount} 核 | 目标: {TOTAL:N0} gRPC 请求 | 并行通道: {DEGREE}");

// 预热缓存路径
var warm = new NexMesh.Grpc.ContractGateway.ContractGatewayClient(
    GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = server.CreateHandler() }));
await warm.InvokeAsync(new NexMesh.Grpc.ContractCall { Service = "load", Method = "Calc", ArgsJson = """{"a":1,"b":2,"op":"+"}""" });
await warm.InvokeAsync(new NexMesh.Grpc.ContractCall { Service = "load", Method = "Echo", ArgsJson = """{"message":"x","repeat":2}""" });

long ok = 0, err = 0, wrong = 0;
var sw = Stopwatch.StartNew();
await Parallel.ForAsync(0, DEGREE, async (w, ct) =>
{
    using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
    {
        HttpHandler = server.CreateHandler(),
    });
    var client = new NexMesh.Grpc.ContractGateway.ContractGatewayClient(channel);
    var calc = w % 2 == 0;
    long taken;
    while ((taken = Interlocked.Add(ref issued_, 200) - 200) < TOTAL)
    {
        var batch = (int)Math.Min(200, TOTAL - taken);
        for (var i = 0; i < batch; i++)
        {
            try
            {
                NexMesh.Grpc.ContractReply reply;
                if (calc)
                {
                    reply = await client.InvokeAsync(new NexMesh.Grpc.ContractCall
                        { Service = "load", Method = "Calc", ArgsJson = """{"a":17,"b":25,"op":"+"}""" });
                    if (!reply.EnvelopeJson.Contains("\"data\":42")) Interlocked.Increment(ref wrong);
                }
                else
                {
                    reply = await client.InvokeAsync(new NexMesh.Grpc.ContractCall
                        { Service = "load", Method = "Echo", ArgsJson = """{"message":"ab","repeat":3}""" });
                    if (!reply.EnvelopeJson.Contains("ababab")) Interlocked.Increment(ref wrong);
                }
                if (reply.EnvelopeJson.Contains("\"success\":true")) Interlocked.Increment(ref ok);
                else Interlocked.Increment(ref err);
            }
            catch
            {
                Interlocked.Increment(ref err);
            }
        }
    }
});
sw.Stop();
Console.WriteLine($"完成: ok={ok:N0} err={err:N0} 内容错误={wrong:N0}");
Console.WriteLine($"守恒: {(ok + err == TOTAL ? "PASS" : "FAIL!!!")} | 正确性: {(wrong == 0 && err == 0 ? "PASS(全部成功且载荷正确)" : "FAIL!!!")}");
Console.WriteLine($"吞吐 {TOTAL / sw.Elapsed.TotalSeconds:N0} req/s | 用时 {sw.Elapsed.TotalMinutes:F1} 分钟 | 峰值堆 {GC.GetTotalMemory(false) / 1024.0 / 1024.0:F0} MB");
await app.DisposeAsync();

public interface ILoadApi : IServiceApi
{
    [AllowAnonymous]
    [HttpGet("calc")]
    Task<long> CalcAsync(long a, long b, string op);

    [AllowAnonymous]
    [HttpGet("echo")]
    Task<string> EchoAsync(string message, int repeat);
}

public class LoadService : ILoadApi, IScopedService
{
    public Task<long> CalcAsync(long a, long b, string op) => Task.FromResult(op == "+" ? a + b : a * b);

    public Task<string> EchoAsync(string message, int repeat)
        => Task.FromResult(string.Concat(Enumerable.Repeat(message, Math.Min(repeat, 8))));
}

/// <summary>单通道顺序延迟分布:隔离服务端热路径每调用成本(吞吐军演见默认模式)。</summary>
internal static class LatencyBenchmark
{
    public static async Task RunAsync()
    {
        const int WARMUP = 2_000, TOTAL = 200_000;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration["NexMesh:Contracts:AllowAnonymousByDefault"] = "true";
        builder.WebHost.UseTestServer();
        builder.Services.AddNexMesh();
        builder.Services.AddNexMeshContracts(typeof(LoadService).Assembly);
        builder.Services.AddNexMeshGrpc(typeof(LoadService).Assembly);
        var app = builder.Build();
        app.MapNexMeshGrpc();
        await app.StartAsync();
        var server = (TestServer)app.Services.GetRequiredService(typeof(Microsoft.AspNetCore.Hosting.Server.IServer));

        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = server.CreateHandler() });
        var client = new NexMesh.Grpc.ContractGateway.ContractGatewayClient(channel);

        // 预热(含首扫建索引/委托编译)
        for (var i = 0; i < WARMUP; i++)
        {
            var reply = await client.InvokeAsync(new NexMesh.Grpc.ContractCall
                { Service = "load", Method = "Calc", ArgsJson = """{"a":17,"b":25,"op":"+"}""" });
            if (!reply.EnvelopeJson.Contains("\"data\":42")) throw new InvalidOperationException("预热载荷校验失败");
        }

        var samples = new long[TOTAL];
        var call = new NexMesh.Grpc.ContractCall { Service = "load", Method = "Calc", ArgsJson = """{"a":17,"b":25,"op":"+"}""" };
        for (var i = 0; i < TOTAL; i++)
        {
            var sw = Stopwatch.GetTimestamp();
            var reply = await client.InvokeAsync(call);
            samples[i] = Stopwatch.GetTimestamp() - sw;
            if (!reply.EnvelopeJson.Contains("\"data\":42")) throw new InvalidOperationException($"载荷校验失败 @{i}");
        }
        await app.DisposeAsync();

        Array.Sort(samples);
        double Mean(long[] s) => s.Average() * 1_000_000.0 / Stopwatch.Frequency;
        double Pct(long[] s, double p) => s[(int)Math.Min(s.Length - 1, s.Length * p)] * 1_000_000.0 / Stopwatch.Frequency;
        Console.WriteLine($"机器: {Environment.ProcessorCount} 核 | 单通道顺序 {TOTAL:N0} 次 Calc(3 参数)");
        Console.WriteLine($"mean={Mean(samples):F2}μs p50={Pct(samples, 0.50):F2}μs p95={Pct(samples, 0.95):F2}μs p99={Pct(samples, 0.99):F2}μs max={Pct(samples, 1.0):F2}μs");
        Console.WriteLine($"吞吐(单通道顺序)={Stopwatch.Frequency / samples.Average():N0} req/s | 峰值堆 {GC.GetTotalMemory(false) / 1024.0 / 1024.0:F0} MB");
    }
}
