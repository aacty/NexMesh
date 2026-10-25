namespace NexMesh.Core.Tests;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexMesh.Core.RateLimiting;
using Xunit;

/// <summary>未知限流策略的运行期行为:引用未注册策略名的端点 500(框架在宿主管线提供启动期告警,此处验证其针对的问题真实存在)。</summary>
public class RateLimitAuditTests : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices((context, services) =>
                {
                    services.AddNexMesh();
                    services.AddNexMeshRateLimiter(context.Configuration); // 无任何策略配置
                    services.AddNexMeshContracts(typeof(RateFixtureService).Assembly);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(e => e.MapNexMeshContracts(typeof(RateFixtureService).Assembly));
                }))
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task 引用未注册策略的端点_请求时明确报错()
    {
        // RateFixtureService 的端点引用 test-burst,但本 Host 未注册该策略;
        // AuditPolicies 在 Host 管线里对 WebApplication 调用——此处通过行为验证:该端点运行期 500
        var client = _host.GetTestClient();
        // TestServer 将未处理异常上抛给调用方——异常内容即审计要提前暴露的问题
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/api/ratefixture/burst"));
        Assert.Contains("test-burst", ex.Message);
    }
}
