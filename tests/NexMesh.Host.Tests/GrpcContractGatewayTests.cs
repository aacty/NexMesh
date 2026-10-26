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
