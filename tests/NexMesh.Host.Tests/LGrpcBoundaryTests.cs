using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NexMesh;
using NexMesh.Core;
using NexMesh.Core.DependencyInjection;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;
using GrpcChannelOptions = Grpc.Net.Client.GrpcChannelOptions;

namespace NexMesh.Host.Tests;

/// <summary>L 轮边界契约:慢方法(deadline/取消传播)+ 大载荷。</summary>
public interface ILBoundaryApi : IServiceApi
{
    [AllowAnonymous]
    [HttpGet("slow")]
    Task<string> SlowAsync(int delayMs, CancellationToken ct);

    [AllowAnonymous]
    [HttpGet("big")]
    Task<string> BigAsync(string payload);
}

public class LBoundaryService : ILBoundaryApi, IScopedService
{
    /// <summary>服务端观察到取消的信号(静态:每请求新实例,经类级共享)。</summary>
    private static TaskCompletionSource _canceled = NewTcs();

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset() => _canceled = NewTcs();

    public static Task CancellationObserved => _canceled.Task;

    public async Task<string> SlowAsync(int delayMs, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delayMs, ct);
            return "done";
        }
        catch (OperationCanceledException)
        {
            _canceled.TrySetResult(); // 契约参数上的 CancellationToken 收到了取消
            throw;
        }
    }

    public Task<string> BigAsync(string payload) => Task.FromResult($"{payload.Length}:{payload[^8..]}");
}

/// <summary>
/// 第三轮交叉测试(L):gRPC 边界——deadline/取消传播、1MB 大载荷往返、32 通道并发混合调用。
/// (契约方法未注册→信封 400 已由既有测试覆盖,不重复。)
/// </summary>
public class LGrpcBoundaryTests : IAsyncLifetime
{
    private WebApplication _app = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["NexMesh:Contracts:AllowAnonymousByDefault"] = "true";
        builder.WebHost.UseTestServer();
        builder.Services.AddNexMesh();
        // 装配整个测试程序集的契约(含 GrpcDemoService 与 LBoundaryService)
        builder.Services.AddNexMeshContracts(typeof(LBoundaryService).Assembly);
        builder.Services.AddNexMeshGrpc(typeof(LBoundaryService).Assembly);
        _app = builder.Build();
        _app.UseNexMeshExceptionHandling();
        _app.MapNexMeshContracts(typeof(LBoundaryService).Assembly);
        _app.MapNexMeshGrpc();
        await _app.StartAsync();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private GrpcChannel CreateChannel()
        => GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = ((TestServer)_app.Services.GetRequiredService(typeof(Microsoft.AspNetCore.Hosting.Server.IServer))!).CreateHandler(),
        });

    private static NexMesh.Grpc.ContractCall Call(string service, string method, string argsJson)
        => new() { Service = service, Method = method, ArgsJson = argsJson };

    [Fact]
    public async Task L_gRPC_deadline超时_客户端DeadlineExceeded_服务端令牌收到取消()
    {
        LBoundaryService.Reset();
        using var channel = CreateChannel();

        var sw = Stopwatch.StartNew();
        var call = new NexMesh.Grpc.ContractGateway.ContractGatewayClient(channel).InvokeAsync(
            Call("lboundary", "Slow", """{"delayMs":10000}"""),
            deadline: DateTime.UtcNow.AddMilliseconds(400));
        var ex = await Record.ExceptionAsync(async () => await call);
        sw.Stop();

        Assert.IsType<RpcException>(ex); // 调用被取消——绝不能"正常完成"
        // 非 OK 不变量(核心断言):修复前取消会被网关包装成 gRPC OK + "服务内部错误"失败信封抢答。
        // 具体状态在传输层竞速中二选一,均合法:DeadlineExceeded(客户端自我判定,单跑稳定实测)
        // / Cancelled(服务端规范终止状态)——负载下服务端尾随状态可能先于客户端 deadline 判定送达。
        var status = ((RpcException)ex!).StatusCode;
        Assert.True(status != StatusCode.OK, $"被取消的调用不应以 OK 完成,实际 {status}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"deadline 生效耗时 {sw.ElapsedMilliseconds}ms");

        // 服务端 CancellationToken 必须收到取消(传播到契约方法参数);5s 观察窗抗整套并行负载
        var observed = await Task.WhenAny(LBoundaryService.CancellationObserved, Task.Delay(5000));
        Assert.True(observed == LBoundaryService.CancellationObserved,
            "deadline 超时后服务端契约方法的 CancellationToken 未收到取消");
    }

    [Fact]
    public async Task L_gRPC_1MB大载荷args_json_往返完整()
    {
        using var channel = CreateChannel();
        var client = new NexMesh.Grpc.ContractGateway.ContractGatewayClient(channel);

        var payload = new string('x', 1_000_000) + "TAIL9876";
        var reply = await client.InvokeAsync(Call("lboundary", "Big",
            "{\"payload\":\"" + payload + "\"}"));

        using var doc = JsonDocument.Parse(reply.EnvelopeJson);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        // 长度与尾部原样返回 → 1MB 未截断/未变形
        Assert.Equal($"{payload.Length}:TAIL9876", doc.RootElement.GetProperty("data").GetString());
    }

    [Fact]
    public async Task L_gRPC_32通道并发混合调用_全对()
    {
        const int N = 32;
        var channels = new GrpcChannel[N];
        var clients = new NexMesh.Grpc.ContractGateway.ContractGatewayClient[N];
        for (var i = 0; i < N; i++)
        {
            channels[i] = CreateChannel();
            clients[i] = new NexMesh.Grpc.ContractGateway.ContractGatewayClient(channels[i]);
        }
        try
        {
            var errors = new ConcurrentBag<string>();
            await Parallel.ForAsync(0, N, async (i, ct) =>
            {
                var client = clients[i];
                var payload = new string('p', 64_000) + $"TAIL{i:000}";
                var expected = $"{payload.Length}:{payload[^8..]}"; // 与服务端同构:长度+末8字符
                var big = await client.InvokeAsync(Call("lboundary", "Big", "{\"payload\":\"" + payload + "\"}"));
                var add = await client.InvokeAsync(Call("grpcdemo", "Add", $"{{\"a\":{i},\"b\":{i + 1}}}"));
                var echo = await client.InvokeAsync(Call("grpcdemo", "Echo", $"{{\"message\":\"m{i}\"}}"));

                using var bigDoc = JsonDocument.Parse(big.EnvelopeJson);
                if (bigDoc.RootElement.GetProperty("data").GetString() != expected)
                {
                    errors.Add($"通道{i}: 大载荷往返不一致(期望 {expected},实际 {bigDoc.RootElement.GetProperty("data").GetString()})");
                }
                using var addDoc = JsonDocument.Parse(add.EnvelopeJson);
                if (addDoc.RootElement.GetProperty("data").GetInt32() != 2 * i + 1)
                {
                    errors.Add($"通道{i}: Add 结果错误");
                }
                using var echoDoc = JsonDocument.Parse(echo.EnvelopeJson);
                if (echoDoc.RootElement.GetProperty("data").GetString() != $"echo:m{i}")
                {
                    errors.Add($"通道{i}: Echo 结果错误");
                }
            });

            Assert.Empty(errors);
        }
        finally
        {
            foreach (var channel in channels)
            {
                channel.Dispose(); // GrpcChannel 仅 IDisposable
            }
        }
    }
}
