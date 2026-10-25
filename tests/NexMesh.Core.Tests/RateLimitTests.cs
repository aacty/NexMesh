namespace NexMesh.Core.Tests;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexMesh;
using NexMesh.Core.DependencyInjection;
using Xunit;

// ═══════════════ 限流演示契约 ═══════════════

public interface IRateFixtureApi : IServiceApi
{
    [AllowAnonymous]
    [HttpGet("burst")]
    [EnableRateLimiting("test-burst")]
    Task<string> BurstAsync();
}

public class RateFixtureService : IRateFixtureApi, IScopedService
{
    public Task<string> BurstAsync() => Task.FromResult("ok");
}

/// <summary>独立策略的第二组端点(策略间互不影响)。</summary>
public interface IRateInterfaceApi : IServiceApi
{
    [AllowAnonymous]
    [HttpGet("iface")]
    [EnableRateLimiting("test-iface")]
    Task<string> IfaceAsync();
}

public class RateInterfaceService : IRateInterfaceApi, IScopedService
{
    public Task<string> IfaceAsync() => Task.FromResult("ok");
}

/// <summary>未标注限流的对照端点。</summary>
public interface IRateFreeApi : IServiceApi
{
    [AllowAnonymous]
    [HttpGet("free")]
    Task<string> FreeAsync();
}

public class RateFreeService : IRateFreeApi, IScopedService
{
    public Task<string> FreeAsync() => Task.FromResult("ok");
}

/// <summary>
/// 契约端点限流黑盒测试:配置驱动策略(PermitLimit=2/60s/IP 分区),
/// [EnableRateLimiting] 方法级与接口级识别;未标注端点不受影响;429 统一信封。
/// </summary>
public class RateLimitTests : IAsyncLifetime
{
    private HttpClient _client = null!;
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["NexMesh:RateLimit:Policies:test-burst:PermitLimit"] = "2",
                    ["NexMesh:RateLimit:Policies:test-burst:WindowSeconds"] = "60",
                    ["NexMesh:RateLimit:Policies:test-burst:Partition"] = "Ip",
                    ["NexMesh:RateLimit:Policies:test-iface:PermitLimit"] = "1",
                    ["NexMesh:RateLimit:Policies:test-iface:WindowSeconds"] = "60",
                    ["NexMesh:RateLimit:Policies:test-iface:Partition"] = "Ip",
                }))
                .ConfigureServices((context, services) =>
                {
                    services.AddNexMesh();
                    services.AddNexMeshRateLimiter(context.Configuration);
                    services.AddNexMeshContracts(typeof(RateFixtureService).Assembly);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(e => e.MapNexMeshContracts(typeof(RateFixtureService).Assembly));
                }))
            .StartAsync();
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task 方法级标注_超限返回429统一信封()
    {
        var first = await _client.GetAsync("/api/ratefixture/burst");
        var second = await _client.GetAsync("/api/ratefixture/burst");
        var third = await _client.GetAsync("/api/ratefixture/burst");

        Assert.Equal(System.Net.HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, third.StatusCode);
        var body = await third.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":429", body);
        Assert.Contains("\"success\":false", body);
        // 中文经 Web 编码器转义(与框架信封一致),断言结构而非原文
    }

    [Fact]
    public async Task 不同策略各自独立生效()
    {
        var first = await _client.GetAsync("/api/rateinterface/iface");
        var second = await _client.GetAsync("/api/rateinterface/iface");

        Assert.Equal(System.Net.HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task 未标注端点不受限流影响()
    {
        for (var i = 0; i < 5; i++)
        {
            var response = await _client.GetAsync("/api/ratefree/free");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }
    }
}
