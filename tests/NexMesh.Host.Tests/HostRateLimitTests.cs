namespace NexMesh.Host.Tests;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexMesh;
using NexMesh.Core.DependencyInjection;
using Xunit;

public interface IHostRateApi : IServiceApi
{
    [AllowAnonymous]
    [HttpGet("hit")]
    [EnableRateLimiting("host-burst")]
    Task<string> HitAsync();
}

public class HostRateService : IHostRateApi, IScopedService
{
    public Task<string> HitAsync() => Task.FromResult("ok");
}

/// <summary>
/// 宿主管线限流集成:UseNexMeshService 内部应真实挂载 UseRateLimiter。
/// 修复过的回归缺口——此前管线插入曾静默丢失而单元测试全绿,仅端到端才发现;
/// 本用例把该端到端断言固化进测试套件。
/// </summary>
public class HostRateLimitTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["NexMesh:Contracts:AllowAnonymousByDefault"] = "true";
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexMesh:Db:ConnectionString"] = $"Data Source=hostrate-{Guid.NewGuid():N}.db",
            ["NexMesh:Db:AutoSyncStructure"] = "true",
            ["NexMesh:RateLimit:Policies:host-burst:PermitLimit"] = "2",
            ["NexMesh:RateLimit:Policies:host-burst:WindowSeconds"] = "60",
        });

        builder.AddNexMeshService(typeof(HostRateService).Assembly);
        builder.Services.AddNexMeshRateLimiter(builder.Configuration);
        _app = builder.Build();
        _app.UseNexMeshService(typeof(HostRateService).Assembly);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task UseNexMeshService管线_限流真实生效()
    {
        var first = await _client.GetAsync("/api/hostrate/hit");
        var second = await _client.GetAsync("/api/hostrate/hit");
        var third = await _client.GetAsync("/api/hostrate/hit");

        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        Assert.True(second.IsSuccessStatusCode, await second.Content.ReadAsStringAsync());
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, third.StatusCode);
    }
}
