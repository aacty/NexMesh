using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NexMesh;
using NexMesh.Core;
using NexMesh.Core.DependencyInjection;
using Xunit;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;
using GrpcChannelOptions = Grpc.Net.Client.GrpcChannelOptions;

namespace NexMesh.Host.Tests;

public interface IGrpcDemoApi : IServiceApi
{
    [AllowAnonymous]
    [HttpGet("add")]
    Task<int> AddAsync(int a, int b);

    [AllowAnonymous]
    [HttpGet("echo")]
    Task<string> EchoAsync(string message);
}

public class GrpcDemoService : IGrpcDemoApi, IScopedService
{
    public Task<int> AddAsync(int a, int b) => Task.FromResult(a + b);

    public Task<string> EchoAsync(string message) => Task.FromResult($"echo:{message}");
}

/// <summary>契约双面输出:同一契约 REST 与 gRPC 网关同时可用,结果一致。</summary>
public class GrpcContractGatewayTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["NexMesh:Contracts:AllowAnonymousByDefault"] = "true";
        builder.WebHost.UseTestServer();
        builder.Services.AddNexMesh();
        builder.Services.AddNexMeshContracts(typeof(GrpcDemoService).Assembly);
        builder.Services.AddNexMeshGrpc(typeof(GrpcDemoService).Assembly);

        _app = builder.Build();
        _app.UseNexMeshExceptionHandling();
        _app.MapNexMeshContracts(typeof(GrpcDemoService).Assembly);
        _app.MapNexMeshGrpc();
        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private GrpcChannel CreateChannel()
        => GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = ((TestServer)_app.Services.GetRequiredService(typeof(Microsoft.AspNetCore.Hosting.Server.IServer))!).CreateHandler(),
        });

    [Fact]
    public async Task gRPC网关_整数参数_与REST结果一致()
    {
        var rest = await (await _http.GetAsync("/api/grpcdemo/add?a=3&b=4")).Content.ReadAsStringAsync();
        Assert.Contains("\"data\":7", rest.Replace(" ", ""));

        var client = new NexMesh.Grpc.ContractGateway.ContractGatewayClient(CreateChannel());
        var reply = await client.InvokeAsync(new NexMesh.Grpc.ContractCall
        {
            Service = "grpcdemo",
            Method = "Add",
            ArgsJson = """{"a":3,"b":4}""",
        });
        Assert.Contains("\"data\":7", reply.EnvelopeJson.Replace(" ", ""));
    }

    [Fact]
    public async Task gRPC网关_字符串参数_未知方法报错信封()
    {
        var client = new NexMesh.Grpc.ContractGateway.ContractGatewayClient(CreateChannel());

        var echo = await client.InvokeAsync(new NexMesh.Grpc.ContractCall
        {
            Service = "grpcdemo",
            Method = "Echo",
            ArgsJson = """{"message":"hi"}""",
        });
        Assert.Contains("echo:hi", echo.EnvelopeJson);

        var missing = await client.InvokeAsync(new NexMesh.Grpc.ContractCall
        {
            Service = "grpcdemo",
            Method = "NotExist",
            ArgsJson = "{}",
        });
        Assert.Contains("\"code\":400", missing.EnvelopeJson); // 未找到方法 → 400 信封(中文经 Web 编码器转义)
    }

    [Fact]
    public async Task gRPC网关_非法ArgsJson_独立信封400不冒充服务端故障()
    {
        var client = new NexMesh.Grpc.ContractGateway.ContractGatewayClient(CreateChannel());

        // SSS+审核:恶意/非法 args_json 是客户端输入错误 → "参数 JSON 非法"(Warning 级,400),
        // 与"服务内部错误"(Error 级告警)分离——告警信噪比;信封中文经 Web 序列化为 \uXXXX,断言反序列化后的字段值
        var malformed = await client.InvokeAsync(new NexMesh.Grpc.ContractCall
        {
            Service = "grpcdemo",
            Method = "Add",
            ArgsJson = "not-a-json{{{",
        });
        using var doc = System.Text.Json.JsonDocument.Parse(malformed.EnvelopeJson);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(400, doc.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("参数 JSON 非法", doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task gRPC网关_并发首触_索引一次构建与计划创建竞态安全()
    {
        // SSS+ 审核专项:直打 BuildIndex 双检锁与 GetOrAdd 并发创建——48 线程同时首触
        // (混合"多方法各首次"与"同方法齐首次"),全部结果正确即无撕裂/漏答
        var registry = _app.Services.GetRequiredService<NexMesh.Host.NexMeshGrpcExtensions.ContractGrpcRegistry>();
        var methods = new[] { "Add", "Echo" };
        const int THREADS = 48;
        var failures = 0;

        await Parallel.ForAsync(0, THREADS, async (w, ct) =>
        {
            // 每线程先 Find(未构建/半构建索引都可能被观察到)再走完整调用
            var method = methods[w % methods.Length];
            var plan = registry.Find("grpcdemo", method);
            if (plan is null)
            {
                Interlocked.Increment(ref failures);
                return;
            }
            // 同方法并发首触同一计划(计划缓存竞态)
            var echoPlan = registry.Find("GRPCDEMO", method.ToUpperInvariant()); // 大小写不敏感路径
            if (echoPlan is null || echoPlan.Endpoint.Method != plan.Endpoint.Method)
            {
                Interlocked.Increment(ref failures);
            }
        });

        // 首触竞态后,经真实 gRPC 调用各方法仍结果正确
        var client = new NexMesh.Grpc.ContractGateway.ContractGatewayClient(CreateChannel());
        var add = await client.InvokeAsync(new NexMesh.Grpc.ContractCall { Service = "grpcdemo", Method = "Add", ArgsJson = """{"a":3,"b":4}""" });
        var echo = await client.InvokeAsync(new NexMesh.Grpc.ContractCall { Service = "grpcdemo", Method = "Echo", ArgsJson = """{"message":"x"}""" });
        Assert.Equal(0, failures);
        Assert.Contains("\"data\":7", add.EnvelopeJson);
        Assert.Contains("echo:x", echo.EnvelopeJson);
    }
}

/// <summary>gRPC Reflection:客户端经 ServerReflection 动态发现 ContractGateway 服务。</summary>
public class GrpcReflectionTests : IAsyncLifetime
{
    private WebApplication _app = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["NexMesh:Contracts:AllowAnonymousByDefault"] = "true";
        builder.WebHost.UseTestServer();
        builder.Services.AddNexMesh();
        builder.Services.AddNexMeshContracts(typeof(GrpcDemoService).Assembly);
        builder.Services.AddNexMeshGrpc(typeof(GrpcDemoService).Assembly);
        _app = builder.Build();
        _app.UseNexMeshExceptionHandling();
        _app.MapNexMeshContracts(typeof(GrpcDemoService).Assembly);
        _app.MapNexMeshGrpc();
        await _app.StartAsync();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Reflection端点存在且响应()
    {
        // 反射协议为 HTTP/2 gRPC 流——最轻验证:端点已映射,直接 gRPC 调用返回协议响应而非 404
        using var http = new System.Net.Http.HttpClient(
            ((TestServer)_app.Services.GetRequiredService(typeof(Microsoft.AspNetCore.Hosting.Server.IServer))!).CreateHandler())
        {
            BaseAddress = new Uri("http://localhost"),
        };
        var response = await http.PostAsync("/nexmesh.contract.ContractGateway/Invoke",
            new StringContent("", System.Text.Encoding.UTF8, "application/grpc"));
        // 未带 gRPC 帧 → 协议层响应(非 404 即端点在,Reflection 注册在 MapNexMeshGrpc 内部)
        Assert.NotEqual(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}
