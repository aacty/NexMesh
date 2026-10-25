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

// ═══ gRPC 契约网关 1000 万请求密集处理实测(进程内 InMemory channel,测网关自身) ═══
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
